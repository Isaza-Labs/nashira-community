using System.Text.Json;
using System.Text.RegularExpressions;
using nashira_backend.Services.Ai.Secrets;

namespace nashira_backend.Services.Workflow;

// Computes a bundle's `requires` block (bundle/SPEC.md §2) from what the bundle
// carries. Used on export to declare, and on import to infer for a v2 bundle (§2.3)
// and to cross-check a v3 one — a bundle that forgot to declare a capability it
// visibly uses is still imported honestly.
//
// Everything here is a pure function of the bundle. It never touches the database:
// what the workflow NEEDS is a property of the file, and it has to be computable on
// the far side from the file alone.
public static partial class BundleRequirements
{
    // `{{ … | … }}` — a filter pipe inside a template (templates/SPEC.md §4).
    [GeneratedRegex(@"\{\{[^{}]*\|[^{}]*\}\}")]
    private static partial Regex FilterPipe();

    // `{{ run.` — the run namespace (templates/SPEC.md §7).
    [GeneratedRegex(@"\{\{\s*run\.")]
    private static partial Regex RunNamespace();

    // `steps.<node>.output.<field>` — a consumer reading a producer's output by field.
    [GeneratedRegex(@"steps\.(?<node>[A-Za-z0-9_\-]+)\.output\.(?<field>[A-Za-z0-9_]+)")]
    private static partial Regex StepOutputField();

    public static BundleRequires Compute(WorkflowBundle bundle)
    {
        var snippetsById = bundle.Dependencies.Snippets
            .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());

        var graphs = new List<(JsonElement Nodes, JsonElement Edges)> { (bundle.Nodes, bundle.Edges) };
        graphs.AddRange(bundle.Dependencies.Workflows.Select(w => (w.Nodes, w.Edges)));

        var capabilities = new SortedSet<string>(StringComparer.Ordinal);
        var secrets = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var nodesBySnippet = new Dictionary<Guid, SortedSet<string>>();

