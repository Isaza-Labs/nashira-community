using Microsoft.Extensions.Primitives;

namespace nashira_backend.Services.Mcp;

// Builds the public redirect_uri for the MCP OAuth flow. Behind the frontend's
// server-side proxy the request's own Host is the internal Docker name
// (backend:8080), which the admin's browser cannot reach and Google's OAuth
// policy rejects (http on a non-localhost host). The proxy forwards the
// browser-facing origin in X-Forwarded-Host/Proto; prefer those, fall back to
// the request itself for direct calls against the published port.
//
// Trusting the header unauthenticated is fine at this altitude: oauth/start
// requires an Admin JWT, and on the anonymous callback the value only feeds the
// redirect_uri fallback for the code exchange — a wrong one makes the exchange
// fail, it cannot redirect the browser anywhere (that URL comes from config).
public static class McpOAuthRedirect
{
    public static string CallbackUri(HttpRequest request, Guid serverId)
    {
        var scheme = First(request.Headers["X-Forwarded-Proto"]) ?? request.Scheme;
        var host = First(request.Headers["X-Forwarded-Host"]) ?? request.Host.Value;
        return $"{scheme}://{host}/api/mcp/servers/{serverId}/oauth/callback";
    }

    // A chain of proxies comma-joins values; the first hop is the browser-facing one.
    private static string? First(StringValues values)
    {
        var raw = values.Count > 0 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var first = raw.Split(',')[0].Trim();
        return first.Length > 0 ? first : null;
    }
}
