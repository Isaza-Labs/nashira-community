using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Services.Mcp;

public sealed record McpSyncResult(int Discovered, int Created, int Updated, int Disappeared, int Reappeared);

public interface IMcpServerService
{
    Task<McpSyncResult> SyncToolsAsync(Guid serverId, CancellationToken ct);
    Task<IntegrationHealthLike> CheckAsync(Guid serverId, CancellationToken ct);
    Task<McpCallResult> CallAsync(Guid serverId, string toolName, JsonElement arguments, CancellationToken ct);
}

// Health shape shared with integrations, so the UI renders one status widget.
public sealed record IntegrationHealthLike(string Status, int ElapsedMs, string? Error, int ToolCount);

public sealed class McpServerService : IMcpServerService
{
    private readonly AppDbContext _db;
    private readonly IMcpClient _client;
    private readonly ILogger<McpServerService> _logger;

    public McpServerService(AppDbContext db, IMcpClient client, ILogger<McpServerService> logger)
    {
        _db = db;
        _client = client;
        _logger = logger;
    }

    // Reconciles the cached McpTool rows with the server's live tools/list.
    //
    // A tool that stops being advertised is marked DisappearedAt rather than
    // deleted: a workflow may still reference it, and "the server no longer offers
    // this tool" is a diagnosable failure where a dangling id is not. A tool that
    // comes back clears the mark and keeps its admin Enabled flag.
    public async Task<McpSyncResult> SyncToolsAsync(Guid serverId, CancellationToken ct)
    {
        var server = await FindAsync(serverId, ct);

        IReadOnlyList<McpToolDescriptor> discovered;
        try
        {
            discovered = await _client.ListToolsAsync(server, ct);
        }
        catch (Exception ex)
        {
            await MarkAsync(server, IntegrationEntity.StatusUnreachable, ex.Message, ct);
            throw new ValidationException($"could not list tools on '{server.Name}': {ex.Message}");
        }

        var existing = await _db.McpTools.Where(t => t.McpServerId == serverId).ToListAsync(ct);
        var byName = existing.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        int created = 0, updated = 0, reappeared = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in discovered)
        {
            if (string.IsNullOrWhiteSpace(tool.Name)) continue;
            seen.Add(tool.Name);
            var schema = tool.InputSchema.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : tool.InputSchema.GetRawText();

            if (byName.TryGetValue(tool.Name, out var row))
            {
                var changed = row.Title != tool.Title || row.Description != tool.Description
                              || row.InputSchemaJson != schema || row.ReadOnlyHint != tool.ReadOnlyHint
                              || !row.IsActive;
                if (row.DisappearedAt is not null) reappeared++;

                row.Title = tool.Title;
                row.Description = tool.Description;
                row.InputSchemaJson = schema;
                // Re-read every sync: a tool that stops calling itself read-only has
                // changed in a way that matters more than its description changing.
                row.ReadOnlyHint = tool.ReadOnlyHint;
                row.DisappearedAt = null;
                row.IsActive = true;
                row.UpdatedAt = now;
                if (changed) updated++;
            }
            else
            {
                _db.McpTools.Add(new McpTool
                {
                    McpToolId = Guid.NewGuid(),
                    McpServerId = serverId,
                    Name = tool.Name,
                    Title = tool.Title,
                    Description = tool.Description,
                    InputSchemaJson = schema,
                    ReadOnlyHint = tool.ReadOnlyHint,
                    Enabled = true,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                created++;
            }
        }

        var disappeared = 0;
        foreach (var row in existing)
        {
            if (seen.Contains(row.Name) || row.DisappearedAt is not null) continue;
            row.DisappearedAt = now;
            row.UpdatedAt = now;
            disappeared++;
        }

        server.ToolCount = seen.Count;
        server.ToolsSyncedAt = now;
        server.Status = IntegrationEntity.StatusHealthy;
        server.LastCheckError = null;
        server.LastCheckedAt = now;
        server.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "mcp.tools.synced server={Server} discovered={Discovered} created={Created} updated={Updated} gone={Gone}",
            server.Name, seen.Count, created, updated, disappeared);

        return new McpSyncResult(seen.Count, created, updated, disappeared, reappeared);
    }

