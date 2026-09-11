using nashira_backend.Services.Workflow;
using YamlDotNet.Serialization;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Tests;

public class WorkflowYamlCompilerTests
{
    private static WorkflowEntity Sample() => new()
    {
        WorkflowId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "decom-site",
        Description = "bulk decommission",
        Environment = "draft",
        SchemaVersion = "v1",
        NodesJson = """[{"id":"a","snippet_id":"__start__","type":"task"},{"id":"b","snippet_id":"__end__","type":"task"}]""",
        EdgesJson = """[{"source":"a","target":"b","type":"success"}]""",
        MetadataJson = """{"count":2}""",
    };

    [Fact]
    public void Compile_is_deterministic()
    {
        var compiler = new WorkflowYamlCompiler();
        Assert.Equal(compiler.Compile(Sample()), compiler.Compile(Sample()));
    }

    [Fact]
    public void Compile_emits_the_contract_shape()
    {
        var yaml = new WorkflowYamlCompiler().Compile(Sample());
        var doc = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(yaml);

        Assert.Equal("1", doc["version"].ToString());
        var wf = (Dictionary<object, object>)doc["workflow"];
        Assert.Equal("decom-site", wf["name"]);
        Assert.Equal("draft", wf["environment"]);
        Assert.Equal("v1", wf["schema_version"]);
        Assert.Equal("11111111-1111-1111-1111-111111111111", wf["id"]);

        var nodes = (List<object>)doc["nodes"];
        Assert.Equal(2, nodes.Count);
        var edges = (List<object>)doc["edges"];
        Assert.Single(edges);
    }
}
