using System.Text.Json;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Services.Ai.Tools;

// Singleton registry of tool metadata. Handler instances are Scoped — the
// registry stores only the metadata + the handler Type so the dispatcher can
// resolve a fresh instance per request.
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(ILogger<ToolRegistry> logger) => _logger = logger;

    public void Register(IToolHandler handler)
    {
        // Fail fast at boot: a duplicate name would silently shadow the earlier
        // handler while keeping the name's classification — the worst kind of drift.
        if (_tools.TryGetValue(handler.Name, out var existing))
            throw new InvalidOperationException(
                $"tool name collision: '{handler.Name}' is declared by both {existing.HandlerType.Name} and {handler.GetType().Name}");

        _tools[handler.Name] = new ToolEntry(handler.Name, handler.Description, handler.ParametersSchema, handler.GetType());
        _logger.LogDebug("ai.tool.registry.register tool={Tool} handler={Handler}", handler.Name, handler.GetType().Name);
    }

    public ToolEntry? Get(string name) => _tools.TryGetValue(name, out var e) ? e : null;

    public IReadOnlyCollection<ToolEntry> All => _tools.Values;

    public List<ToolDefinition> ToDefinitions(IEnumerable<string>? allowedNames = null)
    {
        var source = allowedNames is not null
            ? _tools.Values.Where(t => allowedNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
            : _tools.Values;
        return source.Select(t => new ToolDefinition
        {
            Name = t.Name,
            Description = t.Description,
            ParametersSchema = t.Schema,
        }).ToList();
    }
}

public sealed record ToolEntry(string Name, string Description, JsonElement Schema, Type HandlerType);