    // Connectivity probe. A successful tools/list is the only honest check for an
    // MCP server: the protocol has no ping, and a bare TCP connect would go green
    // against anything listening on the port.
    public async Task<IntegrationHealthLike> CheckAsync(Guid serverId, CancellationToken ct)
    {
        var server = await FindAsync(serverId, ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var tools = await _client.ListToolsAsync(server, ct);
            sw.Stop();
            await MarkAsync(server, IntegrationEntity.StatusHealthy, null, ct);
            return new IntegrationHealthLike(
                IntegrationEntity.StatusHealthy, (int)sw.ElapsedMilliseconds, null, tools.Count);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await MarkAsync(server, IntegrationEntity.StatusUnreachable, ex.Message, ct);
            return new IntegrationHealthLike(
                IntegrationEntity.StatusUnreachable, (int)sw.ElapsedMilliseconds, ex.Message, 0);
        }
    }

    public async Task<McpCallResult> CallAsync(
        Guid serverId, string toolName, JsonElement arguments, CancellationToken ct)
    {
        var server = await FindAsync(serverId, ct);
        if (!server.Enabled)
            throw new ValidationException($"MCP server '{server.Name}' is disabled");

        // The cached row is the authority on whether a tool may be called: it is
        // where an admin's Enabled=false lives, and a server would happily run a
        // tool the operator has withdrawn.
        var tool = await _db.McpTools.AsNoTracking().FirstOrDefaultAsync(
            t => t.McpServerId == serverId && t.Name == toolName && t.IsActive, ct);
        if (tool is null)
            throw new NotFoundException($"tool '{toolName}' is not in the catalog for '{server.Name}' — sync it first");
        if (!tool.Enabled)
            throw new ValidationException($"tool '{toolName}' is disabled on '{server.Name}'");
        if (tool.DisappearedAt is not null)
            throw new ValidationException(
                $"tool '{toolName}' is no longer offered by '{server.Name}' (last seen {tool.DisappearedAt:u})");

        var result = await _client.CallToolAsync(server, toolName, arguments, ct);

        // tools/list is the health probe, and on a server that authenticates upstream
        // once at startup it keeps answering long after every actual call has started
        // failing — the badge says healthy while nothing works. A call that comes back
        // reporting an expired session is the only evidence of that, so it is recorded
        // as the server's state rather than being left as one tool's bad day.
        if (result.IsError && LooksLikeSessionFailure(result))
        {
            var detail = Describe(result);
            _logger.LogWarning(
                "mcp.call.session_expired server={Server} tool={Tool} detail={Detail}",
                server.Name, toolName, detail);
            await MarkAsync(server, IntegrationEntity.StatusDegraded,
                $"a tool call failed to authenticate upstream: {detail}", ct);
        }

        return result;
    }

    // Deliberately narrow. An MCP server reports its tool's own failures through the
    // same channel, and a bad argument must not degrade the whole server — only the
    // shapes that mean "this server's own upstream session is gone" count.
    private static readonly string[] SessionFailureMarkers =
    [
        "session is not logged in", "session expired", "session has expired", "not authenticated",
        "authentication failed", "unauthorized", "401",
    ];

    private static bool LooksLikeSessionFailure(McpCallResult result)
    {
        var text = Describe(result);
        return SessionFailureMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(McpCallResult result)
    {
        var text = result.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) && result.Structured is { } s
            && s.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            text = s.GetRawText();
        return text.Length > 300 ? text[..300] : text;
    }

    private async Task<McpServer> FindAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.McpServers.FirstOrDefaultAsync(s => s.McpServerId == id && s.IsActive, ct);
        if (row is null) throw new NotFoundException("mcp server not found");
        return row;
    }

    private async Task MarkAsync(McpServer server, string status, string? error, CancellationToken ct)
    {
        server.Status = status;
        // Bound the stored message: some transports surface a whole HTML error page.
        server.LastCheckError = error is { Length: > 500 } ? error[..500] : error;
        server.LastCheckedAt = DateTime.UtcNow;
        server.UpdatedAt = server.LastCheckedAt.Value;
        await _db.SaveChangesAsync(ct);
    }
}
