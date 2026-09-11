using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Services.Notifications;

public sealed record NotificationResult(bool Success, int? StatusCode, string? Error, int Attempts, int ElapsedMs);

public interface INotificationDispatcher
{
    /// <param name="blocksJson">
    /// Provider-native rich content, verbatim, for the kinds that accept it. Slack incoming
    /// webhooks do; Teams and generic webhooks do not, and the caller is expected to have
    /// refused rather than let it be dropped here.
    /// </param>
    Task<NotificationResult> SendAsync(
        Guid channelId, string text, Guid? workflowRunId, CancellationToken ct, string? blocksJson = null);
}

public static class NotificationHttpClients
{
    public const string Name = "notifications";
}

// Sends a text message to a channel and records the attempt.
//
// Retries only on failures that could plausibly clear: a 5xx, a 429, or a
// transport error. A 400 from Slack means the payload is wrong and will be wrong
// three times; a 404 means the webhook was revoked. Retrying those turns a clear
// failure into a slow one.
public sealed class NotificationDispatcher : INotificationDispatcher
{
    private const int MaxAttempts = 3;
    private const int PreviewChars = 500;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        AppDbContext db, ISecretProtector protector, IUrlGuard urlGuard,
        IHttpClientFactory httpFactory, ILogger<NotificationDispatcher> logger)
    {
        _db = db;
        _protector = protector;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<NotificationResult> SendAsync(
        Guid channelId, string text, Guid? workflowRunId, CancellationToken ct, string? blocksJson = null)
    {
        var channel = await _db.NotificationChannels
            .FirstOrDefaultAsync(c => c.NotificationChannelId == channelId && c.IsActive, ct)
            ?? throw new Exceptions.NotFoundException("notification channel not found");

        if (!channel.Enabled)
            throw new Exceptions.ValidationException($"channel '{channel.Name}' is disabled");

        var url = _protector.Decrypt(channel.WebhookUrlEncrypted);
        if (string.IsNullOrWhiteSpace(url))
            // A rotated Data Protection keyring makes this unreadable. Say so — a
            // 500 would send an operator hunting for a network problem.
            throw new Exceptions.ValidationException(
                $"channel '{channel.Name}' has no readable webhook URL; re-enter it");

        var result = await PostWithRetryAsync(channel, url!, text, blocksJson, ct);

        _db.NotificationDeliveries.Add(new NotificationDelivery
        {
            NotificationDeliveryId = Guid.NewGuid(),
            NotificationChannelId = channelId,
            Preview = text.Length > PreviewChars ? text[..PreviewChars] : text,
            Success = result.Success,
            StatusCode = result.StatusCode,
            Error = result.Error is { Length: > 500 } e ? e[..500] : result.Error,
            Attempts = result.Attempts,
            ElapsedMs = result.ElapsedMs,
            SentAt = DateTime.UtcNow,
            WorkflowRunId = workflowRunId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        channel.Status = result.Success ? IntegrationEntity.StatusHealthy : IntegrationEntity.StatusUnreachable;
        channel.LastCheckError = result.Error;
        channel.LastCheckedAt = DateTime.UtcNow;
        channel.UpdatedAt = channel.LastCheckedAt.Value;

        await _db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<NotificationResult> PostWithRetryAsync(
        NotificationChannel channel, string url, string text, string? blocksJson, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? lastError = null;
        int? lastStatus = null;
        var attemptsMade = 0;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            attemptsMade = attempt;
            if (attempt > 1)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 2)), ct);

            try
            {
                _urlGuard.EnsureSafe(url, allowPrivate: channel.AllowPrivateNetwork);
            }
            catch (InvalidOperationException ex)
            {
                // A blocked target will not unblock on a retry.
                sw.Stop();
                return new NotificationResult(false, null, ex.Message, attempt, (int)sw.ElapsedMilliseconds);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(BuildPayload(channel.Kind, text, blocksJson), Encoding.UTF8, "application/json"),
            };
            ApplyHeaders(request, channel.HeadersJson);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            try
            {
                var client = _httpFactory.CreateClient(NotificationHttpClients.Name);
                using var response = await client.SendAsync(request, cts.Token);
                var code = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    sw.Stop();
                    return new NotificationResult(true, code, null, attempt, (int)sw.ElapsedMilliseconds);
                }

                // Slack/Teams put the actionable reason in the body
                // ("invalid_payload"); "HTTP 400" alone sends an operator to
                // check the network instead of the message.
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                lastStatus = code;
                lastError = string.IsNullOrWhiteSpace(errorBody)
                    ? $"HTTP {code}"
                    : $"HTTP {code}: {Truncate(errorBody, 200)}";

                // 4xx other than 429 is the caller's problem, not a transient one.
                if (code < 500 && code != 429) break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"timed out after {Timeout.TotalSeconds:0}s";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            _logger.LogWarning(
                "notifications.attempt_failed channel={Channel} attempt={Attempt} error={Error}",
                channel.Name, attempt, lastError);
        }

        sw.Stop();
        // attemptsMade, not MaxAttempts: a 400 breaks out after one attempt, and
        // the delivery record exists to say what actually happened.
        return new NotificationResult(false, lastStatus, lastError, attemptsMade, (int)sw.ElapsedMilliseconds);
    }

    private static string Truncate(string s, int max)
    {
        var flat = s.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max];
    }

    // Each service wants its own envelope for what is conceptually the same thing:
    // a line of text.
    internal static string BuildPayload(string kind, string text, string? blocksJson = null) => kind switch
    {
        NotificationChannel.KindSlack => SlackPayload(text, blocksJson),
        // Teams' legacy connector schema. `text` alone renders; the type/context
        // pair is what makes it a card rather than a rejected body.
        NotificationChannel.KindTeams => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["@type"] = "MessageCard",
            ["@context"] = "https://schema.org/extensions",
            ["text"] = text,
        }),
        // Generic: the receiver decides. `text` matches Slack's shape so a plain
        // webhook endpoint written for one usually works for the other.
        _ => JsonSerializer.Serialize(new { text }),
    };

    // Slack incoming webhooks accept `blocks` alongside `text`, and `text` stays as the
    // fallback Slack shows in the sidebar and on push. The blocks arrive as the caller's
    // provider-native JSON and are attached verbatim: re-typing Slack's block schema here
    // would only give it a second place to drift from.
    private static string SlackPayload(string text, string? blocksJson)
    {
        var body = new System.Text.Json.Nodes.JsonObject { ["text"] = text };
        if (string.IsNullOrWhiteSpace(blocksJson)) return body.ToJsonString();

        try
        {
            body["blocks"] = System.Text.Json.Nodes.JsonNode.Parse(blocksJson);
        }
        catch (JsonException)
        {
            // Sending the text alone would be the silent reduction the caller refused to
            // make; failing here surfaces as a delivery error naming the channel.
            throw new Exceptions.ValidationException("`blocks` is not valid JSON");
        }

        return body.ToJsonString();
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                request.Headers.TryAddWithoutValidation(p.Name, p.Value.GetString());
            }
        }
        catch (JsonException)
        {
            // Cosmetic; a broken blob must not stop the message going out.
        }
    }
}
