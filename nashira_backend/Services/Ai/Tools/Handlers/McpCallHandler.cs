using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Mcp;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Invokes a tool on a registered MCP server.
//
// Classified as an execute/confirm tool rather than autonomous: an MCP server is an
// arbitrary external program, and neither Nashira nor the model can know whether a
// given tool reads or writes. Treating the whole surface as state-changing is the
// only safe default when the blast radius is defined by someone else's server.
public sealed class McpCallHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "server":{"type":"string","description":"Registered MCP server name"},
          "tool":{"type":"string","description":"Tool name as reported by list_mcp_tools"},
          "arguments":{"type":"object","description":"Arguments matching the tool's input schema"}
        },"required":["server","tool"],"additionalProperties":false}
        """).RootElement.Clone();

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMcpServerService _service;
    private readonly ILogger<McpCallHandler> _logger;

    public McpCallHandler(
        AppDbContext db, ICurrentUser user, IMcpServerService service, ILogger<McpCallHandler> logger)
    {
        _db = db;
        _user = user;
        _service = service;
        _logger = logger;
    }

    public string Name => "mcp_call";
    public string Description =>
        "Calls a tool on a registered MCP server and returns its output. Run list_mcp_tools " +
        "first to get the server name, the tool name and the argument schema.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var serverName = Str(args, "server")?.Trim();
        if (string.IsNullOrWhiteSpace(serverName)) return Err("server is required");

        var toolName = Str(args, "tool")?.Trim();
        if (string.IsNullOrWhiteSpace(toolName)) return Err("tool is required");

        var arguments = args.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : EmptyArgs;

        var server = await _db.McpServers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsActive && s.Name == serverName, ct);
        if (server is null) return Err($"no MCP server named '{serverName}' is registered");

        try
        {
            var result = await _service.CallAsync(server.McpServerId, toolName!, arguments, ct);

            // The server's own error flag is surfaced as a normal result, not an
            // exception: "the tool ran and reported a problem" is information the
            // model should act on, and it is different from "the call never happened".
            return JsonSerializer.SerializeToElement(new
            {
                server = server.Name,
                tool = toolName,
                is_error = result.IsError,
                content = result.Content,
                structured = result.Structured,
            });
        }
        catch (DomainException ex)
        {
            // Catalog-level refusals (unknown/disabled/retired tool) are the caller's
            // problem to fix, so they come back as a message rather than a 500.
            return Err(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcp.call.failed server={Server} tool={Tool}", serverName, toolName);
            return Err($"MCP call failed: {ex.Message}");
        }
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
