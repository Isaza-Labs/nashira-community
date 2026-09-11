using nashira_backend.Services.Ai.Specs;

namespace nashira_backend.Tests;

// The shipped Specs/*.yaml are the agent's catalog on a fresh install. A file that
// does not parse, or that parses to nothing, is invisible at runtime — the seeder
// logs it and moves on — so the failure would only ever surface as "the agent claims
// this API has no operations". These tests are the thing that catches it.
public class BuiltinSpecTests
{
    private static IReadOnlyList<(string Api, string Path)> SpecFiles()
    {
        var dir = Path.Combine(RepoRoot(), "nashira_backend", "Specs");
        Assert.True(Directory.Exists(dir), $"Specs directory not found at {dir}");
        return Directory.GetFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(dir, "*.yml", SearchOption.TopDirectoryOnly))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), f))
            .ToList();
    }

    // Walk up from the test binary to the directory holding the solution file.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nashira.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_builtin_spec_parses_into_at_least_one_operation()
    {
        var files = SpecFiles();
        Assert.NotEmpty(files);

        foreach (var (api, path) in files)
        {
            var yaml = File.ReadAllText(path);
            var ops = YamlSpecIndex.ParseOperations(api, yaml).ToList();
            Assert.True(ops.Count > 0, $"{api}: parsed to zero operations");
        }
    }

    [Fact]
    public void Builtin_operation_ids_are_unique_across_every_spec()
    {
        // YamlSpecIndex keys the whole catalog by operationId and keeps the first of a
        // duplicate pair, so a collision silently hides one operation from the agent.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (api, path) in SpecFiles())
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

    [Fact]
    public void Builtin_operations_carry_a_summary_and_a_real_operation_id()
    {
        // A missing operationId is synthesized as "<api>:<method>_<path>", which works
        // but is not something the agent can be told to call by name.
        foreach (var (api, path) in SpecFiles())
        {
            foreach (var op in YamlSpecIndex.ParseOperations(api, File.ReadAllText(path)))
            {
                Assert.False(op.OperationId.StartsWith($"{api}:", StringComparison.Ordinal),
                    $"{api} {op.Method} {op.Path}: missing operationId");
                Assert.False(string.IsNullOrWhiteSpace(op.Summary),
                    $"{api} {op.OperationId}: missing summary");
            }
        }
    }
}