        foreach (var (nodes, edges) in graphs)
        {
            if (edges.ValueKind == JsonValueKind.Array
                && edges.EnumerateArray().Any(e => Is(e, "type", "conditional")))
                capabilities.Add(BundleCapabilities.ConditionalEdges);

            if (nodes.ValueKind != JsonValueKind.Array) continue;

            var nodeList = nodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object).ToList();
            var perDeviceNodes = nodeList
                .Where(n => TargetMode(n, snippetsById) == "per_device")
                .Select(NodeReferences.NodeId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var node in nodeList)
            {
                var nodeId = NodeReferences.NodeId(node);
                if (NodeReferences.IsSubflow(node)) capabilities.Add(BundleCapabilities.Subflow);

                if (node.TryGetProperty("snippet_id", out var sid) && sid.ValueKind == JsonValueKind.String
                    && Guid.TryParse(sid.GetString(), out var snippetId))
                {
                    if (!nodesBySnippet.TryGetValue(snippetId, out var users))
                        nodesBySnippet[snippetId] = users = new SortedSet<string>(StringComparer.Ordinal);
                    users.Add(nodeId);
                }

                if (!node.TryGetProperty("config_overrides", out var overrides)) continue;

                foreach (var text in Strings(overrides))
                {
                    if (FilterPipe().IsMatch(text)) capabilities.Add(BundleCapabilities.TemplateFilters);
                    if (RunNamespace().IsMatch(text)) capabilities.Add(BundleCapabilities.RunNamespace);

                    // A per_device consumer addressing a per_device producer's output by
                    // a field other than the aggregate's `devices` relies on the engine
                    // scoping that output to the current device (templates/SPEC.md §6).
                    if (perDeviceNodes.Contains(nodeId))
                    {
                        foreach (Match m in StepOutputField().Matches(text))
                        {
                            if (perDeviceNodes.Contains(m.Groups["node"].Value)
                                && !string.Equals(m.Groups["field"].Value, "devices", StringComparison.Ordinal))
                                capabilities.Add(BundleCapabilities.PerDeviceScope);
                        }
                    }

                    foreach (var reference in SecretResolver.References(text))
                        Use(secrets, reference.Raw, nodeId);
                }
            }
        }

        foreach (var snippet in bundle.Dependencies.Snippets)
        {
            if (snippet.MaxParallel > 1) capabilities.Add(BundleCapabilities.MaxParallel);
            if (string.Equals(snippet.TargetMode?.Trim(), "per_pool", StringComparison.OrdinalIgnoreCase))
                capabilities.Add(BundleCapabilities.PerPool);
            if (snippet.NetworkEnabled
                && string.Equals(snippet.Type?.Trim(), "python_snippet", StringComparison.OrdinalIgnoreCase))
                capabilities.Add(BundleCapabilities.PythonNetwork);

            // A reference inside the snippet's code or schema defaults is used by every
            // node that runs the snippet.
            nodesBySnippet.TryGetValue(snippet.Id, out var users);
            foreach (var text in new[] { snippet.Code, snippet.InputSchema?.GetRawText() })
                foreach (var reference in SecretResolver.References(text))
                    Use(secrets, reference.Raw, users);
        }

        if (bundle.Triggers.Count > 0) capabilities.Add(BundleCapabilities.Triggers);

        return new BundleRequires
        {
            SnippetTypes = bundle.Dependencies.Snippets
                .Select(s => (s.Type ?? string.Empty).Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList(),
            Capabilities = capabilities.ToList(),
            Secrets = secrets
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new BundleSecretRequirement { Ref = kv.Key, UsedBy = kv.Value.ToList() })
                .ToList(),
        };
    }

    /// <summary>
    /// Declared ∪ inferred. A v3 exporter that missed a capability the file visibly uses
    /// still gets the honest treatment; a declared capability nothing visibly uses is
    /// kept, because the exporter may know something this scan cannot see.
    /// </summary>
    public static BundleRequires Merge(BundleRequires declared, BundleRequires inferred) => new()
    {
        SnippetTypes = declared.SnippetTypes.Concat(inferred.SnippetTypes)
            .Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
        Capabilities = declared.Capabilities.Concat(inferred.Capabilities)
            .Select(c => c.Trim()).Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList(),
        Secrets = declared.Secrets.Concat(inferred.Secrets)
            .GroupBy(s => s.Ref, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BundleSecretRequirement
            {
                Ref = g.Key,
                UsedBy = g.SelectMany(s => s.UsedBy).Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal).ToList(),
            })
            .ToList(),
    };

    /// <summary>Every string value under <paramref name="element"/>, depth-first.</summary>
    public static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var s in Strings(item)) yield return s;
                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    foreach (var s in Strings(prop.Value)) yield return s;
                break;
        }
    }

    private static string? TargetMode(JsonElement node, IReadOnlyDictionary<Guid, BundleSnippet> snippets)
    {
        if (!node.TryGetProperty("snippet_id", out var sid) || sid.ValueKind != JsonValueKind.String) return null;
        if (!Guid.TryParse(sid.GetString(), out var id) || !snippets.TryGetValue(id, out var snippet)) return null;
        return snippet.TargetMode?.Trim().ToLowerInvariant();
    }

    private static bool Is(JsonElement obj, string property, string value) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
        && string.Equals(el.GetString(), value, StringComparison.OrdinalIgnoreCase);

    private static void Use(Dictionary<string, SortedSet<string>> secrets, string reference, string user)
        => Use(secrets, reference, [user]);

    private static void Use(Dictionary<string, SortedSet<string>> secrets, string reference, IEnumerable<string>? users)
    {
        if (!secrets.TryGetValue(reference, out var set))
            secrets[reference] = set = new SortedSet<string>(StringComparer.Ordinal);
        if (users is null) return;
        foreach (var u in users) set.Add(u);
    }
}
