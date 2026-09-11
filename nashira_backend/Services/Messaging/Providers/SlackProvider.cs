using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Messaging.Providers;

// Slack Events API channel. Inbound is verified with the app signing secret
// (HMAC-SHA256 over "v0:{timestamp}:{body}", header X-Slack-Signature) plus a
// 5-minute anti-replay window on X-Slack-Request-Timestamp, and answers the
// one-time url_verification challenge. Outbound posts chat.postMessage with the
// bot token, threading the reply.
//
// ExternalThreadId encodes where to reply: "channel:thread_ts" for messages
// inside a thread (the reply stays in-thread), or just "channel" for a
// top-level message / DM (the reply posts to the main flow, not nested).
public sealed class SlackProvider : IMessagingProvider
{
    private const int ReplayWindowSeconds = 300;

    // Slack's chat.postMessage text field hard limit.
    private const int MaxMessageChars = 40000;


    // Message subtypes to ignore: bot echoes, edits/deletes, and replies that
    // would loop or duplicate. Real user subtypes (file_share, thread_broadcast)
    // are intentionally NOT here so their text is still processed.
    private static readonly HashSet<string> DroppedSubtypes = new(StringComparer.Ordinal)
    {
        "bot_message", "message_changed", "message_deleted", "message_replied",
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SlackProvider> _logger;

    public SlackProvider(IHttpClientFactory httpFactory, ILogger<SlackProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string Provider => MessagingChannel.ProviderSlack;

    public Task<WebhookVerifyResult> VerifyAsync(
        MessagingChannel channel, MessagingHttpRequest request,
        string? decryptedSigningSecret, CancellationToken ct)
        => Task.FromResult(VerifyCore(channel, request, decryptedSigningSecret));

    private static WebhookVerifyResult VerifyCore(
        MessagingChannel channel, MessagingHttpRequest request, string? secret)
    {
        if (!HttpMethods.IsPost(request.Method))
            return WebhookVerifyResult.Verified();

        if (string.IsNullOrEmpty(secret))
        {
            return channel.AllowUnsigned
                ? WebhookVerifyResult.Verified()
                : WebhookVerifyResult.Rejected(401, "no signing secret configured; set one or enable allow_unsigned");
        }

        var ts = request.Header("X-Slack-Request-Timestamp");
        var sig = request.Header("X-Slack-Signature");
        if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(sig))
            return WebhookVerifyResult.Rejected(401, "missing slack signature headers");

        if (!WithinReplayWindow(ts))
            return WebhookVerifyResult.Rejected(401, "stale request timestamp");

        var body = Encoding.UTF8.GetString(request.Body);
        var expected = "v0=" + ComputeHmacHex(secret, $"v0:{ts}:{body}");
        if (!FixedTimeEquals(expected, sig))
            return WebhookVerifyResult.Rejected(401, "signature mismatch");

        // Authentic — answer the one-time URL verification handshake if present.
        if (TryGetChallenge(request.Body, out var challenge))
            return WebhookVerifyResult.Challenge(challenge);

        return WebhookVerifyResult.Verified();
    }

    public InboundMessage? ParseInbound(MessagingChannel channel, MessagingHttpRequest request)
    {
        if (request.Body.Length == 0) return null;
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(request.Body); }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (!Str(root, "type").Equals("event_callback", StringComparison.Ordinal)) return null;
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) return null;

        var evType = Str(ev, "type");
        if (evType != "message" && evType != "app_mention") return null;

        // Skip the bot's own messages and bot/system/edit subtypes to avoid
        // loops, while keeping genuine user subtypes (file_share, …).
        if (ev.TryGetProperty("bot_id", out _)) return null;
        if (ev.TryGetProperty("subtype", out var sub) && sub.ValueKind == JsonValueKind.String
            && DroppedSubtypes.Contains(sub.GetString()!))
            return null;

        var user = Str(ev, "user");
        var channelId = Str(ev, "channel");
        var ts = Str(ev, "ts");
        // Only treat the message as threaded when Slack actually provides a
        // thread_ts. A top-level message / DM has none.
        var threadTs = ev.TryGetProperty("thread_ts", out var tt) && tt.ValueKind == JsonValueKind.String
            ? tt.GetString()
            : null;
        var text = Str(ev, "text");
        var eventId = Str(root, "event_id");
        if (user.Length == 0 || channelId.Length == 0 || ts.Length == 0) return null;

