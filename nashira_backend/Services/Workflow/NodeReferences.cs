using System.Text.Json;
using System.Text.Json.Nodes;

namespace nashira_backend.Services.Workflow;

// Reads and rewrites what a workflow's nodes point at.
//
// One place owns the knowledge of WHERE a node keeps its references, because export
// reads them to decide what to describe and import rewrites them to point at local
// rows — and splitting that across the two directions is how they drift apart.
//
// The portable keys are those of bundle/SPEC.md §4. A node names its integration,
// action, MCP server, credential and repository by NAME (`integration`, `action`,
// `server`, `credential`, `repository`) and carries GUIDs only where they are
// structural: `snippet_id` and `subflow_workflow_id`, both remapped through the
// definitions the bundle carries. Nashira's git handler still reads `repository_id`,
// so that id is written onto the STORED row on import — but it never goes back out on
// the wire: §4 forbids an exporter emitting any legacy id key.
// The legacy id keys (`integration_id`, `action_id`, `mcp_server_id`, `credential_id`,
// `repository_id`) are still accepted on import — v2 bundles and hand-authored files
// carry them — translated when the bundle's dependencies carry the mapping and refused
// otherwise; when both keys are present the canonical one wins.
public static class NodeReferences
{
    public const string SubflowLiteral = "subflow";

    public sealed record Refs(
        HashSet<Guid> SnippetIds,
        HashSet<string> IntegrationNames,
        HashSet<string> McpServerNames,
        HashSet<string> CredentialNames,
        HashSet<Guid> CredentialIds,
        HashSet<string> RepositoryNames,
        HashSet<Guid> RepositoryIds,
        HashSet<Guid> SubflowWorkflowIds);

    public static Refs Extract(JsonElement nodes)
    {
        var refs = new Refs(
            [], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase), [], new(StringComparer.OrdinalIgnoreCase), [], []);

        if (nodes.ValueKind != JsonValueKind.Array) return refs;

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;

            if (TryGuid(node, "snippet_id", out var snippetId)) refs.SnippetIds.Add(snippetId);

            if (!node.TryGetProperty("config_overrides", out var overrides)
                || overrides.ValueKind != JsonValueKind.Object) continue;

