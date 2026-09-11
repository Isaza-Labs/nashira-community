using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Ported 1:1 from FlowWeaver (Controllers/McpOAuthCallbackController.cs); only
// the route follows Nashira's /api/mcp/servers prefix.
//
// Public OAuth redirect target for MCP authorization-code flows. The
// authorization server bounces the admin's browser here with ?code=&state=.
// Anonymous by necessity (no session on the redirect), but CSRF-safe: the
// signed, time-limited `state` identifies the server and its nonce is
// matched against the value stashed at oauth/start. Always ends in a 302 back
// to the frontend.
[ApiController]
[Route("api/mcp/servers")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
public class McpOAuthCallbackController : ControllerBase
{
    private readonly IMcpOAuthFlowService _oauth;

    public McpOAuthCallbackController(IMcpOAuthFlowService oauth)
    {
        _oauth = oauth;
    }

    [HttpGet("{id:guid}/oauth/callback")]
    public async Task<IActionResult> Callback(
        Guid id,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct = default)
    {
        var redirectUriFallback = McpOAuthRedirect.CallbackUri(Request, id);
        var frontendUrl = await _oauth.HandleCallbackAsync(code, state, error, redirectUriFallback, ct);
        return Redirect(frontendUrl);
    }
}
