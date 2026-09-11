using System.Text.Json;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// The SchemaHash is the simulation-staleness fingerprint. It must be stable under object
// key reordering + number normalization, and change on any structural edit.
public class WorkflowCanonicalizerTests
{
    private static (JsonElement Nodes, JsonElement Edges) Doc(string json)
    {
        using var d = JsonDocument.Parse(json);
        return (d.RootElement.GetProperty("nodes").Clone(), d.RootElement.GetProperty("edges").Clone());
    }

    private static string Hash(string json)
    {
        var (nodes, edges) = Doc(json);
        return WorkflowCanonicalizer.ComputeSchemaHash(nodes, edges);
    }

    [Fact]
    public void Hash_is_stable_under_object_key_reordering()
    {
        Assert.Equal(
            Hash("""{"nodes":[{"id":"n","snippet_id":"s","type":"task"}],"edges":[]}"""),
            Hash("""{"nodes":[{"type":"task","snippet_id":"s","id":"n"}],"edges":[]}"""));
    }

    [Fact]
    public void Hash_normalizes_trailing_zeros()
    {
        Assert.Equal(
            Hash("""{"nodes":[{"id":"n","snippet_id":"s","x":1.0}],"edges":[]}"""),
            Hash("""{"nodes":[{"id":"n","snippet_id":"s","x":1}],"edges":[]}"""));
    }

    [Fact]
    public void Hash_changes_and_is_sha256_hex()
    {
        var a = Hash("""{"nodes":[{"id":"n","snippet_id":"s","config_overrides":{"status":"active"}}],"edges":[]}""");
        var b = Hash("""{"nodes":[{"id":"n","snippet_id":"s","config_overrides":{"status":"down"}}],"edges":[]}""");
        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);
    }

    [Theory]
    [InlineData("1.00", "1")]
    [InlineData("1.50", "1.5")]
    [InlineData("100", "100")]
    [InlineData("0.0", "0")]
    public void StripTrailingZeros_trims_correctly(string input, string expected) =>
        Assert.Equal(expected, WorkflowCanonicalizer.StripTrailingZeros(input));
}
