using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Ai.Tools;
using YamlDotNet.RepresentationModel;

namespace nashira_backend.Tests;

// The integration bundles under `integrations/<name>/` — spec.yaml plus skill.md, the
// pair `register.ps1` posts to /api/integrations/bundle.
//
// Nothing else looks at these files. They are not compiled, and unlike the built-in
// Specs/*.yaml they are not seeded at boot, so a malformed one is not caught until
// somebody runs the register script against a live instance and the API answers 400 —
// or worse, accepts a document that parses to zero operations and leaves an integration
// whose action catalogue is empty. The seeder's own failure mode, one step further from
// anyone watching.
public class IntegrationBundleSpecTests
{
    // Walk up from the test binary to the directory holding the solution file.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nashira.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // (api, spec path, bundle directory). `api` is the directory name, which is what
    // register.ps1 sends and therefore what the agent types as
    // discover_operations(api="…").
    private static IReadOnlyList<(string Api, string SpecPath, string Dir)> Bundles()
    {
        var root = Path.Combine(RepoRoot(), "integrations");
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Select(d => (Api: Path.GetFileName(d).ToLowerInvariant(), SpecPath: Path.Combine(d, "spec.yaml"), Dir: d))
            .Where(b => File.Exists(b.SpecPath))
            .OrderBy(b => b.Api, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void Every_bundle_spec_parses_into_at_least_one_operation()
    {
        var bundles = Bundles();
        Assert.NotEmpty(bundles);

        foreach (var (api, path, _) in bundles)
        {
            var ops = YamlSpecIndex.ParseOperations(api, File.ReadAllText(path)).ToList();
            Assert.True(ops.Count > 0, $"{api}: parsed to zero operations");
        }
    }

    // A bundle is a spec AND the operational knowledge that goes with it. Posting the
    // spec alone gives the agent a list of endpoints and no idea that sysparm_query is
    // an encoded query, which is how it writes a filter that silently matches
    // everything.
    [Fact]
    public void Every_bundle_ships_a_skill_beside_its_spec()
    {
        foreach (var (api, _, dir) in Bundles())
        {
            var skill = Path.Combine(dir, "skill.md");
            Assert.True(File.Exists(skill), $"{api}: no skill.md beside spec.yaml");
            Assert.NotEmpty(File.ReadAllText(skill).Trim());
        }
    }

    // The agent picks operations out of `discover_operations` by summary. One without a
    // summary is listed by its id alone, which reads as noise next to twenty that
    // describe themselves — so it is the one that never gets called.
    [Fact]
    public void Every_bundle_operation_has_a_summary()
    {
        foreach (var (api, path, _) in Bundles())
        {
            foreach (var op in YamlSpecIndex.ParseOperations(api, File.ReadAllText(path)))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(op.Summary),
                    $"{api}: operation '{op.OperationId}' has no summary");
            }
        }
    }

    // The runtime index is keyed by operationId across EVERY spec in the table, not per
    // api — `ById` is built with GroupBy(OperationId).First(), so the loser of a
    // collision is not lower-priority, it is absent. A bundle colliding with a built-in
    // would delete one of Nashira's own operations from the catalog.
    [Fact]
    public void Bundle_operation_ids_collide_with_nothing_including_the_builtins()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        var builtins = Path.Combine(RepoRoot(), "nashira_backend", "Specs");
        var files = Directory.GetFiles(builtins, "*.yaml", SearchOption.TopDirectoryOnly)
            .Select(f => (Api: Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), Path: f))
            .Concat(Bundles().Select(b => (b.Api, Path: b.SpecPath)));

        foreach (var (api, path) in files)
        {
            foreach (var op in YamlSpecIndex.ParseOperations(api, File.ReadAllText(path)))
            {
                Assert.False(
                    seen.TryGetValue(op.OperationId, out var owner),
                    $"operationId '{op.OperationId}' is declared by both {owner} and {api}");
                seen[op.OperationId] = api;
            }
        }
    }

    // A `$ref` that does not resolve is not an error anywhere in the pipeline. The spec
    // still parses, the operation still lists, and OperationYamlSlicer simply skips the
    // entry — "an unresolvable $ref, not a parameter". So one mistyped pointer deletes a
    // parameter from what `operation_detail` shows, and the agent then calls the
    // operation without it. That is the bug the slicer's own header describes: an
    // operation whose parameters came back empty, and a model that did not know `limit`
    // existed.
    //
    // Comparing declared entries against resolved ones is what catches it. The counts
    // match as long as no bundle declares the same parameter at both the path and the
    // operation level, which would be redundant anyway.
    [Fact]
    public void Every_parameter_ref_in_a_bundle_resolves()
    {
        foreach (var (api, path, _) in Bundles())
        {
            var yaml = File.ReadAllText(path);
            var declared = DeclaredParameterCounts(yaml);

            foreach (var op in YamlSpecIndex.ParseOperations(api, yaml))
            {
                var slice = OperationYamlSlicer.ExtractDetail(yaml, op.Method, op.Path);
                Assert.NotNull(slice);

                var expected = declared[$"{op.Method}|{op.Path}"];
                Assert.True(
                    slice!.Parameters.Count == expected,
                    $"{api}: '{op.OperationId}' declares {expected} parameters but "
                    + $"{slice.Parameters.Count} resolved — a $ref points at nothing");
            }
        }
    }

    // Raw count of parameter entries per operation: the shared path-level list plus the
    // operation's own, unresolved and undeduped. This is the "should be there" side of
    // the comparison, so it deliberately does not follow any $ref.
    private static Dictionary<string, int> DeclaredParameterCounts(string yaml)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return counts;
        if (!root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode)
            || pathsNode is not YamlMappingNode paths) return counts;

        static int Count(YamlMappingNode scope) =>
            scope.Children.TryGetValue(new YamlScalarNode("parameters"), out var p) && p is YamlSequenceNode seq
                ? seq.Children.Count
                : 0;

        foreach (var pathEntry in paths)
        {
            if (pathEntry.Key is not YamlScalarNode pathKey || pathEntry.Value is not YamlMappingNode methods) continue;
            var shared = Count(methods);

            foreach (var methodEntry in methods)
            {
                if (methodEntry.Key is not YamlScalarNode methodKey) continue;
                if (methodEntry.Value is not YamlMappingNode opNode) continue;
                // Skips the shared `parameters` key itself, which is a sequence.
                var method = methodKey.Value ?? string.Empty;
                if (method.Equals("parameters", StringComparison.OrdinalIgnoreCase)) continue;

                counts[$"{method.ToUpperInvariant()}|{pathKey.Value}"] = shared + Count(opNode);
            }
        }

        return counts;
    }

    // The bundle's `api` is its directory name, and IntegrationCatalog refuses anything
    // outside this pattern — a bundle named with a capital or a space would be rejected
    // by the API at the last step, after the operator had already entered a secret.
    [Fact]
    public void Every_bundle_directory_is_a_usable_api_identifier()
    {
        foreach (var (api, _, dir) in Bundles())
        {
            Assert.Matches("^[a-z0-9_-]+$", api);
            Assert.Equal(Path.GetFileName(dir), api);
        }
    }
}
