using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Browses the cached MCP tool catalog. Read-only → autonomous.
//
// Reads the cache rather than the servers themselves: a live tools/list per server
// would turn one agent question into N network round trips, and the cache is what
// mcp_call validates against anyway, so browsing it shows exactly what is callable.
public sealed class ListMcpToolsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "server":{"type":"string","description":"Server name. Omit to list tools across every registered server."},
          "keyword":{"type":"string","description":"Substring match on tool name, title or description"},
          "limit":{"type":"integer","minimum":1,"maximum":200,"default":50}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListMcpToolsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_mcp_tools";
    public string Description =>
        "Lists the tools available on the registered MCP servers, with their argument schemas. " +
        "Use this before mcp_call to find a tool's exact name and required arguments.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var serverName = Str(args, "server");
        var keyword = Str(args, "keyword");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv)
            ? Math.Clamp(lv, 1, 200) : 50;

        var servers = await _db.McpServers.AsNoTracking()
            .Where(s => s.IsActive && s.Enabled)
            .ToDictionaryAsync(s => s.McpServerId, s => s.Name, ct);
        if (servers.Count == 0)
            return JsonSerializer.SerializeToElement(new { tools = Array.Empty<object>(), count = 0 });

        var wanted = servers.Keys.ToList();
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            wanted = servers
                .Where(kv => kv.Value.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
            if (wanted.Count == 0)
                return Err($"no enabled MCP server named '{serverName}'");
        }

        // Retired tools are excluded: they cannot be called, so offering them to the
        // agent only produces a failed attempt.
        var q = _db.McpTools.AsNoTracking().Where(t =>
            t.IsActive && t.Enabled && t.DisappearedAt == null && wanted.Contains(t.McpServerId));

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword}%";
            q = q.Where(t =>
                EF.Functions.ILike(t.Name, pattern)
                || (t.Title != null && EF.Functions.ILike(t.Title, pattern))
                || (t.Description != null && EF.Functions.ILike(t.Description, pattern)));
        }

        var rows = await q.OrderBy(t => t.Name).Take(limit).ToListAsync(ct);

        var tools = rows.Select(t => new
        {
            server = servers[t.McpServerId],
            name = t.Name,
            title = t.Title,
            description = t.Description,
            input_schema = ParseSchema(t.InputSchemaJson),
        });

        return JsonSerializer.SerializeToElement(new { tools, count = rows.Count });
    }

    private static JsonElement ParseSchema(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
