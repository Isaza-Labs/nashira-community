using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Messaging;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// The inbound edge for every messaging provider.
//
// Anonymous by necessity — Slack, Telegram, Meta and the Bot Framework have no
// Nashira credential to present. Authenticity is established per provider inside
// MessagingIngestService (HMAC, secret token, or a Bot Framework JWT), never by
// this controller, and never by trusting anything in the URL.
//
// GET exists because two providers use it as a handshake: WhatsApp echoes
// hub.challenge, and it doubles as a reachability check an admin can curl.
[ApiController]
[Route("api/messaging/webhooks")]
[AllowAnonymous]
[SkipAudit] // the ingest writes its own inbound audit row on every outcome
[EnableRateLimiting(RateLimitingConfiguration.MessagingWebhookIngest)]
public class MessagingWebhookController : ControllerBase
{
    private readonly IMessagingIngestService _ingest;

    public MessagingWebhookController(IMessagingIngestService ingest) => _ingest = ingest;

    [HttpGet("{provider}/{channelId:guid}")]
    [HttpPost("{provider}/{channelId:guid}")]
    public async Task<IActionResult> Receive(string provider, Guid channelId, CancellationToken ct)
    {
        // The raw bytes, verbatim: HMAC verification signs the body exactly as it
        // arrived, so re-serializing parsed JSON would break every signature.
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);

        var request = new MessagingHttpRequest
        {
            Method = Request.Method,
            Body = buffer.ToArray(),
            Headers = Request.Headers.ToDictionary(
                h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            Query = Request.Query.ToDictionary(
                q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase),
        };

        var outcome = await _ingest.ReceiveAsync(provider, channelId, request, ct);

        // A challenge is echoed verbatim — the provider compares bytes, so wrapping
        // it in a status envelope would fail the handshake.
        if (outcome.Raw)
            return new ContentResult
            {
                StatusCode = outcome.StatusCode,
                Content = outcome.Body ?? string.Empty,
                ContentType = outcome.ContentType,
            };

        return new ObjectResult(new { status = outcome.Body }) { StatusCode = outcome.StatusCode };
    }
}
