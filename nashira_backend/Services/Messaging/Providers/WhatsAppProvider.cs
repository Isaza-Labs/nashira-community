using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Messaging.Providers;

// WhatsApp Cloud API (Meta) channel. Verification is two-fold: the initial GET
// handshake echoes hub.challenge when hub.verify_token matches the configured
// token (stored in ExternalConfigJson.verify_token); event POSTs are signed with
// the app secret (HMAC-SHA256, header X-Hub-Signature-256). Outbound posts to the
// Graph API messages endpoint for the configured phone_number_id.
//
// Note: WhatsApp only allows free-form text inside the 24h customer-service
// window. Outside it, sends fail and land as a failed delivery (template
// messages are out of scope for v1).
public sealed class WhatsAppProvider : IMessagingProvider
{
    private const string DefaultGraphVersion = "v21.0";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WhatsAppProvider> _logger;

    public WhatsAppProvider(IHttpClientFactory httpFactory, ILogger<WhatsAppProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string Provider => MessagingChannel.ProviderWhatsApp;

    public Task<WebhookVerifyResult> VerifyAsync(
        MessagingChannel channel, MessagingHttpRequest request,
        string? decryptedSigningSecret, CancellationToken ct)
        => Task.FromResult(VerifyCore(channel, request, decryptedSigningSecret));

    private static WebhookVerifyResult VerifyCore(
        MessagingChannel channel, MessagingHttpRequest request, string? appSecret)
    {
        // GET subscription handshake.
        if (HttpMethods.IsGet(request.Method))
        {
            var mode = request.QueryValue("hub.mode");
            var token = request.QueryValue("hub.verify_token");
            var challenge = request.QueryValue("hub.challenge");
            var configured = ConfigStr(channel, "verify_token");
            if (mode == "subscribe" && challenge is not null
                && !string.IsNullOrEmpty(configured) && token is not null && FixedTimeEquals(token, configured))
                return WebhookVerifyResult.Challenge(challenge);
            return WebhookVerifyResult.Rejected(403, "verify token mismatch");
        }

        // POST event signature.
        if (string.IsNullOrEmpty(appSecret))
        {
            return channel.AllowUnsigned
                ? WebhookVerifyResult.Verified()
                : WebhookVerifyResult.Rejected(401, "no app secret configured; set one or enable allow_unsigned");
        }

        var sig = request.Header("X-Hub-Signature-256");
        if (string.IsNullOrEmpty(sig))
            return WebhookVerifyResult.Rejected(401, "missing X-Hub-Signature-256");

        var expected = "sha256=" + ComputeHmacHex(appSecret, request.Body);
        return FixedTimeEquals(expected, sig)
            ? WebhookVerifyResult.Verified()
            : WebhookVerifyResult.Rejected(401, "signature mismatch");
    }

    public InboundMessage? ParseInbound(MessagingChannel channel, MessagingHttpRequest request)
    {
        if (request.Body.Length == 0) return null;
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(request.Body); }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) continue;
                if (!value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;

                var phoneNumberId = value.TryGetProperty("metadata", out var meta)
                    ? Str(meta, "phone_number_id") : string.Empty;

                foreach (var m in messages.EnumerateArray())
                {
                    if (Str(m, "type") != "text") continue;
                    var from = Str(m, "from");
                    var id = Str(m, "id");
                    var text = m.TryGetProperty("text", out var t) ? Str(t, "body") : string.Empty;
                    if (from.Length == 0 || id.Length == 0) continue;

                    return new InboundMessage
                    {
                        ProviderEventId = id,
                        ExternalWorkspaceId = phoneNumberId,
                        ExternalUserId = from,
                        ExternalThreadId = from, // WhatsApp threads are 1:1 by wa_id
                        Text = text,
                        EventKind = "message",
                    };
                }
            }
        }
        return null;
    }

    public async Task SendAsync(
        MessagingChannel channel, string? decryptedBotToken,
        OutboundMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(decryptedBotToken))
            throw new MessagingSendException("whatsapp channel has no access token configured");
        var phoneNumberId = ConfigStr(channel, "phone_number_id");
        if (string.IsNullOrEmpty(phoneNumberId))
            throw new MessagingSendException("whatsapp channel has no phone_number_id in external_config");

        var version = ConfigStr(channel, "graph_version") is { Length: > 0 } v ? v : DefaultGraphVersion;
        var payload = JsonSerializer.Serialize(new
        {
            messaging_product = "whatsapp",
            to = message.ExternalThreadId,
            type = "text",
            text = new { body = Truncate(message.Text, 4096) }, // WhatsApp text body limit
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var http = _httpFactory.CreateClient(MessagingHttpClients.Name);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"https://graph.facebook.com/{version}/{phoneNumberId}/messages")
        {
            Content = content,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", decryptedBotToken);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            // A socket-level failure says nothing about the message; the next
            // attempt may well reach Meta.
            throw new MessagingSendException($"whatsapp send failed: {ex.Message}", transient: true, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MessagingSendException("whatsapp send timed out", transient: true, ex);
        }

        using (resp)
        {
            if (resp.IsSuccessStatusCode) return;

            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "messaging.whatsapp.send_failed channel={Channel} status={Status} body={Body}",
                channel.MessagingChannelId, (int)resp.StatusCode, Truncate(body, 300));

            var code = (int)resp.StatusCode;
            // A 4xx other than a rate limit is a bad payload, an expired 24h
            // window or a revoked token: identical on every retry.
            var transient = code >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
            throw new MessagingSendException($"whatsapp send failed: {code}", transient);
        }
    }

    private static string ComputeHmacHex(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    // ExternalConfigJson is free-form operator input, so a malformed blob must
    // read as "not configured" rather than throw out of the webhook path.
    private static string ConfigStr(MessagingChannel channel, string name)
    {
        if (string.IsNullOrWhiteSpace(channel.ExternalConfigJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(channel.ExternalConfigJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, name) : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
