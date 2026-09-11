using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Seed;
using YamlDotNet.RepresentationModel;

namespace nashira_backend.Services.Ai.Specs;

// Singleton index of ApiOperation records parsed from ai_api_specs.
// DB access goes through IServiceScopeFactory so the scoped AppDbContext can be
// resolved. The parser is lenient: missing fields become empty strings rather
// than throwing, so one bad spec can't poison a tenant. A missing operationId is
// synthesized as "<api>:<method>_<path>".
//
// Shipped specs of a capability this deployment does not run are left out of the index
// (ModuleContentCatalog), including rows an earlier all-enabled deployment seeded: the
// filter is on reading, so nothing is deleted and re-enabling the capability brings the
// same rows back. Specs an admin wrote are never filtered — nothing classifies them.
public sealed class YamlSpecIndex : IApiSpecIndex
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "head", "options", "trace",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModuleSelection _modules;
    private readonly ILogger<YamlSpecIndex> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SpecIndexSnapshot _index = SpecIndexSnapshot.Empty;
    private volatile bool _loaded;

    public YamlSpecIndex(
        IServiceScopeFactory scopeFactory, ModuleSelection modules, ILogger<YamlSpecIndex> logger)
    {
        _scopeFactory = scopeFactory;
        _modules = modules;
        _logger = logger;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (!_loaded) await ReloadAsync(ct);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var stored = await db.AiApiSpecs.AsNoTracking()
                .Where(s => s.IsActive)
                .Select(s => new { s.Api, s.Content })
                .ToListAsync(ct);

            var rows = stored
                .Where(row => ModuleContentCatalog.IsSpecAvailable(row.Api, _modules))
                .ToList();
            if (rows.Count < stored.Count)
                _logger.LogInformation(
                    "ai.spec.index_filtered specs={Indexed} of={Stored} — the rest describe "
                    + "capabilities this deployment does not run", rows.Count, stored.Count);

            var ops = new List<ApiOperation>();
            foreach (var row in rows)
            {
                try
                {
                    ops.AddRange(ParseOperations(row.Api, row.Content).Where(operation =>
                        ModuleContentCatalog.IsOperationAvailable(
                            row.Api, operation.OperationId, _modules)));
                }
                catch (Exception ex) { _logger.LogWarning(ex, "ai.spec.parse_failed api={Api}", row.Api); }
            }

            _index = new SpecIndexSnapshot
            {
                Operations = ops,
                ById = ops.GroupBy(o => o.OperationId, StringComparer.Ordinal)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            };
            _loaded = true;
            _logger.LogInformation("ai.spec.indexed operations={Count} specs={Rows}", ops.Count, rows.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ApiOperation> All() => _index.Operations;

    public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null)
    {
        IEnumerable<ApiOperation> query = _index.Operations;
        if (!string.IsNullOrWhiteSpace(api)) query = query.Where(o => o.Api.Equals(api, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(method)) query = query.Where(o => o.Method.Equals(method, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(o =>
                o.OperationId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || o.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || o.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || o.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        return query.ToList();
    }

    public ApiOperation? GetByOperationId(string operationId) =>
        _index.ById.TryGetValue(operationId, out var op) ? op : null;

    // The scoped lookup walks the operation list rather than the id map, because the id map
    // is exactly what loses the second of two specs sharing an id: it is built with
    // `GroupBy(OperationId).First()`, so the loser is not merely lower-priority, it is
    // absent. Searching the list is the only way to find it.
    public ApiOperation? GetByOperationId(string operationId, string? api) =>
        string.IsNullOrWhiteSpace(api)
            ? GetByOperationId(operationId)
            : _index.Operations.FirstOrDefault(o =>
                string.Equals(o.OperationId, operationId, StringComparison.Ordinal)
                && string.Equals(o.Api, api, StringComparison.OrdinalIgnoreCase));

    // Returns the same OpenAPI document with disabled operations removed. The api id,
    // metadata and component schemas stay intact, so callers of get_spec cannot bypass
    // the runtime index and recover operations that Chat was not meant to see.
    public static string FilterContent(
        string api, string yaml, ModuleSelection selection)
    {
        var coverage = ModuleContentCatalog.SpecCoverage(api);
        if (coverage is null || coverage.Count == 1) return yaml;

        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return yaml;
        if (!root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode)
            || pathsNode is not YamlMappingNode paths)
            return yaml;

        foreach (var pathEntry in paths.Children.ToList())
        {
            if (pathEntry.Key is not YamlScalarNode pathScalar
                || pathEntry.Value is not YamlMappingNode methods)
                continue;

            foreach (var methodEntry in methods.Children.ToList())
            {
                if (methodEntry.Key is not YamlScalarNode methodScalar
                    || methodEntry.Value is not YamlMappingNode operation)
                    continue;
                var method = methodScalar.Value ?? string.Empty;
                if (!HttpMethods.Contains(method)) continue;

                var path = pathScalar.Value ?? string.Empty;
                var operationId = GetScalar(operation, "operationId")
                    ?? $"{api}:{method.ToLowerInvariant()}_{path}";
                if (!ModuleContentCatalog.IsOperationAvailable(api, operationId, selection))
                    methods.Children.Remove(methodEntry.Key);
            }

            if (!methods.Children.Any(entry =>
                    entry.Key is YamlScalarNode key && HttpMethods.Contains(key.Value ?? string.Empty)))
                paths.Children.Remove(pathEntry.Key);
        }

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }
    // Parses OpenAPI paths -> operations. Public + static so the AiApiSpec service
    // can compute OperationCount without duplicating the walk.
    public static IEnumerable<ApiOperation> ParseOperations(string api, string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            yield break;
        if (!root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode) || pathsNode is not YamlMappingNode paths)
            yield break;

        foreach (var pathEntry in paths)
        {
            if (pathEntry.Key is not YamlScalarNode pathScalar) continue;
            if (pathEntry.Value is not YamlMappingNode methodsNode) continue;

            foreach (var methodEntry in methodsNode)
            {
                if (methodEntry.Key is not YamlScalarNode methodScalar) continue;
                if (methodEntry.Value is not YamlMappingNode opNode) continue;

                var method = methodScalar.Value ?? string.Empty;
                if (!HttpMethods.Contains(method)) continue;

                var path = pathScalar.Value ?? string.Empty;
                yield return new ApiOperation
                {
                    OperationId = GetScalar(opNode, "operationId") ?? $"{api}:{method.ToLowerInvariant()}_{path}",
                    Api = api,
                    Method = method.ToUpperInvariant(),
                    Path = path,
                    Summary = GetScalar(opNode, "summary") ?? string.Empty,
                    Description = GetScalar(opNode, "description") ?? string.Empty,
                    Tags = GetStringList(opNode, "tags"),
                };
            }
        }
    }

    private static string? GetScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;

    private static List<string> GetStringList(YamlMappingNode node, string key)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlSequenceNode seq)
            return seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? string.Empty).Where(s => s.Length > 0).ToList();
        return [];
    }

    private sealed class SpecIndexSnapshot
    {
        public static readonly SpecIndexSnapshot Empty = new()
        {
            Operations = [],
            ById = new Dictionary<string, ApiOperation>(StringComparer.Ordinal),
        };

        public IReadOnlyList<ApiOperation> Operations { get; init; } = [];
        public IReadOnlyDictionary<string, ApiOperation> ById { get; init; } = new Dictionary<string, ApiOperation>(StringComparer.Ordinal);
    }
}
