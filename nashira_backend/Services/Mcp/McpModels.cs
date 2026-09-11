using System.Text.Json;

namespace nashira_backend.Services.Mcp;

// A tool as reported by an MCP server's tools/list. Cached into McpTool rows and
// surfaced to the agent.
//
// ReadOnlyHint is the server's own claim about itself. Carried through so it can be
// shown and, on a server an admin has vouched for, spare the user a confirmation —
// never believed on its own.
public sealed record McpToolDescriptor(
    string Name, string? Title, string? Description, JsonElement InputSchema, bool ReadOnlyHint = false);

// Normalized result of a tools/call: text blocks concatenated, the optional
// structured payload, and the server's own error flag.
public sealed record McpCallResult(string Content, JsonElement? Structured, bool IsError);

// Everything needed to open a session to one server. Built by IMcpConnectionFactory,
// which decrypts the auth material and applies the SSRF guard.
public sealed record McpConnection(Uri Endpoint, Dictionary<string, string> Headers, bool TlsSkipVerify);

// Named clients for MCP traffic. The insecure variant accepts any TLS certificate
// (TlsSkipVerify) and mirrors the integration pair.
public static class McpHttpClients
{
    public const string Secure = "mcp";
    public const string Insecure = "mcp-insecure";
}
