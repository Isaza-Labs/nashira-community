using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Tests;

// The import half of export -> import. The property that matters is the round trip:
// whatever Compile writes, Parse must read back without drift, because the two are
// the wire format between one Nashira instance and another.
public class WorkflowYamlParserTests
{
    private static readonly WorkflowYamlCompiler Compiler = new();
    private static readonly WorkflowYamlParser Parser = new();

    private static WorkflowEntity Workflow() => new()
    {
        WorkflowId = Guid.NewGuid(),
        Name = "drain-edge",
        Description = "Drain traffic from an edge router",
        Version = 3,
        SchemaVersion = "v1",
        Environment = WorkflowEntity.EnvProduction,
        NodesJson = """
            [{"id":"start","snippet_id":"__start__","type":"task"},
             {"id":"drain","snippet_id":"isis_overload","type":"task","retries":2,"critical":true},
             {"id":"end","snippet_id":"__end__","type":"task"}]
            """,
        EdgesJson = """
            [{"source":"start","target":"drain","type":"success"},
             {"source":"drain","target":"end","type":"always"}]
            """,
        InputSchemaJson = """{"type":"object","properties":{"device":{"type":"string"}}}""",
        MetadataJson = """{"owner":"neteng","tier":2}""",
    };

    [Fact]
    public void Round_trip_preserves_name_description_nodes_and_edges()
    {
        var wf = Workflow();

        var parsed = Parser.Parse(Compiler.Compile(wf));

        Assert.Equal(wf.Name, parsed.Name);
        Assert.Equal(wf.Description, parsed.Description);
        Assert.Equal(Canonical(wf.NodesJson), Canonical(parsed.Nodes.GetRawText()));
        Assert.Equal(Canonical(wf.EdgesJson), Canonical(parsed.Edges.GetRawText()));
        Assert.Equal(Canonical(wf.InputSchemaJson!), Canonical(parsed.InputSchema!.Value.GetRawText()));
        Assert.Equal(Canonical(wf.MetadataJson!), Canonical(parsed.Metadata!.Value.GetRawText()));
    }

    // A round trip that changes the schema hash would make an imported workflow
    // report as a different definition than the one exported.
    [Fact]
    public void Round_trip_preserves_the_canonical_schema_hash()
    {
        var wf = Workflow();
        var original = WorkflowCanonicalizer.ComputeSchemaHash(Parse(wf.NodesJson), Parse(wf.EdgesJson));

        var parsed = Parser.Parse(Compiler.Compile(wf));

        Assert.Equal(original, WorkflowCanonicalizer.ComputeSchemaHash(parsed.Nodes, parsed.Edges));
    }

    [Fact]
    public void Round_trip_preserves_scalar_types()
    {
        // retries stays a number and critical stays a boolean — stringifying either
        // would fail workflow.v1 validation on the way back in.
        var parsed = Parser.Parse(Compiler.Compile(Workflow()));

        var drain = parsed.Nodes.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "drain");
        Assert.Equal(JsonValueKind.Number, drain.GetProperty("retries").ValueKind);
        Assert.Equal(2, drain.GetProperty("retries").GetInt32());
        Assert.Equal(JsonValueKind.True, drain.GetProperty("critical").ValueKind);
    }

    [Fact]
    public void A_quoted_numeric_id_stays_a_string()
    {
        // "01" must not come back as 1 — node ids are matched by string equality.
        const string yaml = """
            workflow:
              name: quoted
            nodes:
              - id: "01"
                snippet_id: __start__
            edges: []
            """;

        var parsed = Parser.Parse(yaml);

        var id = parsed.Nodes.EnumerateArray().Single().GetProperty("id");
        Assert.Equal(JsonValueKind.String, id.ValueKind);
        Assert.Equal("01", id.GetString());
    }

    [Fact]
    public void A_null_input_schema_round_trips_as_absent()
    {
        // The compiler writes `input_schema:` with an empty value, not an absent key.
        var wf = Workflow();
        wf.InputSchemaJson = null;
        wf.MetadataJson = null;

        var parsed = Parser.Parse(Compiler.Compile(wf));

        Assert.Null(parsed.InputSchema);
        Assert.Null(parsed.Metadata);
    }

    [Fact]
    public void A_flat_document_without_the_workflow_wrapper_is_accepted()
    {
        // Hand-written files should not need the export's nesting.
        var parsed = Parser.Parse("""
            name: hand-written
            nodes: []
            edges: []
            """);

        Assert.Equal("hand-written", parsed.Name);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("just a string", "mapping")]
    [InlineData("nodes: []\nedges: []", "name")]
    [InlineData("name: x\nedges: []", "nodes")]
    [InlineData("name: x\nnodes: []", "edges")]
    [InlineData("name: x\nnodes: []\nedges: []\ninput_schema: 5", "input_schema")]
    public void Malformed_documents_are_rejected_with_a_reason(string yaml, string expected)
    {
        var ex = Assert.Throws<ValidationException>(() => Parser.Parse(yaml));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Bad syntax is user input, so it has to come back as a ValidationException (400)
    // whichever exception type YamlDotNet happens to raise — the scanner does not
    // funnel everything through YamlException.
    [Theory]
    [InlineData("name: [unclosed\nnodes: []")]
    [InlineData("name: x\n\tnodes: []")]
    [InlineData("a: 1\n  b: 2")]
    public void Invalid_yaml_is_a_validation_error_not_a_server_error(string yaml)
    {
        var ex = Assert.Throws<ValidationException>(() => Parser.Parse(yaml));
        Assert.Contains("not valid YAML", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // An alias bomb expands while the tree is built, so a small file can materialise
    // an enormous one. The budget has to be enforced during the walk.
    [Fact]
    public void An_alias_expansion_bomb_is_refused()
    {
        var yaml = """
            a: &a ["x","x","x","x","x","x","x","x","x"]
            b: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]
            c: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]
            d: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c]
            e: &e [*d,*d,*d,*d,*d,*d,*d,*d,*d]
            name: bomb
            nodes: [*e,*e,*e,*e,*e,*e,*e,*e,*e]
            edges: []
            """;

        var ex = Assert.Throws<ValidationException>(() => Parser.Parse(yaml));
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // Compare by value, not by whitespace.
    private static string Canonical(string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json));
}
