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

    // The admin setting that lets plain `show` commands over SSH run without a
    // confirmation. Off by default: logging in to a device was confirmed for what it
    // is, and an installation has to decide that reading from one is routine.
    public const string AutoRunSshReadsKey = "Ssh:AutoRunReadCommands";

    // What counts as a plain read over SSH, and it is deliberately much narrower than
    // SshCommandPolicy's "read". That classifier only looks at how a command starts,
    // which is right for refusing destructive commands and wrong for waiving a
    // confirmation: `cat notes; rm -rf /` starts with a read verb. So here the whole
    // command has to be a `show` / `display` with ordinary arguments, optionally
    // piped into one of the CLI's own output filters. No `;`, `&`, backticks, `$`,
    // redirection or line breaks anywhere, and nothing but a filter after a pipe.
    private static readonly System.Text.RegularExpressions.Regex PlainShowCommand = new(
        @"^[ \t]*(show|display)\b[\w \t\-./:,]*(\|[ \t]*(include|exclude|begin|section|count|match|no-more)\b[\w \t\-./:,""]*)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly AppDbContext _db;
    private readonly IApiSpecIndex _specs;
    private readonly ILogger<ToolAutonomyResolver> _logger;
    private readonly Services.Settings.AppSettingsProvider? _settings;

    public ToolAutonomyResolver(AppDbContext db, IApiSpecIndex specs, ILogger<ToolAutonomyResolver> logger,
        Services.Settings.AppSettingsProvider? settings = null)
    {
        _db = db;
        _specs = specs;
        _logger = logger;
        _settings = settings;
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

            case "device_connect":
            {
                // Only where an admin has opted in, and only when every command in the
                // call is a plain `show`. One command that is anything else puts the
                // whole call back behind the confirmation.
                if (_settings is null || !_settings.GetBool(AutoRunSshReadsKey, false)) return false;
                var commands = Commands(args);
                if (commands is null || commands.Count == 0) return false;

                var readOnly = commands.All(IsPlainShowCommand);
                if (readOnly)
                    _logger.LogDebug(
                        "ai.autonomy.read_only tool=device_connect commands={Count}", commands.Count);
                return readOnly;
            }

            default:
                // Every other confirmed tool is confirmed because of what it is, not
                // because of what it was called with.
                return false;
        }
    }

    internal static bool IsPlainShowCommand(string command) =>
        command.Length is > 0 and <= 300 && PlainShowCommand.IsMatch(command);

    // The `commands` array as strings, or null when it is missing or holds anything
    // that is not a string — a call this cannot read is not one it can wave through.
    private static List<string>? Commands(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("commands", out var c)
            || c.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var el in c.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) return null;
            list.Add(el.GetString() ?? string.Empty);
        }
        return list;
    }

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;
}
