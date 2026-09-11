using System.Text.Json;
using System.Text.Json.Nodes;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Tests.Conformance;

// Family `bundle` (workflow-v1-conformance/bundle/SPEC.md §7).
//
//   { bundle, local? } -> { outcome, codes, schema_hash, notes, notes_empty,
//                           created, workflow, triggers, requires }
//   { bundle, local?, round_trip: true }
//                      -> the above plus { schema_hash_unchanged, requires }
//
// `local` is what the RECEIVING instance already holds, and it has to be part of the
// vector: §5 refuses a bundle whose integrations, credentials, repositories or handler
// types are missing, so "refused" and "imported" are answers about a pair (bundle,
// instance) and a vector that named only the bundle would be asserting half a question.
//
//   local.snippet_types   the handler registry's KnownTypes
//   local.integrations    [{ slug, name, type, actions: [{ name }] }]
//   local.credentials     [{ name }]
//   local.repositories    [{ name }]
//   local.mcp_servers     [{ name }]
//
// `codes` is the SET of refusal codes the failure names, not just the one the product
// happens to put on the exception: §7 says a refusal "names every missing dependency,
// unsupported capability or untranslatable key AT ONCE", so a vector that could only
// see the first would let the other two rot.
//
// `schema_hash` is the §8 hash — over the BUNDLE's own wire nodes+edges, with
// `snippet_id` and `subflow_workflow_id` normalized, never over the stored row. The two
// are deliberately different things: the row may carry whatever local vocabulary the
// product runs on, while the wire form carries portable identities only. Comparing
// stored rows would assert that two products store workflows identically, which is not
// the contract and is not true.
public sealed partial class NashiraAdapter
{
    private JsonElement? Bundle(JsonElement input)
    {
        var raw = Prop(input, "bundle") ?? input;
        var local = Prop(input, "local");

        using var db = BundleTestKit.NewDb();
        Seed(db, local);
        var service = BundleTestKit.NewService(db, new ConformanceUser(), new DeclaredHandlers(local));

        WorkflowBundle bundle;
        try
        {
            bundle = WorkflowBundleReader.Parse(raw.GetRawText());
        }
        catch (ValidationException ex)
        {
            return Refused(ex);
        }

        var wireHash = WireHash(bundle.Nodes, bundle.Edges);

        BundleImportResult imported;
        try
        {
            imported = service.ImportAsync(bundle, null, null, default).GetAwaiter().GetResult();
        }
        catch (ValidationException ex)
        {
            return Refused(ex);
        }

        // Keyed by name rather than listed, so a vector can assert the fields §6 fixes
        // for a given trigger without also pinning the ones it does not (a cron trigger
        // has no HMAC secret to be fresh; a webhook does).
        var triggers = db.WorkflowTriggers
            .Where(t => t.WorkflowId == imported.Workflow.WorkflowId)
            .ToDictionary(t => t.Name, t => new
            {
                name = t.Name,
                type = t.Type,
                enabled = t.Enabled,
                has_secret = t.EncryptedSecret != null,
                targets = t.TargetDevicesJson,
            });

        var result = new Dictionary<string, object?>
        {
            ["outcome"] = "imported",
            ["codes"] = Array.Empty<string>(),
            ["schema_hash"] = wireHash,
            ["notes"] = imported.Notes,
            // §7: "Silence means nothing was degraded." That is the assertion a vector
            // can actually make about notes — the wording of each note is a product's
            // own, the presence or absence of any is the contract's.
            ["notes_empty"] = imported.Notes.Count == 0,
            ["created"] = new
            {
                snippets = imported.CreatedSnippets.Count,
                workflows = imported.CreatedWorkflows.Count,
                triggers = imported.CreatedTriggers.Count,
            },
            ["workflow"] = new
            {
                environment = imported.Workflow.Environment,
                version = imported.Workflow.Version,
                schema_version = imported.Workflow.SchemaVersion,
                has_simulation = imported.Workflow.LastSimulationId != null,
            },
            ["triggers"] = triggers,
            ["requires"] = Requires(bundle.Requires),
            ["nodes"] = NodesOf(db, imported.Workflow.WorkflowId),
            // The stored node payloads, flattened by node id, plus the EXACT key set of
            // each. The key set is what proves a legacy id key was dropped rather than
            // left lying beside the canonical name — an absence no "these fields are
            // present" comparison can see.
            ["node_overrides"] = Overrides(db, imported.Workflow.WorkflowId),
            ["node_override_keys"] = OverrideKeys(db, imported.Workflow.WorkflowId),
        };

        if (Prop(input, "round_trip") is { ValueKind: JsonValueKind.True })
        {
            var reexported = service.BuildAsync(imported.Workflow.WorkflowId, default).GetAwaiter().GetResult();
            result["schema_hash_unchanged"] = WireHash(reexported.Nodes, reexported.Edges) == wireHash;
            result["requires"] = Requires(reexported.Requires);
            // What went back ON THE WIRE, which is the only place §4 constrains the
            // value: "what it stores is its business; what it puts on the wire is this".
            result["exported_node_overrides"] = WireOverrides(reexported.Nodes);
            result["exported_node_override_keys"] = WireOverrides(reexported.Nodes).ToDictionary(
                kv => kv.Key,
                kv => kv.Value.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
        }

        return JsonSerializer.SerializeToElement(result);
    }

    // Every code the refusal names. `ex.Code` is the most structural one; the message
    // carries a `[code]` marker per family so the operator sees all of them at once.
    private static JsonElement Refused(ValidationException ex)
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal) { ex.Code };
        foreach (var known in KnownRefusalCodes)
            if (ex.Message.Contains($"[{known}]", StringComparison.Ordinal))
                codes.Add(known);