            if (TryName(overrides, "integration", out var integration)) refs.IntegrationNames.Add(integration);
            // An mcp_call step names its server the same way. It is a dependency of the
            // same kind: present on this instance or the step fails at run time.
            if (TryName(overrides, "server", out var server)) refs.McpServerNames.Add(server);
            if (TryName(overrides, "credential", out var credential)) refs.CredentialNames.Add(credential);
            if (TryGuid(overrides, "credential_id", out var credentialId)) refs.CredentialIds.Add(credentialId);
            if (TryName(overrides, "repository", out var repository)) refs.RepositoryNames.Add(repository);
            if (TryGuid(overrides, "repository_id", out var repositoryId)) refs.RepositoryIds.Add(repositoryId);
            if (IsSubflow(node) && TryGuid(overrides, "subflow_workflow_id", out var child))
                refs.SubflowWorkflowIds.Add(child);
        }

        return refs;
    }

    /// <summary>
    /// A node that runs another workflow. bundle/SPEC.md §2.2 defines the capability by
    /// <c>type: "subflow"</c> — the schema's node <c>type</c> enum is
    /// <c>task | decision | subflow</c>, so the word cannot mean anything else — and
    /// execution/SPEC.md §5 pairs it with the <c>subflow</c> sentinel in
    /// <c>snippet_id</c>. Either one alone identifies the node.
    /// </summary>
    /// <remarks>
    /// Keying only on the sentinel left a node carrying <c>type: "subflow"</c> and some
    /// other <c>snippet_id</c> invisible to capability detection, to
    /// <c>SubflowWorkflowIds</c>, and therefore to the cycle guard — a way to walk a
    /// subflow past the one check that refuses a workflow which can never finish.
    /// </remarks>
    public static bool IsSubflow(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object
        && (IsSubflowSentinel(node)
            || (node.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String
                && string.Equals(t.GetString(), SubflowLiteral, StringComparison.Ordinal)));

    /// <summary>
    /// The <c>subflow</c> sentinel in <c>snippet_id</c>, which workflow.v1 allows
    /// beside <c>__start__</c> and <c>__end__</c>. Narrower than
    /// <see cref="IsSubflow"/> on purpose: a check that exempts a node because its
    /// extra keys are the child's input has to ask for the sentinel, or a node that
    /// names a real snippet and merely wears the type word slips through the exemption.
    /// </summary>
    public static bool IsSubflowSentinel(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty("snippet_id", out var s)
        && s.ValueKind == JsonValueKind.String
        && string.Equals(s.GetString(), SubflowLiteral, StringComparison.Ordinal);

    public static string NodeId(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? "?"
            : "?";

    // ─── export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rewrites the nodes into the portable keys of §4: the name <b>in place of</b> every
    /// local <c>credential_id</c> / <c>repository_id</c>, and the integration's
    /// <b>slug</b> in place of the local name an <c>integration</c> key carries. Existing
    /// names are left as written; nothing else changes.
    /// </summary>
    /// <remarks>
    /// The slug is the canonical value the spec fixes for <c>integration</c>, and it is
    /// the half of the identity that is stable across instances — a local display name
    /// is renamed freely, a slug is what both catalogues agree on. The importer maps it
    /// back to whatever this side calls that integration.
    /// <para>
    /// The local id does not travel beside the name. Node objects are hashed and that
    /// hash is what §8's round trip compares; a GUID that means something only on the
    /// instance that wrote it makes the fingerprint instance-specific, so the same
    /// workflow exported from two instances would never come back equal to itself.
    /// </para>
    /// </remarks>
    public static JsonElement WithPortableKeys(
        JsonElement nodes,
        IReadOnlyDictionary<Guid, string> credentialNames,
        IReadOnlyDictionary<Guid, string> repositoryNames,
        IReadOnlyDictionary<string, string> integrationSlugs)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return nodes;

        return Mutate(nodes, (_, overrides) =>
        {
            ToPortableName(overrides, "credential_id", "credential", credentialNames);
            ToPortableName(overrides, "repository_id", "repository", repositoryNames);
            if (TryName(overrides, "integration", out var integration)
                && integrationSlugs.TryGetValue(integration, out var slug)
                && !string.Equals(integration, slug, StringComparison.Ordinal))
                overrides["integration"] = slug;
        });
    }

    // The canonical key carries the name; the legacy id key is dropped. A value that is
    // not a GUID is left alone — a `{{ … }}` template is resolved per run, so it is a
    // runtime reference rather than this instance's id, and removing it would break the
    // node. An id whose row this instance no longer has still goes: an unresolvable
    // local GUID is no more portable than a resolvable one.
    private static void ToPortableName(
        JsonObject overrides, string idKey, string nameKey, IReadOnlyDictionary<Guid, string> names)
    {
        if (!TryGuid(overrides, idKey, out var id)) return;
        if (!overrides.ContainsKey(nameKey) && names.TryGetValue(id, out var name))
            overrides[nameKey] = name;
        overrides.Remove(idKey);
    }

    // ─── import ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the legacy id keys of bundle/SPEC.md §4 with the canonical name key,
    /// using the mapping the bundle's own <c>dependencies</c> carry. A legacy key with
    /// no mapping and no canonical key beside it is collected in
    /// <paramref name="untranslatable"/> (node id and key) for the caller to refuse.
    /// </summary>
    public static JsonElement TranslateLegacyKeys(
        JsonElement nodes, BundleDependencies deps, ICollection<string> untranslatable)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return nodes;

        var integrationsById = deps.Integrations.ToDictionary(i => i.Id, i => i);
        var actionsById = deps.Integrations
            .SelectMany(i => i.Actions)
            .GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var serversById = deps.McpServers.Where(m => m.Id is not null)
            .GroupBy(m => m.Id!.Value).ToDictionary(g => g.Key, g => g.First());
        var credentialsById = deps.Credentials.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var repositoriesById = deps.Repositories.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());

        return Mutate(nodes, (nodeId, overrides) =>
        {
            // The three "identity lives on the other instance" keys: the foreign GUID
            // is meaningless here, so it is dropped once the name is in place. The
            // name is what every handler on this side resolves.
            Translate(overrides, nodeId, "integration_id", "integration", untranslatable,
                id => integrationsById.TryGetValue(id, out var i)
                    ? (string.IsNullOrWhiteSpace(i.Slug) ? i.Name : i.Slug) : null,
                dropLegacy: true);
            Translate(overrides, nodeId, "action_id", "action", untranslatable,
                id => actionsById.TryGetValue(id, out var a) ? a.Name : null, dropLegacy: true);
            Translate(overrides, nodeId, "mcp_server_id", "server", untranslatable,
                id => serversById.TryGetValue(id, out var m) ? m.Name : null, dropLegacy: true);
            // These two ids are rewritten to local rows by Rewrite() once the name has
            // resolved, so they stay beside the name.
            Translate(overrides, nodeId, "credential_id", "credential", untranslatable,
                id => credentialsById.TryGetValue(id, out var c) ? c.Name : null, dropLegacy: false);
            Translate(overrides, nodeId, "repository_id", "repository", untranslatable,
                id => repositoriesById.TryGetValue(id, out var r) ? r.Name : null, dropLegacy: false);
        });
    }

    private static void Translate(
        JsonObject overrides, string nodeId, string legacyKey, string canonicalKey,
        ICollection<string> untranslatable, Func<Guid, string?> lookup, bool dropLegacy)
    {
        if (!overrides.ContainsKey(legacyKey)) return;
        // A templated value is resolved per run; there is nothing to translate.
        if (!TryGuid(overrides, legacyKey, out var id)) return;

        var hasCanonical = overrides[canonicalKey] is JsonValue v && v.TryGetValue<string>(out var s)
                           && !string.IsNullOrWhiteSpace(s);
        if (!hasCanonical)
        {
            var name = lookup(id);
            if (name is null)
            {
                untranslatable.Add($"node '{nodeId}' key '{legacyKey}' ({id})");
                return;
            }
            overrides[canonicalKey] = name;
        }
        if (dropLegacy) overrides.Remove(legacyKey);
    }

    /// <summary>What Rewrite() replaces, all of it keyed by what the node currently says.</summary>
    public sealed class RewritePlan
    {
        public IReadOnlyDictionary<Guid, Guid> SnippetIds { get; init; } = new Dictionary<Guid, Guid>();
        public IReadOnlyDictionary<Guid, Guid> WorkflowIds { get; init; } = new Dictionary<Guid, Guid>();
        /// <summary>Canonical credential name → local row. Rewrites an existing <c>credential_id</c> only.</summary>
        public IReadOnlyDictionary<string, Guid> CredentialIdsByName { get; init; } =
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Canonical repository name → local row. Always writes <c>repository_id</c>: the git handler reads it.</summary>
        public IReadOnlyDictionary<string, Guid> RepositoryIdsByName { get; init; } =
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Integration name as written → local name, when the two differ (matched by slug).</summary>
        public IReadOnlyDictionary<string, string> IntegrationNames { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty =>
            SnippetIds.Count == 0 && WorkflowIds.Count == 0 && CredentialIdsByName.Count == 0
            && RepositoryIdsByName.Count == 0 && IntegrationNames.Count == 0;
    }

    // Returns `nodes` with every referenced snippet GUID replaced via `idMap`. Ids
    // absent from the map are left untouched: the sentinel __start__ / __end__ /
    // subflow nodes carry non-GUID snippet ids, and anything genuinely unresolved was
    // already rejected by the resolver before this runs.
    public static JsonElement Remap(JsonElement nodes, IReadOnlyDictionary<Guid, Guid> idMap) =>
        Rewrite(nodes, new RewritePlan { SnippetIds = idMap });

    /// <summary>
    /// One pass over the nodes applying the whole <see cref="RewritePlan"/>. Anything the
    /// plan does not mention is written back verbatim.
    /// </summary>
    public static JsonElement Rewrite(JsonElement nodes, RewritePlan plan)
    {
        if (nodes.ValueKind != JsonValueKind.Array || plan.IsEmpty) return nodes;

        var array = JsonNode.Parse(nodes.GetRawText()) as JsonArray;
        if (array is null) return nodes;

        foreach (var item in array)
        {
            if (item is not JsonObject node) continue;

            if (TryGuid(node, "snippet_id", out var snippetId) && plan.SnippetIds.TryGetValue(snippetId, out var localSnippet))
                node["snippet_id"] = localSnippet.ToString();

            if (node["config_overrides"] is not JsonObject overrides) continue;

            if (IsSubflow(node) && TryGuid(overrides, "subflow_workflow_id", out var child)
                && plan.WorkflowIds.TryGetValue(child, out var localWorkflow))
                overrides["subflow_workflow_id"] = localWorkflow.ToString();

            if (TryName(overrides, "credential", out var credential)
                && plan.CredentialIdsByName.TryGetValue(credential, out var credentialId)
                && overrides.ContainsKey("credential_id"))
                overrides["credential_id"] = credentialId.ToString();

            if (TryName(overrides, "repository", out var repository)
                && plan.RepositoryIdsByName.TryGetValue(repository, out var repositoryId))
                overrides["repository_id"] = repositoryId.ToString();

            if (TryName(overrides, "integration", out var integration)
                && plan.IntegrationNames.TryGetValue(integration, out var localName))
                overrides["integration"] = localName;
        }

        return ToElement(array);
    }

    // ─── plumbing ──────────────────────────────────────────────────────────────

    private static JsonElement Mutate(JsonElement nodes, Action<string, JsonObject> edit)
    {
        var array = JsonNode.Parse(nodes.GetRawText()) as JsonArray;
        if (array is null) return nodes;

        foreach (var item in array)
        {
            if (item is not JsonObject node) continue;
            if (node["config_overrides"] is not JsonObject overrides) continue;
            var nodeId = node["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) ? id : "?";
            edit(nodeId, overrides);
        }

        return ToElement(array);
    }

    private static JsonElement ToElement(JsonNode node) =>
        JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    // Same rule as the JsonElement overload above: the sentinel OR the type word.
    private static bool IsSubflow(JsonObject node) =>
        (node["snippet_id"] is JsonValue v && v.TryGetValue<string>(out var s)
         && string.Equals(s, SubflowLiteral, StringComparison.Ordinal))
        || (node["type"] is JsonValue t && t.TryGetValue<string>(out var type)
            && string.Equals(type, SubflowLiteral, StringComparison.Ordinal));

    private static bool TryGuid(JsonElement obj, string property, out Guid value)
    {
        value = Guid.Empty;
        return obj.TryGetProperty(property, out var el)
               && el.ValueKind == JsonValueKind.String
               && Guid.TryParse(el.GetString(), out value);
    }

    private static bool TryGuid(JsonObject obj, string property, out Guid value)
    {
        value = Guid.Empty;
        return obj[property] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out value);
    }

    // A name is only a dependency when it is a plain literal. A value still carrying a
    // `{{ … }}` template is resolved per run from step output or the trigger payload,
    // so there is no single name to check at import time and claiming one is missing
    // would be wrong.
    private static bool TryName(JsonElement obj, string property, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        return IsLiteralName(el.GetString(), out value);
    }

    private static bool TryName(JsonObject obj, string property, out string value)
    {
        value = string.Empty;
        return obj[property] is JsonValue v && v.TryGetValue<string>(out var s) && IsLiteralName(s, out value);
    }

    private static bool IsLiteralName(string? raw, out string value)
    {
        value = string.Empty;
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw) || raw.Contains("{{", StringComparison.Ordinal)) return false;
        value = raw;
        return true;
    }
}
