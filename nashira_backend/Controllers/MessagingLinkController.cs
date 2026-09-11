using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Messaging;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

public class MessagingLinkPreviewResponse
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = string.Empty;
    [JsonPropertyName("channel_name")] public string ChannelName { get; set; } = string.Empty;
    [JsonPropertyName("external_user_id")] public string ExternalUserId { get; set; } = string.Empty;
    [JsonPropertyName("expires_at")] public DateTime ExpiresAt { get; set; }
}

public class MessagingLinkConfirmResponse
{
    [JsonPropertyName("linked")] public bool Linked { get; set; }
    [JsonPropertyName("messaging_identity_link_id")] public Guid Id { get; set; }
}

// Self-service account linking, the user-facing half.
//
// Authenticated on purpose, at any role: the whole point is to bind the external
// identity to *whoever is signed in right now*, so the caller's own session is the
// evidence. The token in the URL says which external identity is being claimed; it
// never says who the user is.
[ApiController]
[Route("api/messaging/link")]
[Authorize]
[EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
public class MessagingLinkController : ControllerBase
{
    private readonly IMessagingLinkService _links;

    public MessagingLinkController(IMessagingLinkService links) => _links = links;

    // What the user is about to link, shown before they commit. A wrong or expired
    // token 404s exactly like a missing one, so this cannot enumerate live tokens.
    [HttpGet("{token}")]
    public async Task<ActionResult<MessagingLinkPreviewResponse>> Preview(
        string token, CancellationToken ct)
    {
        var p = await _links.PreviewAsync(token, ct);
        return new MessagingLinkPreviewResponse
        {
            Provider = p.Provider,
            ChannelName = p.ChannelName,
            ExternalUserId = p.ExternalUserId,
            ExpiresAt = p.ExpiresAt,
        };
    }

    [HttpPost("{token}/confirm")]
    public async Task<ActionResult<MessagingLinkConfirmResponse>> Confirm(
        string token, CancellationToken ct)
    {
        var id = await _links.ConfirmAsync(token, ct);
        return new MessagingLinkConfirmResponse { Linked = true, Id = id };
    }
}
