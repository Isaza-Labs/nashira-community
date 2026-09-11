using System.Text.Json;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Workflow;

/// <summary>
/// Recognition and parsing for the portable bundle format.
/// </summary>
/// <remarks>
/// Detection is by explicit marker (<c>kind</c>), never by shape. Guessing from the
/// presence of a <c>snippet_id</c> key is unreliable — that shape is shared by the
/// plain YAML export, by a raw workflow.v1 document and by several foreign formats —
/// and a format that travels between installations has to say what it is.
/// </remarks>
public static class WorkflowBundleReader
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // Readable diffs matter: these files get committed to git repos and reviewed in
        // pull requests.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// True when the payload announces itself as a bundle. Cheap and total — never
    /// throws, so the caller can use it to pick a path.
    /// </summary>
    public static bool LooksLikeBundle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // The format is JSON only. A YAML document is the plain export and belongs on
        // the other path; parsing it here just to reject it would be wasted work.
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            return root.TryGetProperty("kind", out var kind)
                   && kind.ValueKind == JsonValueKind.String
                   && WorkflowBundle.AcceptedKinds.Contains(kind.GetString(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses and structurally checks a bundle. A v2 bundle comes back as a v3 one with
    /// <c>requires</c> inferred and no triggers (bundle/SPEC.md §1, §2.3). Unknown members
    /// are ignored; an unknown capability name is refused (§2.2).
    /// </summary>
    public static WorkflowBundle Parse(string raw)
    {
        WorkflowBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<WorkflowBundle>(raw, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new ValidationException(
                $"workflow bundle is not valid JSON: {ex.Message}", "bundle_parse_failed");
        }

        if (bundle is null)
            throw new ValidationException("workflow bundle is empty", "bundle_empty");

        // Refuse a version this build does not know rather than silently reading a
        // subset of it — a bundle that half-imports is worse than one rejected with a
        // version number the operator can act on.
        if (!WorkflowBundle.AcceptedSchemaVersions.Contains(bundle.SchemaVersion, StringComparer.Ordinal))
            throw new ValidationException(
                $"workflow bundle schema_version '{bundle.SchemaVersion}' is not supported by this "
                + $"instance (accepted: {string.Join(", ", WorkflowBundle.AcceptedSchemaVersions)}). "
                + "Re-export the workflow from the source instance, or upgrade this one.",
                "bundle_version_unsupported");

        // Lists may arrive as JSON null from a hand-edited file; treat that as empty.
        bundle.Requires ??= new BundleRequires();
        bundle.Requires.SnippetTypes ??= [];
        bundle.Requires.Capabilities ??= [];
        bundle.Requires.Secrets ??= [];
        bundle.Dependencies ??= new BundleDependencies();
        bundle.Dependencies.Snippets ??= [];
        bundle.Dependencies.Integrations ??= [];
        bundle.Dependencies.McpServers ??= [];
        bundle.Dependencies.Credentials ??= [];
        bundle.Dependencies.Repositories ??= [];
        bundle.Dependencies.Workflows ??= [];
        bundle.Triggers ??= [];

        if (string.IsNullOrWhiteSpace(bundle.Workflow?.Name))
            throw new ValidationException("the bundle's workflow.name is required", "name_required");

        if (bundle.Nodes.ValueKind != JsonValueKind.Array)
            throw new ValidationException("bundle nodes must be an array", "nodes_invalid");
        if (bundle.Edges.ValueKind != JsonValueKind.Array)
            throw new ValidationException("bundle edges must be an array", "edges_invalid");
        foreach (var sub in bundle.Dependencies.Workflows)
        {
            if (string.IsNullOrWhiteSpace(sub.Name))
                throw new ValidationException(
                    $"a carried sub-workflow ({sub.Id}) has no name", "name_required");
            if (sub.Nodes.ValueKind != JsonValueKind.Array || sub.Edges.ValueKind != JsonValueKind.Array)
                throw new ValidationException(
                    $"the carried sub-workflow '{sub.Name}' must have nodes and edges arrays", "nodes_invalid");
        }

        // Every snippet the nodes reference — in the root and in every carried
        // sub-workflow — must have travelled. A bundle missing one cannot be completed
        // by guessing, and guessing is exactly the placeholder behaviour this format
        // exists to remove.
        var carried = bundle.Dependencies.Snippets.Select(s => s.Id).ToHashSet();
        var absent = new SortedSet<Guid>();
        foreach (var nodes in bundle.Dependencies.Workflows.Select(w => w.Nodes).Prepend(bundle.Nodes))
            foreach (var id in NodeReferences.Extract(nodes).SnippetIds)
                if (!carried.Contains(id)) absent.Add(id);
        if (absent.Count > 0)
            throw new ValidationException(
                "the bundle references snippets whose definitions it does not carry "
                + $"({string.Join(", ", absent)}). Re-export it from the source instance.",
                "bundle_incomplete");

        // Same for sub-workflows, and a cycle is refused here, before anything is
        // resolved against the database.
        BundleSubWorkflows.CreationOrder(bundle);

        if (string.Equals(bundle.SchemaVersion, "v2", StringComparison.Ordinal))
        {
            // §2.3: v2 declared nothing. Infer from what is visible.
            bundle.Triggers = [];
            bundle.Requires = BundleRequirements.Compute(bundle);
            return bundle;
        }

        // §2.2: the vocabulary is closed. A name this build has never heard of might
        // be something the workflow cannot run without, so it is refused rather than
        // skipped.
        var unknown = bundle.Requires.Capabilities
            .Where(c => !BundleCapabilities.Known.Contains(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
            throw new ValidationException(
                $"the bundle requires capabilities this instance does not recognise: {string.Join(", ", unknown)}. "
                + $"Known capabilities: {string.Join(", ", BundleCapabilities.Known.OrderBy(k => k, StringComparer.Ordinal))}.",
                "bundle_capability_unsupported");

        return bundle;
    }

    public static string FileName(string workflowName)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in workflowName)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        var safe = sb.ToString().Trim('_');
        return (safe.Length == 0 ? "workflow" : safe) + ".bundle.json";
    }
}
