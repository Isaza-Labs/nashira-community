using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Specs;

namespace nashira_backend.Services.Ai.Permissions;

public interface IToolAutonomyResolver
{
    // True when this specific call reads and changes nothing, so the confirmation gate
    // has nothing to protect.
    Task<bool> IsReadOnlyCallAsync(string toolName, JsonElement args, CancellationToken ct);
}

// Decides whether a call that is normally confirmed can run on its own.
//
// The static tier is per TOOL, and two of them cover both reads and writes:
// `execute_operation` is every REST verb, `mcp_call` is every tool on every server. So
// asking NetBox how many devices it has required the same approval as deleting one,
// and a conversation spent reading anything at all was a wall of confirmations. People
// then approve without reading, which is the exact opposite of what a confirmation gate
// is for — the friction was not just annoying, it was corrosive.
//
// Two sources decide, and they are not equally trustworthy:
//
//   * The OpenAPI spec, for execute_operation. The method comes from a document an
//     administrator uploaded to this installation. A GET is a read.
//   * The server's `readOnlyHint`, for mcp_call. This is the MCP server describing
//     itself, and the specification says in as many words that a client must not make
//     tool-use decisions on annotations from a server it does not trust. So it counts
//     only where an admin has set TrustToolHints on that server.
public sealed class ToolAutonomyResolver : IToolAutonomyResolver
{
    private static readonly HashSet<string> ReadMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD" };

    private readonly AppDbContext _db;
    private readonly IApiSpecIndex _specs;
    private readonly ILogger<ToolAutonomyResolver> _logger;

    public ToolAutonomyResolver(AppDbContext db, IApiSpecIndex specs, ILogger<ToolAutonomyResolver> logger)
    {
        _db = db;
        _specs = specs;
        _logger = logger;
    }

    public async Task<bool> IsReadOnlyCallAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        switch (toolName)
        {
            case "execute_operation":
            {
                if (Str(args, "operation_id") is not { Length: > 0 } operationId) return false;
                await _specs.EnsureLoadedAsync(ct);
                var op = _specs.GetByOperationId(operationId);
                if (op is null) return false;

                var readOnly = ReadMethods.Contains(op.Method);
                if (readOnly)
                    _logger.LogDebug(
                        "ai.autonomy.read_only tool=execute_operation operation={Operation} method={Method}",
                        operationId, op.Method);
                return readOnly;
            }

            case "mcp_call":
            {
                if (Str(args, "server") is not { Length: > 0 } serverName) return false;
                if (Str(args, "tool") is not { Length: > 0 } tool) return false;

                var server = await _db.McpServers.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.IsActive && s.Name == serverName, ct);
                if (server is null || !server.TrustToolHints) return false;

                var readOnly = await _db.McpTools.AsNoTracking().AnyAsync(
                    t => t.McpServerId == server.McpServerId && t.Name == tool
                         && t.IsActive && t.DisappearedAt == null && t.ReadOnlyHint, ct);
                if (readOnly)
                    _logger.LogDebug(
                        "ai.autonomy.read_only tool=mcp_call server={Server} mcp_tool={McpTool}",
                        serverName, tool);
                return readOnly;
            }

            default:
                // Every other confirmed tool is confirmed because of what it is, not
                // because of what it was called with.
                return false;
        }
    }

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;
}