        return new InboundMessage
        {
            ProviderEventId = eventId.Length > 0 ? eventId : $"{channelId}:{ts}",
            ExternalWorkspaceId = Str(root, "team_id"),
            ExternalUserId = user,
            // In a thread → "channel:thread_ts" (continuity + reply in-thread).
            // Top-level / DM → just the channel, so replies land in the main flow
            // (not nested) and the whole DM is one conversation.
            ExternalThreadId = threadTs is null ? channelId : $"{channelId}:{threadTs}",
            Text = text,
            EventKind = evType,
        };
    }

    public async Task SendAsync(
        MessagingChannel channel, string? decryptedBotToken,
        OutboundMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(decryptedBotToken))
            // Not transient: a missing token is a configuration error that three
            // attempts will reproduce identically.
            throw new MessagingSendException("slack channel has no bot token configured");

        var (channelId, threadTs) = SplitThread(message.ExternalThreadId);
        var text = Truncate(message.Text, MaxMessageChars);
        // Thread the reply only when the source message was in a thread; for a
        // top-level message / DM, omit thread_ts so it posts to the main flow.
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["channel"] = channelId,
            ["text"] = text,
        };
        if (threadTs is not null) body["thread_ts"] = threadTs;

        // Rich content travels as provider-native JSON the caller supplies verbatim:
        // re-typing Slack's block schema here would only give it a second place to drift
        // from. `text` stays as the fallback Slack shows in the sidebar and on push.
        if (!string.IsNullOrWhiteSpace(message.BlocksJson))
        {
            try
            {
                body["blocks"] = System.Text.Json.Nodes.JsonNode.Parse(message.BlocksJson);
            }
            catch (JsonException ex)
            {
                // Not transient: the same malformed payload fails identically three times.
                throw new MessagingSendException($"slack blocks are not valid JSON: {ex.Message}");
            }
        }

        var payload = body.ToJsonString();

        HttpStatusCode status;
        string bodyText;
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var http = _httpFactory.CreateClient(MessagingHttpClients.Name);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage")
            {
                Content = content,
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", decryptedBotToken);

            using var resp = await http.SendAsync(req, ct);
            status = resp.StatusCode;
            bodyText = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MessagingSendException($"slack chat.postMessage transport error: {ex.Message}", transient: true, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The client's own timeout, not the caller giving up — worth a retry.
            throw new MessagingSendException("slack chat.postMessage timed out", transient: true, ex);
        }

        var code = (int)status;
        // Slack returns HTTP 200 with {ok:false,error:...} on logical failures.
        var ok = code is >= 200 and < 300 && TryGetOk(bodyText);
        if (ok) return;

        _logger.LogWarning(
            "messaging.slack.send_failed channel={Channel} status={Status} body={Body}",
            channel.MessagingChannelId, code, Truncate(bodyText, 300));

        // A 5xx or a 429 can clear; any other 4xx (invalid_auth, channel_not_found)
        // and an ok:false on a 200 will fail the same way every time.
        var transient = code >= 500 || code == 429;
        throw new MessagingSendException($"slack chat.postMessage failed: {code}", transient);
    }

    private static (string channelId, string? threadTs) SplitThread(string composite)
    {
        var idx = composite.IndexOf(':');
        return idx < 0 ? (composite, null) : (composite[..idx], composite[(idx + 1)..]);
    }

    private static bool WithinReplayWindow(string ts)
    {
        if (!long.TryParse(ts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sent))
            return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Math.Abs(now - sent) <= ReplayWindowSeconds;
    }

    private static bool TryGetChallenge(byte[] body, out string challenge)
    {
        challenge = string.Empty;
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(body);
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() == "url_verification"
                && root.TryGetProperty("challenge", out var c)
                && c.ValueKind == JsonValueKind.String)
            {
                challenge = c.GetString() ?? string.Empty;
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static bool TryGetOk(string body)
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(body);
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("ok", out var ok)
                   && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static string ComputeHmacHex(string secret, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
