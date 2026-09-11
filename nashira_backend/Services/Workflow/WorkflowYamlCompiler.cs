using System.Text.Json;
using YamlDotNet.Serialization;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Workflow;

// Compiles a workflow into its human-readable YAML artifact (deterministic for a given
// workflow: fixed top-level field order, JSON converted recursively). Mirrors the
// oracle's compiled shape: version / workflow{...} / nodes / edges / input_schema / metadata.
public sealed class WorkflowYamlCompiler
{
    private static readonly ISerializer Yaml = new SerializerBuilder().Build();

    public string Compile(WorkflowEntity wf)
    {
        var doc = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["workflow"] = new Dictionary<string, object?>
            {
                ["id"] = wf.WorkflowId.ToString(),
                ["name"] = wf.Name,
                ["description"] = wf.Description,
                ["environment"] = wf.Environment,
                ["schema_version"] = wf.SchemaVersion,
            },
            ["nodes"] = JsonToObject(ParseJson(wf.NodesJson)),
            ["edges"] = JsonToObject(ParseJson(wf.EdgesJson)),
            ["input_schema"] = wf.InputSchemaJson is null ? null : JsonToObject(ParseJson(wf.InputSchemaJson)),
            ["metadata"] = wf.MetadataJson is null ? null : JsonToObject(ParseJson(wf.MetadataJson)),
        };
        return Yaml.Serialize(doc);
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal static object? JsonToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonToObject(p.Value)),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonToObject).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