        return JsonSerializer.SerializeToElement(new
        {
            outcome = "refused",
            codes = codes.ToList(),
            created = new { snippets = 0, workflows = 0, triggers = 0 },
        });
    }

    private static readonly string[] KnownRefusalCodes =
    [
        "bundle_version_unsupported", "bundle_capability_unsupported", "bundle_incomplete",
        "bundle_dependencies_missing", "bundle_reference_untranslatable", "bundle_subflow_cycle",
    ];

    private static object Requires(BundleRequires r) => new
    {
        snippet_types = r.SnippetTypes,
        capabilities = r.Capabilities,
        secrets = r.Secrets.Select(s => new { @ref = s.Ref, used_by = s.UsedBy }).ToList(),
    };

    private static JsonElement NodesOf(AppDbContext db, Guid workflowId)
    {
        var json = db.Workflows.Where(w => w.WorkflowId == workflowId).Select(w => w.NodesJson).Single();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Overrides(AppDbContext db, Guid workflowId) =>
        NodesOf(db, workflowId).EnumerateArray()
            .Where(n => n.TryGetProperty("config_overrides", out var c) && c.ValueKind == JsonValueKind.Object)
            .ToDictionary(n => n.GetProperty("id").GetString()!, n => n.GetProperty("config_overrides").Clone());

    private static Dictionary<string, JsonElement> WireOverrides(JsonElement nodes) =>
        nodes.ValueKind != JsonValueKind.Array
            ? []
            : nodes.EnumerateArray()
                .Where(n => n.TryGetProperty("config_overrides", out var c) && c.ValueKind == JsonValueKind.Object)
                .ToDictionary(n => n.GetProperty("id").GetString()!, n => n.GetProperty("config_overrides").Clone());

    private static Dictionary<string, List<string>> OverrideKeys(AppDbContext db, Guid workflowId) =>
        Overrides(db, workflowId).ToDictionary(
            kv => kv.Key,
            kv => kv.Value.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());

    // ── §8's hash ────────────────────────────────────────────────────────────

    // The canonical hash of a bundle's WIRE nodes+edges, with `snippet_id` and
    // `subflow_workflow_id` replaced by an ordinal placeholder assigned in order of
    // first appearance. §8 requires exactly that normalization
    // (`remap:snippet_ids`, `remap:workflow_ids`): those two are remapped to the
    // receiving instance's rows BY DESIGN, so the round trip must tolerate them
    // changing while everything else stays byte-identical. The sentinels
    // (`__start__`, `__end__`, `subflow`) are structure, not identity, and are kept.
    internal static string WireHash(JsonElement nodes, JsonElement edges)
    {
        var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
        var workflows = new Dictionary<string, string>(StringComparer.Ordinal);

        if (JsonNode.Parse(nodes.ValueKind == JsonValueKind.Undefined ? "[]" : nodes.GetRawText()) is not JsonArray array)
            return WorkflowCanonicalizer.ComputeSchemaHash(nodes, edges);

        foreach (var item in array)
        {
            if (item is not JsonObject node) continue;

            if (node["snippet_id"] is JsonValue sv && sv.TryGetValue<string>(out var snippetId)
                && Guid.TryParse(snippetId, out _))
                node["snippet_id"] = Placeholder(snippets, snippetId, "snippet");

            if (node["config_overrides"] is JsonObject overrides
                && overrides["subflow_workflow_id"] is JsonValue wv && wv.TryGetValue<string>(out var childId)
                && Guid.TryParse(childId, out _))
                overrides["subflow_workflow_id"] = Placeholder(workflows, childId, "workflow");
        }

        using var normalized = JsonDocument.Parse(array.ToJsonString());
        return WorkflowCanonicalizer.ComputeSchemaHash(
            normalized.RootElement,
            edges.ValueKind == JsonValueKind.Undefined ? JsonDocument.Parse("[]").RootElement : edges);
    }

    private static string Placeholder(Dictionary<string, string> seen, string id, string kind)
    {
        if (!seen.TryGetValue(id, out var placeholder))
        {
            placeholder = $"#{kind}:{seen.Count}";
            seen[id] = placeholder;
        }
        return placeholder;
    }

    // ── the receiving instance ───────────────────────────────────────────────

    private static void Seed(AppDbContext db, JsonElement? local)
    {
        foreach (var i in Each(local, "integrations"))
        {
            var integrationId = Guid.NewGuid();
            db.Integrations.Add(new IntegrationEntity
            {
                IntegrationId = integrationId,
                Name = Str(i, "name") ?? Str(i, "slug") ?? "integration",
                Slug = Str(i, "slug") ?? string.Empty,
                Type = Str(i, "type") ?? "rest",
                BaseUrl = Str(i, "base_url") ?? "https://example.invalid",
            });

            // §5.3 checks the ACTION as well as the integration, so an instance that has
            // the system but not the operation is still a missing dependency.
            foreach (var a in Each(i, "actions"))
            {
                db.IntegrationActions.Add(new nashira_backend.Data.Models.IntegrationAction
                {
                    IntegrationActionId = Guid.NewGuid(),
                    IntegrationId = integrationId,
                    Name = Str(a, "name") ?? "action",
                    Method = Str(a, "method") ?? "GET",
                    Path = Str(a, "path") ?? "/",
                });
            }
        }

        foreach (var c in Each(local, "credentials"))
        {
            db.Credentials.Add(new nashira_backend.Data.Models.Credential
            {
                CredentialId = Guid.NewGuid(),
                Name = Str(c, "name") ?? "credential",
                Type = Str(c, "type") ?? "ssh",
            });
        }

        foreach (var r in Each(local, "repositories"))
        {
            db.GitRepositories.Add(new nashira_backend.Data.Models.GitRepository
            {
                GitRepositoryId = Guid.NewGuid(),
                Name = Str(r, "name") ?? "repo",
                Url = Str(r, "remote_url") ?? "https://example.invalid/repo.git",
            });
        }

        foreach (var m in Each(local, "mcp_servers"))
        {
            db.McpServers.Add(new nashira_backend.Data.Models.McpServer
            {
                McpServerId = Guid.NewGuid(),
                Name = Str(m, "name") ?? "mcp",
            });
        }

        db.SaveChanges();
    }

    private static IEnumerable<JsonElement> Each(JsonElement? holder, string property) =>
        Prop(holder, property) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    // The registry's KnownTypes is the whole handler-support gate (§2.1), so the vector
    // states which handlers this instance has.
    private sealed class DeclaredHandlers : ISnippetHandlerRegistry
    {
        public DeclaredHandlers(JsonElement? local) =>
            KnownTypes = Prop(local, "snippet_types") is { ValueKind: JsonValueKind.Array } a
                ? a.EnumerateArray().Select(t => t.GetString() ?? string.Empty).ToList()
                : ["ping", "transform", "ssh", "rest_call", "integration_action", "mcp_call", "git", "python_snippet"];

        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => null;

        public IReadOnlyCollection<string> KnownTypes { get; }
    }

    private sealed class ConformanceUser : ICurrentUser
    {
        public Guid UserId { get; } = Guid.Parse("44444444-4444-4444-4444-444444444444");
        public string? Username => "conformance";
        public IReadOnlyList<string> Roles { get; } = ["admin"];
        public bool IsAuthenticated => true;
    }
}
