using System.Runtime.CompilerServices;
using System.Text.Json;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.Tools;

namespace nashira_backend.Tests;

// Guards the tool catalog against silent drift (the CHANGELOG's known follow-up).
// The catalog has three views that must stay in lockstep: the IToolHandler
// implementations, the PermissionClassifier matrix, and (at boot) the registry.
// A tool missing from the matrix would silently classify as human_only; an
// orphan matrix entry means a tool was removed without cleaning its policy.
//
// Handlers are materialized WITHOUT running constructors (no DI): Name /
// Description / ParametersSchema are expression-bodied constants over static
// fields by convention, so they are safe to read on an uninitialized instance —
// and this test doubles as the enforcement of that convention.
public class ToolCatalogTests
{
    private static readonly List<(Type Type, IToolHandler Handler)> Handlers =
        typeof(IToolHandler).Assembly.GetTypes()
            .Where(t => typeof(IToolHandler).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => (t, (IToolHandler)RuntimeHelpers.GetUninitializedObject(t)))
            .ToList();

    [Fact]
    public void Catalog_is_not_empty()
    {
        // Floor, not an exact count — adding tools must not fail the build, but a
        // collapse (bad DI wiring, a dropped assembly) must.
        Assert.True(Handlers.Count >= 80, $"expected the full catalog, found {Handlers.Count} handlers");
    }

    [Fact]
    public void Every_handler_has_an_explicit_permission_classification()
    {
        var missing = Handlers
            .Where(h => !PermissionClassifier.Matrix.ContainsKey(h.Handler.Name))
            .Select(h => $"{h.Handler.Name} ({h.Type.Name})")
            .ToList();
        Assert.True(missing.Count == 0,
            "handlers without a PermissionClassifier entry (they would silently become human_only): "
            + string.Join(", ", missing));
    }

    [Fact]
    public void Every_classification_matches_an_existing_handler()
    {
        var names = Handlers.Select(h => h.Handler.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = PermissionClassifier.Matrix.Keys.Where(k => !names.Contains(k)).ToList();
        Assert.True(orphans.Count == 0,
            "classifier entries with no handler behind them: " + string.Join(", ", orphans));
    }

    [Fact]
    public void Tool_names_are_unique_and_snake_case()
    {
        var duplicates = Handlers.GroupBy(h => h.Handler.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(" + ", g.Select(x => x.Type.Name))})")
            .ToList();
        Assert.True(duplicates.Count == 0, "duplicate tool names: " + string.Join(", ", duplicates));

        var badNames = Handlers
            .Where(h => !System.Text.RegularExpressions.Regex.IsMatch(h.Handler.Name, "^[a-z][a-z0-9_]*$"))
            .Select(h => h.Handler.Name)
            .ToList();
        Assert.True(badNames.Count == 0, "tool names must be snake_case: " + string.Join(", ", badNames));
    }

    [Fact]
    public void Every_schema_is_a_json_object_readable_without_di()
    {
        foreach (var (type, handler) in Handlers)
        {
            var schema = handler.ParametersSchema; // throws here if it needs constructor state
            Assert.True(schema.ValueKind == JsonValueKind.Object,
                $"{handler.Name} ({type.Name}): ParametersSchema must be a JSON object");
            Assert.False(string.IsNullOrWhiteSpace(handler.Description),
                $"{handler.Name} ({type.Name}): Description is empty");
        }
    }
}
