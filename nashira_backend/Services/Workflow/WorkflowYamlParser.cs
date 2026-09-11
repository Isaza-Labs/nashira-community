using System.Text.Json;
using System.Text.Json.Nodes;
using nashira_backend.Exceptions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace nashira_backend.Services.Workflow;

// Reads back the artifact WorkflowYamlCompiler writes: version / workflow{...} /
// nodes / edges / input_schema / metadata. The inverse of Compile, and the import
// half of "export a workflow from one instance, import it into another".
//
// Two things are read and then deliberately discarded:
//   - workflow.id     — the exported row's identity. Honouring it would let an
//                       import silently overwrite an unrelated local workflow.
//   - workflow.environment — importing straight into production would walk around
//                       the promotion gate, which is the one thing the gate exists
//                       to prevent. Every import lands in draft and is promoted
//                       through the normal path.
//
// Structure is checked here; workflow.v1 conformance and acyclicity are not — those
// stay with WorkflowValidator, so an imported definition passes exactly the same
// write-time gate a hand-authored one does.
public sealed class WorkflowYamlParser
{
    // Guards against an alias-expansion bomb: YamlStream resolves aliases while it
    // builds the tree, so a small file can materialise an enormous one. Counted
    // during the walk rather than on input length, which would not catch it.
    private const int MaxNodes = 50_000;

    public sealed record ImportedWorkflow(
        string Name,
        string? Description,
        string? SchemaVersion,
        JsonElement Nodes,
        JsonElement Edges,
        JsonElement? InputSchema,
        JsonElement? Metadata);

    public ImportedWorkflow Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ValidationException("the workflow document is empty");

        // Only the load is wrapped: the structural checks below raise ValidationException
        // themselves and must not be reclassified as syntax errors.
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            // Line/column is the difference between "it's broken" and a fixable report.
            throw new ValidationException(
                $"the workflow document is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The scanner does not funnel everything through YamlException — an
            // unterminated flow sequence surfaces as InvalidOperationException. Left
            // unhandled these become a 500 on what is plainly bad user input.
            throw new ValidationException($"the workflow document is not valid YAML: {ex.Message}");
        }

        if (stream.Documents.Count == 0)
            throw new ValidationException("the workflow document is empty");
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ValidationException("the workflow document must be a YAML mapping");

        var budget = MaxNodes;

        var nodes = RequireSequence(root, "nodes", ref budget);
        var edges = RequireSequence(root, "edges", ref budget);

        // The compiler nests identity under `workflow`; accept a flat top-level
        // `name`/`description` too, so a hand-written file does not need the wrapper.
        var meta = Child(root, "workflow") as YamlMappingNode;
        var name = Scalar(meta, "name") ?? Scalar(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("the workflow document has no name (workflow.name)");

        return new ImportedWorkflow(
            Name: name!.Trim(),
            Description: Scalar(meta, "description") ?? Scalar(root, "description"),
            SchemaVersion: Scalar(meta, "schema_version"),
            Nodes: nodes,
            Edges: edges,
            InputSchema: OptionalMapping(root, "input_schema", ref budget),
            Metadata: OptionalMapping(root, "metadata", ref budget));
    }

    private static JsonElement RequireSequence(YamlMappingNode root, string key, ref int budget)
    {
        if (Child(root, key) is not YamlSequenceNode seq)
            throw new ValidationException($"the workflow document has no `{key}` array");
        return ToJson(seq, ref budget);
    }

    private static JsonElement? OptionalMapping(YamlMappingNode root, string key, ref int budget)
    {
        var node = Child(root, key);
        // The compiler writes `input_schema:` with an empty value when the column is
        // null, which round-trips as a null scalar rather than an absent key.
        if (node is null || node is YamlScalarNode { Value: null or "" or "~" or "null" }) return null;
        if (node is not YamlMappingNode map)
            throw new ValidationException($"`{key}` must be a mapping");
        return ToJson(map, ref budget);
    }

    private static YamlNode? Child(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlMappingNode? node, string key) =>
        Child(node, key) is YamlScalarNode { Value: { Length: > 0 } v } ? v : null;

    // ─── YAML -> JSON ───────────────────────────────────────────────────

    private static JsonElement ToJson(YamlNode node, ref int budget)
    {
        var json = Convert(node, ref budget);
        return JsonSerializer.SerializeToElement(json);
    }

    private static JsonNode? Convert(YamlNode node, ref int budget)
    {
        if (--budget < 0)
            throw new ValidationException("the workflow document is too large to import");

        switch (node)
        {
            case YamlMappingNode map:
            {
                var obj = new JsonObject();
                foreach (var entry in map.Children)
                {
                    // JSON object keys are strings; a complex YAML key has no
                    // representation, and silently stringifying it would corrupt the
                    // definition rather than reject it.
                    if (entry.Key is not YamlScalarNode { Value: { } key })
                        throw new ValidationException("workflow mappings must use scalar keys");
                    obj[key] = Convert(entry.Value, ref budget);
                }
                return obj;
            }

            case YamlSequenceNode seq:
            {
                var arr = new JsonArray();
                foreach (var child in seq.Children)
                    arr.Add(Convert(child, ref budget));
                return arr;
            }

            case YamlScalarNode scalar:
                return ConvertScalar(scalar);

            default:
                return null;
        }
    }

    // Resolves an untagged plain scalar to its JSON type. A quoted or block scalar is
    // always a string — that is what the quotes mean, and it is how the compiler keeps
    // a node id like "01" from coming back as the number 1.
    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;

        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(text);
        if (scalar.Tag is { IsEmpty: false } tag && tag.Value == "tag:yaml.org,2002:str")
            return JsonValue.Create(text);

        if (text.Length == 0 || text is "~" or "null" or "Null" or "NULL") return null;

        // Only the canonical booleans. YAML 1.1's y/yes/on family is deliberately not
        // accepted: a node named "no" is far more likely than an intended false.
        if (bool.TryParse(text, out var b)) return JsonValue.Create(b);

        if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);

        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);

        return JsonValue.Create(text);
    }
}
