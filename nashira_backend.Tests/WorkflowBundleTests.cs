using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using IntegrationEntity = nashira_backend.Data.Models.Integration;
using SnippetEntity = nashira_backend.Data.Models.Snippet;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Tests;

// The portable form of a workflow, and the reason it exists.
//
// A plain YAML export carries nodes verbatim, which means it carries nothing but this
// instance's GUIDs. Handed to a second instance none of them resolve, and because the
// reference gate rejects an unresolvable snippet_id at write time, the import is
// refused outright — which is what happened to every FlowWeaver workflow brought here.
// A bundle carries the definition of each snippet the nodes name, so the receiving
// instance recreates what it lacks rather than rejecting the file or, worse, stubbing
// it into a run that goes green having done no work.
public class WorkflowBundleTests
{
    // ─── fixtures ────────────────────────────────────────────────────────────

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bundle-{Guid.NewGuid()}")
            .Options);

    private static WorkflowBundleService NewService(AppDbContext db, ICurrentUser? user = null) =>
        BundleTestKit.NewService(db, user ?? new FakeUser(), new FakeRegistry());

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static BundleSnippet ForeignSnippet(
        Guid id, string name, string type = "ping", string? slug = null,
        bool networkEnabled = false, string targetMode = "once", int maxParallel = 0) => new()
        {
            Id = id,
            Slug = slug,
            Name = name,
            Type = type,
            Description = "from the other instance",
            TargetMode = targetMode,
            MaxParallel = maxParallel,
            TimeoutSeconds = 30,
            NetworkEnabled = networkEnabled,
        };

    private static WorkflowBundle BundleWith(Guid snippetId, params BundleSnippet[] snippets) => new()
    {
        Workflow = new BundleWorkflow { Name = "reachability sweep" },
        Nodes = Json($$"""
            [{"id":"start","snippet_id":"__start__"},
             {"id":"probe","snippet_id":"{{snippetId}}"},
             {"id":"end","snippet_id":"__end__"}]
            """),
        Edges = Json("""[{"source":"start","target":"probe","type":"always"}]"""),
        Dependencies = new BundleDependencies { Snippets = [.. snippets] },
    };

    // ─── recognition ─────────────────────────────────────────────────────────

    // Detection is by explicit marker, never by shape. `snippet_id` on the first node
    // is a shape the plain export, a raw workflow.v1 document and several foreign
    // formats all share.
    [Theory]
    [InlineData("flow_weaver.workflow_bundle")]
    [InlineData("nashira.workflow_bundle")]
    [InlineData("netora.workflow_bundle")]
    public void A_bundle_is_recognised_by_its_kind_marker(string kind)
    {
        Assert.True(WorkflowBundleReader.LooksLikeBundle(
            $$"""{"kind":"{{kind}}","schema_version":"v2"}"""));
    }

    [Fact]
    public void A_plain_workflow_document_is_not_mistaken_for_a_bundle()
    {
        Assert.False(WorkflowBundleReader.LooksLikeBundle(
            """{"name":"wf","nodes":[{"id":"a","snippet_id":"__start__"}]}"""));
        Assert.False(WorkflowBundleReader.LooksLikeBundle("name: wf\nnodes: []"));
        Assert.False(WorkflowBundleReader.LooksLikeBundle("not json at all"));
        Assert.False(WorkflowBundleReader.LooksLikeBundle(""));
    }

    // A bundle that half-imports is worse than one refused with a version to act on.
    [Fact]
    public void An_unknown_schema_version_is_refused_rather_than_partly_read()
    {
        var raw = """
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v9",
             "workflow":{"name":"wf"},"nodes":[],"edges":[]}
            """;
        var ex = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(raw));
        Assert.Equal("bundle_version_unsupported", ex.Code);
        Assert.Contains("v9", ex.Message);
    }

    // The one guarantee the format makes: what the nodes name, the file carries.
    [Fact]
    public void A_bundle_that_does_not_carry_a_referenced_snippet_is_incomplete()
    {
        var orphan = Guid.NewGuid();
        var raw = $$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v2",
             "workflow":{"name":"wf"},
             "nodes":[{"id":"a","snippet_id":"{{{orphan}}}"}],"edges":[],
             "dependencies":{"snippets":[]}}
            """;
        var ex = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(raw));
        Assert.Equal("bundle_incomplete", ex.Code);
        Assert.Contains(orphan.ToString(), ex.Message);
    }

    // ─── resolution ──────────────────────────────────────────────────────────

    // The case the whole format exists for: a workflow from another instance whose
    // snippet this one has never seen.
    [Fact]
    public async Task A_snippet_this_instance_lacks_is_recreated_from_the_definition_that_travelled()
    {
        var db = NewDb();
        var foreignId = Guid.NewGuid();
        var bundle = BundleWith(foreignId, ForeignSnippet(foreignId, "TCP probe", slug: "tcp-probe"));

        var resolution = await NewService(db).ResolveAsync(bundle, default);

        var created = Assert.Single(resolution.SnippetsToCreate);
        Assert.Equal("TCP probe", created.Name);
        Assert.Equal("ping", created.Type);
        // The source slug is kept when free, so a later bundle from either side
        // resolves by identity rather than falling back to a name match.
        Assert.Equal("tcp-probe", created.Slug);
        // It arrived unverified: this instance has neither reviewed nor run it.
        Assert.False(created.Verified);
        Assert.Equal(created.SnippetId, resolution.IdMap[foreignId]);
    }

    // Reuse, not duplication — and the node ends up pointing at the local row.
    [Fact]
    public async Task A_snippet_already_here_is_reused_and_the_node_repointed()
    {
        var db = NewDb();
        var localId = Guid.NewGuid();
        db.Snippets.Add(new SnippetEntity
        {
            SnippetId = localId, Name = "TCP probe", Slug = "tcp-probe",
            Type = "ping", IsActive = true,
        });
        await db.SaveChangesAsync();

        var foreignId = Guid.NewGuid();
        var bundle = BundleWith(foreignId, ForeignSnippet(foreignId, "TCP probe", slug: "tcp-probe"));

        var resolution = await NewService(db).ResolveAsync(bundle, default);

        Assert.Empty(resolution.SnippetsToCreate);
        Assert.Equal(localId, resolution.IdMap[foreignId]);

        var remapped = NodeReferences.Remap(bundle.Nodes, resolution.IdMap);
        var probe = remapped.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "probe");
        Assert.Equal(localId.ToString(), probe.GetProperty("snippet_id").GetString());
        // The sentinels are not GUIDs and must survive the rewrite untouched.
        var start = remapped.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "start");
        Assert.Equal("__start__", start.GetProperty("snippet_id").GetString());
    }

    // A slug match wins over a name match, because a slug is identity and a name is a
    // label someone can change.
    [Fact]
    public async Task A_renamed_local_snippet_still_matches_by_slug()
    {
        var db = NewDb();
        var localId = Guid.NewGuid();
        db.Snippets.Add(new SnippetEntity
        {
            SnippetId = localId, Name = "probe (renamed by an operator)", Slug = "tcp-probe",
            Type = "ping", IsActive = true,
        });
        await db.SaveChangesAsync();

        var foreignId = Guid.NewGuid();
        var resolution = await NewService(db).ResolveAsync(
            BundleWith(foreignId, ForeignSnippet(foreignId, "TCP probe", slug: "tcp-probe")), default);

        Assert.Empty(resolution.SnippetsToCreate);
        Assert.Equal(localId, resolution.IdMap[foreignId]);
    }

    // Matching by name is a fallback, and the operator is told it happened — two
    // instances can easily hold different steps under the same label.
    [Fact]
    public async Task A_name_match_resolves_but_says_so()
    {
        var db = NewDb();
        db.Snippets.Add(new SnippetEntity
        {
            SnippetId = Guid.NewGuid(), Name = "TCP probe", Slug = "some-other-slug",
            Type = "ping", IsActive = true,
        });
        await db.SaveChangesAsync();

        var foreignId = Guid.NewGuid();
        var resolution = await NewService(db).ResolveAsync(
            BundleWith(foreignId, ForeignSnippet(foreignId, "TCP probe", slug: "tcp-probe")), default);

        Assert.Empty(resolution.SnippetsToCreate);
        Assert.Contains(resolution.Notes, n => n.Contains("by name", StringComparison.OrdinalIgnoreCase));
    }

    // ─── what must not travel ────────────────────────────────────────────────

    // network_enabled lifts the python sandbox's network isolation and is admin-gated
    // locally. A file from another instance must not be able to grant it — and the
    // drop has to be visible, or the operator finds out from a failing run.
    [Fact]
    public async Task Network_access_never_arrives_with_the_bundle()
    {
        var db = NewDb();
        var foreignId = Guid.NewGuid();
        var bundle = BundleWith(foreignId,
            ForeignSnippet(foreignId, "scraper", type: "python_snippet", networkEnabled: true));

        var resolution = await NewService(db).ResolveAsync(bundle, default);

        var created = Assert.Single(resolution.SnippetsToCreate);
        Assert.False(created.NetworkEnabled);
        Assert.Contains(resolution.Notes, n => n.Contains("network-enabled", StringComparison.OrdinalIgnoreCase));
    }

    // ─── what this build cannot run ──────────────────────────────────────────

    // FlowWeaver ships handlers this build does not (ansible, netconf, snmp_v3, …).
    // Storing such a snippet would defer the discovery to a device: the workflow would
    // validate, promote, schedule, and then fail with `unknown_handler` at 3am.
    [Fact]
    public async Task A_handler_type_this_build_cannot_execute_is_refused_at_import()
    {
        var db = NewDb();
        var foreignId = Guid.NewGuid();
        var bundle = BundleWith(foreignId,
            ForeignSnippet(foreignId, "run the playbook", type: "ansible"));

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => NewService(db).ResolveAsync(bundle, default));

        Assert.Equal("bundle_dependencies_missing", ex.Code);
        Assert.Contains("ansible", ex.Message);
        // The refusal has to say what IS available, or the operator cannot act on it.
        Assert.Contains("ping", ex.Message);
    }

    // ─── dependencies that cannot be invented ────────────────────────────────

    // An integration is credentials on this instance. Fabricating the row would produce
    // a workflow that looks wired and fails on the first call.
    [Fact]
    public async Task A_missing_integration_is_fatal_and_named()
    {
        var db = NewDb();
        var foreignId = Guid.NewGuid();
        var bundle = BundleWith(foreignId, ForeignSnippet(foreignId, "call netbox", type: "integration_action"));
        bundle.Dependencies.Integrations.Add(new BundleIntegration
        {
            Id = Guid.NewGuid(), Name = "NetBox", Type = "netbox",
            Slug = "netbox", BaseUrl = "https://netbox.example",
        });

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => NewService(db).ResolveAsync(bundle, default));

        Assert.Equal("bundle_dependencies_missing", ex.Code);
        Assert.Contains("NetBox", ex.Message);
        // The source base_url is informational but it is what lets a human confirm the
        // two sides mean the same system.
        Assert.Contains("netbox.example", ex.Message);
    }

    [Fact]
    public async Task An_integration_present_here_resolves_cleanly()
    {
        var db = NewDb();
        db.Integrations.Add(new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(), Name = "NetBox", Slug = "netbox",
            Type = "netbox", BaseUrl = "https://netbox.internal", IsActive = true, Enabled = true,
        });
        await db.SaveChangesAsync();

        var foreignId = Guid.NewGuid();
        var bundle = BundleWith(foreignId, ForeignSnippet(foreignId, "call netbox", type: "integration_action"));
        bundle.Dependencies.Integrations.Add(new BundleIntegration
        {
            Id = Guid.NewGuid(), Name = "NetBox", Type = "netbox", Slug = "netbox",
        });

        var resolution = await NewService(db).ResolveAsync(bundle, default);
        Assert.Single(resolution.SnippetsToCreate);
    }

    // bundle/SPEC.md §5.3: an integration found by slug under a different local name is
    // usable. This build's handler resolves by name, so the node is rewritten to the
    // local name and the operator is told — silently binding would hide a possible
    // wrong-system match, refusing would reject a legitimate rename.
    [Fact]
    public async Task An_integration_matched_by_slug_but_named_differently_is_renamed_and_noted()
    {
        var db = NewDb();
        db.Integrations.Add(new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(), Name = "NetBox (lab)", Slug = "netbox",
            Type = "netbox", BaseUrl = "https://netbox.internal", IsActive = true, Enabled = true,
        });
        await db.SaveChangesAsync();

        var foreignId = Guid.NewGuid();
        var bundle = BundleWith(foreignId, ForeignSnippet(foreignId, "call netbox", type: "integration_action"));
        bundle.Dependencies.Integrations.Add(new BundleIntegration
        {
            Id = Guid.NewGuid(), Name = "NetBox", Type = "netbox", Slug = "netbox",
        });

        var resolution = await NewService(db).ResolveAsync(bundle, default);
        Assert.Contains(resolution.Notes, n => n.Contains("NetBox (lab)", StringComparison.Ordinal));
        Assert.Equal("NetBox (lab)", resolution.Plan.IntegrationNames["NetBox"]);
    }

    // ─── translation the operator has to know about ──────────────────────────

    // FlowWeaver's pool fan-out has no equivalent here. per_device is the nearest
    // honest reading, and saying nothing would let the difference surface as a run
    // that touched the wrong set of devices.
    [Fact]
    public async Task A_target_mode_with_no_local_equivalent_is_translated_and_reported()
    {
        var db = NewDb();
        var foreignId = Guid.NewGuid();
        var bundle = BundleWith(foreignId,
            ForeignSnippet(foreignId, "sweep the pool", targetMode: "per_pool"));

        var resolution = await NewService(db).ResolveAsync(bundle, default);

        var created = Assert.Single(resolution.SnippetsToCreate);
        Assert.Equal(SnippetEntity.TargetPerDevice, created.TargetMode);
        Assert.Contains(resolution.Notes, n => n.Contains("per_pool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dropped_max_parallel_is_reported()
    {
        var db = NewDb();
        var foreignId = Guid.NewGuid();
        var resolution = await NewService(db).ResolveAsync(
            BundleWith(foreignId, ForeignSnippet(foreignId, "wide fan-out", maxParallel: 8)), default);

        Assert.Contains(resolution.Notes, n => n.Contains("max_parallel", StringComparison.Ordinal));
    }

    // ─── round trip ──────────────────────────────────────────────────────────

    // Export then import on a second instance: the workflow arrives with its steps
    // intact rather than being refused, which is the whole point.
    [Fact]
    public async Task A_workflow_survives_export_and_import_into_a_fresh_instance()
    {
        var source = NewDb();
        var snippetId = Guid.NewGuid();
        source.Snippets.Add(new SnippetEntity
        {
            SnippetId = snippetId, Name = "TCP probe", Slug = "tcp-probe", Type = "ping",
            TargetMode = SnippetEntity.TargetOnce, TimeoutSeconds = 15,
            InputSchemaJson = """{"type":"object"}""", IsActive = true,
        });
        var workflowId = Guid.NewGuid();
        source.Workflows.Add(new WorkflowEntity
        {
            WorkflowId = workflowId,
            Name = "reachability sweep",
            NodesJson = $$"""
                [{"id":"start","snippet_id":"__start__"},
                 {"id":"probe","snippet_id":"{{snippetId}}"},
                 {"id":"end","snippet_id":"__end__"}]
                """,
            EdgesJson = """[{"source":"start","target":"probe","type":"always"}]""",
            Environment = WorkflowEntity.EnvProduction,
            IsActive = true,
        });
        await source.SaveChangesAsync();

        var bundle = await NewService(source).BuildAsync(workflowId, default);

        // Cross the wire exactly as the endpoint does.
        var wire = JsonSerializer.Serialize(bundle, WorkflowBundleReader.SerializerOptions);
        Assert.True(WorkflowBundleReader.LooksLikeBundle(wire));
        var received = WorkflowBundleReader.Parse(wire);

        var target = NewDb();
        var resolution = await NewService(target).ResolveAsync(received, default);

        var created = Assert.Single(resolution.SnippetsToCreate);
        Assert.Equal("TCP probe", created.Name);
        Assert.Equal(15, created.TimeoutSeconds);
        // The schema crosses as a JSON value and comes back pretty-printed, so the
        // contract is semantic equivalence rather than byte equality — it is a JSON
        // Schema, not a blob whose whitespace anyone depends on.
        Assert.Equal(
            JsonSerializer.Serialize(JsonDocument.Parse("""{"type":"object"}""").RootElement),
            JsonSerializer.Serialize(JsonDocument.Parse(created.InputSchemaJson!).RootElement));

        var remapped = NodeReferences.Remap(received.Nodes, resolution.IdMap);
        var probe = remapped.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "probe");
        Assert.Equal(created.SnippetId.ToString(), probe.GetProperty("snippet_id").GetString());

        // The source was in production; a bundle must never place a workflow straight
        // into production elsewhere. The endpoint stamps draft — assert the bundle only
        // reports the source environment rather than dictating it.
        Assert.Equal(WorkflowEntity.EnvProduction, received.Workflow.Environment);
    }

    // Credentials do not travel. This is the property that makes a bundle safe to
    // attach to an email.
    [Fact]
    public async Task An_exported_bundle_carries_no_credential_material()
    {
        var db = NewDb();
        db.Integrations.Add(new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(), Name = "NetBox", Slug = "netbox", Type = "netbox",
            BaseUrl = "https://netbox.internal", AuthConfig = """{"token":"s3cr3t-do-not-share"}""",
            HeadersJson = """{"X-Api-Key":"also-secret"}""", IsActive = true, Enabled = true,
        });
        var snippetId = Guid.NewGuid();
        db.Snippets.Add(new SnippetEntity
        {
            SnippetId = snippetId, Name = "call netbox", Slug = "call-netbox",
            Type = "integration_action", IsActive = true,
        });
        var workflowId = Guid.NewGuid();
        db.Workflows.Add(new WorkflowEntity
        {
            WorkflowId = workflowId,
            Name = "netbox sync",
            NodesJson = $$$"""
                [{"id":"call","snippet_id":"{{{snippetId}}}","config_overrides":{"integration":"NetBox","action":"list_devices"}}]
                """,
            EdgesJson = "[]",
            Environment = WorkflowEntity.EnvDraft,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var bundle = await NewService(db).BuildAsync(workflowId, default);
        var wire = JsonSerializer.Serialize(bundle, WorkflowBundleReader.SerializerOptions);

        Assert.DoesNotContain("s3cr3t-do-not-share", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("also-secret", wire, StringComparison.Ordinal);
        // The integration is still described enough to be recognised on the far side.
        Assert.Contains("NetBox", wire, StringComparison.Ordinal);
    }

    // A templated reference has no single name to check at import time, so claiming it
    // is missing would be wrong.
    [Fact]
    public void A_templated_integration_name_is_not_treated_as_a_dependency()
    {
        var refs = NodeReferences.Extract(Json("""
            [{"id":"call","snippet_id":"__start__","config_overrides":{"integration":"{{ input.system }}"}}]
            """));
        Assert.Empty(refs.IntegrationNames);
    }

    // The actual user scenario, against the wire shape FlowWeaver emits rather than
    // objects built in this file: its marker, its field names, its `max_parallel`, and
    // an input_schema carried as a JSON value where Nashira stores text.
    [Fact]
    public async Task A_bundle_serialised_by_flow_weaver_imports_here()
    {
        var snippetId = Guid.NewGuid();
        var raw = $$$"""
            {
              "schema_version": "v2",
              "kind": "flow_weaver.workflow_bundle",
              "exported_at": "2026-08-20T09:14:00Z",
              "workflow": {
                "name": "edge reachability",
                "description": "probes every edge switch",
                "environment": "production",
                "input_schema": { "type": "object", "properties": { "site": { "type": "string" } } },
                "metadata": { "author": "netops" }
              },
              "nodes": [
                { "id": "start", "snippet_id": "__start__" },
                { "id": "probe", "snippet_id": "{{{snippetId}}}", "type": "task",
                  "config_overrides": { "host": "{{ device.ip_address }}", "port": 22 } },
                { "id": "end", "snippet_id": "__end__" }
              ],
              "edges": [
                { "source": "start", "target": "probe", "type": "always" },
                { "source": "probe", "target": "end", "type": "success" }
              ],
              "dependencies": {
                "snippets": [
                  {
                    "id": "{{{snippetId}}}",
                    "slug": "edge-tcp-probe",
                    "name": "Edge TCP probe",
                    "type": "ping",
                    "description": "TCP 22 against an edge switch",
                    "code": null,
                    "script_language": null,
                    "input_schema": { "type": "object", "required": ["host"] },
                    "output_schema": { "type": "object" },
                    "target_mode": "per_device",
                    "max_parallel": 4,
                    "timeout_seconds": 20,
                    "idempotency": "idempotent",
                    "logic_diagram_mermaid": "graph TD; A-->B;",
                    "network_enabled": false
                  }
                ],
                "integrations": []
              }
            }
            """;

        Assert.True(WorkflowBundleReader.LooksLikeBundle(raw));
        var bundle = WorkflowBundleReader.Parse(raw);

        var db = NewDb();
        var resolution = await NewService(db).ResolveAsync(bundle, default);

        var created = Assert.Single(resolution.SnippetsToCreate);
        Assert.Equal("Edge TCP probe", created.Name);
        Assert.Equal("edge-tcp-probe", created.Slug);
        Assert.Equal(SnippetEntity.TargetPerDevice, created.TargetMode);
        Assert.Equal(20, created.TimeoutSeconds);
        Assert.Equal("idempotent", created.Idempotency);
        // The schema crossed as a JSON value and is stored as text on this side.
        Assert.Contains("\"required\"", created.InputSchemaJson);
        // FlowWeaver's fan-out width has no column here, and the drop is reported.
        Assert.Contains(resolution.Notes, n => n.Contains("max_parallel", StringComparison.Ordinal));

        // The node now points at the local row, and the sentinels are untouched.
        var remapped = NodeReferences.Remap(bundle.Nodes, resolution.IdMap);
        var probe = remapped.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "probe");
        Assert.Equal(created.SnippetId.ToString(), probe.GetProperty("snippet_id").GetString());
        // config_overrides survives the rewrite verbatim — the template included.
        Assert.Equal("{{ device.ip_address }}",
            probe.GetProperty("config_overrides").GetProperty("host").GetString());
    }

    // ─── fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "tester";
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => true;
    }

    // The handler types this build registers, which is what decides whether an
    // imported snippet can run at all.
    private sealed class FakeRegistry : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes =>
            ["ping", "transform", "rest_call", "integration_action", "ssh", "mcp_call", "python_snippet", "git"];

        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => null;
    }
}
