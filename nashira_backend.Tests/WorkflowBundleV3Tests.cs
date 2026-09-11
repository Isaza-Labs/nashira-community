using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using CredentialEntity = nashira_backend.Data.Models.Credential;
using IntegrationEntity = nashira_backend.Data.Models.Integration;
using RepositoryEntity = nashira_backend.Data.Models.GitRepository;
using SecretEntity = nashira_backend.Data.Models.Secret;
using SnippetEntity = nashira_backend.Data.Models.Snippet;
using TriggerEntity = nashira_backend.Data.Models.WorkflowTrigger;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Tests;

// The v3 interchange bundle (workflow-v1-conformance/bundle/SPEC.md): what travels,
// what is refused, and what the operator is told. Every test here pins a sentence of
// that document, because the other product is implementing the same sentences
// concurrently and the spec is the only place the two meet.
public class WorkflowBundleV3Tests
{
    // ─── fixtures ────────────────────────────────────────────────────────────

    private static AppDbContext NewDb() => BundleTestKit.NewDb();

    private static WorkflowBundleService NewService(AppDbContext db) =>
        BundleTestKit.NewService(db, new FakeUser(), new FakeRegistry());

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static string Wire(WorkflowBundle b) => JsonSerializer.Serialize(b, WorkflowBundleReader.SerializerOptions);

    private static SnippetEntity Snippet(string name, string type, string targetMode = "once", string? retry = null) => new()
    {
        SnippetId = Guid.NewGuid(), Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'),
        Type = type, TargetMode = targetMode, TimeoutSeconds = 30, RetryPolicyJson = retry, IsActive = true,
    };

    private static WorkflowEntity Workflow(string name, string nodes, string edges = "[]", string? metadata = null)
    {
        var n = Json(nodes);
        var e = Json(edges);
        return new WorkflowEntity
        {
            WorkflowId = Guid.NewGuid(), Name = name, NodesJson = nodes, EdgesJson = edges,
            MetadataJson = metadata, SchemaHash = WorkflowCanonicalizer.ComputeSchemaHash(n, e),
            Environment = WorkflowEntity.EnvDraft, IsActive = true,
        };
    }

    private static string Raw(Guid snippetId, string overrides = "{}") => $$"""
        [{"id":"start","snippet_id":"__start__"},
         {"id":"step","snippet_id":"{{snippetId}}","config_overrides":{{overrides}}},
         {"id":"end","snippet_id":"__end__"}]
        """;

    private const string Chain = """[{"source":"start","target":"step","type":"always"},{"source":"step","target":"end","type":"success"}]""";

    private static JsonElement Node(JsonElement nodes, string id) =>
        nodes.EnumerateArray().Single(n => n.GetProperty("id").GetString() == id);

    private static JsonElement Overrides(JsonElement nodes, string id) => Node(nodes, id).GetProperty("config_overrides");

    // ─── §1 root shape ────────────────────────────────────────────────────────

    // The shape the other side's exporter must match: v3, the FW marker, exported_by,
    // requires, every dependencies section, triggers.
    [Fact]
    public async Task An_export_is_a_v3_bundle_with_every_section_present()
    {
        var db = NewDb();
        var ping = Snippet("TCP probe", "ping");
        db.Snippets.Add(ping);
        var wf = Workflow("sweep", Raw(ping.SnippetId), Chain);
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var bundle = await NewService(db).BuildAsync(wf.WorkflowId, default);
        using var doc = JsonDocument.Parse(Wire(bundle));
        var root = doc.RootElement;

        Assert.Equal("v3", root.GetProperty("schema_version").GetString());
        Assert.Equal("flow_weaver.workflow_bundle", root.GetProperty("kind").GetString());
        Assert.Equal("nashira", root.GetProperty("exported_by").GetProperty("product").GetString());
        var requires = root.GetProperty("requires");
        Assert.Equal(["ping"], requires.GetProperty("snippet_types").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Array, requires.GetProperty("capabilities").ValueKind);
        Assert.Equal(JsonValueKind.Array, requires.GetProperty("secrets").ValueKind);
        var deps = root.GetProperty("dependencies");
        foreach (var section in new[] { "snippets", "integrations", "mcp_servers", "credentials", "repositories", "workflows" })
            Assert.Equal(JsonValueKind.Array, deps.GetProperty(section).ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("triggers").ValueKind);
        // The retry policy goes out in the canonical shape even when absent.
        Assert.True(root.GetProperty("dependencies").GetProperty("snippets")[0].TryGetProperty("retry_policy", out _));
    }

    // Forward compatibility: a member this build has never heard of is ignored (§1).
    [Fact]
    public void Unknown_members_are_ignored_but_unknown_capabilities_are_refused()
    {
        var snippetId = Guid.NewGuid();
        string Bundle(string capabilities) => $$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3","future_member":{"x":1},
             "requires":{"snippet_types":["ping"],"capabilities":{{{capabilities}}},"secrets":[],"future":true},
             "workflow":{"name":"wf","future":1},
             "nodes":[{"id":"a","snippet_id":"{{{snippetId}}}"}],"edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippetId}}}","name":"p","type":"ping","target_mode":"once"}],"future":[]}}
            """;

        var ok = WorkflowBundleReader.Parse(Bundle("[\"conditional_edges\"]"));
        Assert.Equal("wf", ok.Workflow.Name);

        var ex = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(Bundle("[\"teleport\"]")));
        Assert.Equal("bundle_capability_unsupported", ex.Code);
        Assert.Contains("teleport", ex.Message);
    }

    // ─── §2.3 v2 read ─────────────────────────────────────────────────────────

    [Fact]
    public void A_v2_bundle_is_read_with_requires_inferred_and_no_triggers()
    {
        var snippetId = Guid.NewGuid();
        var raw = $$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v2",
             "workflow":{"name":"wf"},
             "nodes":[{"id":"start","snippet_id":"__start__"},
                      {"id":"a","snippet_id":"{{{snippetId}}}","config_overrides":{"host":"{{ run.id | upper }}"}}],
             "edges":[{"source":"start","target":"a","type":"conditional","condition":"true"}],
             "dependencies":{"snippets":[{"id":"{{{snippetId}}}","name":"p","type":"ping","target_mode":"once","max_parallel":4}]},
             "triggers":[{"name":"should be ignored on v2","type":"cron","cron_expression":"* * * * *"}]}
            """;

        var bundle = WorkflowBundleReader.Parse(raw);

        Assert.Equal(["ping"], bundle.Requires.SnippetTypes);
        Assert.Equal(
            ["conditional_edges", "max_parallel", "run_namespace", "template_filters"],
            bundle.Requires.Capabilities);
        Assert.Empty(bundle.Triggers);
    }

    // ─── §2.2 capabilities ────────────────────────────────────────────────────

    // A declared degradable capability imports with a note; nothing is refused.
    [Fact]
    public async Task A_declared_degradable_capability_imports_with_a_note()
    {
        var db = NewDb();
        var snippetId = Guid.NewGuid();
        var bundle = WorkflowBundleReader.Parse($$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["ping"],"capabilities":["per_pool"],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[{"id":"a","snippet_id":"{{{snippetId}}}"}],"edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippetId}}}","name":"p","type":"ping","target_mode":"once"}]}}
            """);

        var resolution = await NewService(db).ResolveAsync(bundle, default);
        Assert.Contains(resolution.Notes, n => n.Contains("per_pool", StringComparison.Ordinal));
    }

    // per_device_scope is the one capability that needs the graph, not just a string
    // scan: a per_device consumer reading a per_device producer's output by field.
    [Fact]
    public void Per_device_scope_is_detected_only_across_two_per_device_nodes()
    {
        var producer = Guid.NewGuid();
        var consumer = Guid.NewGuid();
        WorkflowBundle Build(string consumerMode, string template) => new()
        {
            Workflow = new BundleWorkflow { Name = "wf" },
            Nodes = Json($$$"""
                [{"id":"show","snippet_id":"{{{producer}}}"},
                 {"id":"parse","snippet_id":"{{{consumer}}}","config_overrides":{"input":"{{{template}}}"}}]
                """),
            Edges = Json("[]"),
            Dependencies = new BundleDependencies
            {
                Snippets =
                [
                    new BundleSnippet { Id = producer, Name = "show", Type = "ssh", TargetMode = "per_device" },
                    new BundleSnippet { Id = consumer, Name = "parse", Type = "transform", TargetMode = consumerMode },
                ],
            },
        };

        Assert.Contains("per_device_scope", BundleRequirements.Compute(Build("per_device", "{{ steps.show.output.stdout }}")).Capabilities);
        Assert.DoesNotContain("per_device_scope", BundleRequirements.Compute(Build("once", "{{ steps.show.output.stdout }}")).Capabilities);
        Assert.DoesNotContain("per_device_scope", BundleRequirements.Compute(Build("per_device", "{{ steps.show.output.devices }}")).Capabilities);
    }

    // ─── §2.4 secrets ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Secret_references_travel_with_their_users_and_missing_ones_are_noted_not_refused()
    {
        var db = NewDb();
        db.Secrets.Add(new SecretEntity { SecretId = Guid.NewGuid(), Name = "present", EncryptedValue = [1, 2, 3], IsActive = true });
        var ssh = Snippet("show version", "ssh");
        ssh.Code = "${secret:credential:netops:password}";
        db.Snippets.Add(ssh);
        var wf = Workflow("audit", Raw(ssh.SnippetId,
            """{"commands":["show version"],"enable_secret":"${secret:secret:present:value}","password":"${secret:secret:absent:value}"}"""), Chain);
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var bundle = await NewService(db).BuildAsync(wf.WorkflowId, default);

        var refs = bundle.Requires.Secrets.ToDictionary(s => s.Ref, s => s.UsedBy);
        Assert.Equal(["step"], refs["${secret:secret:present:value}"]);
        Assert.Equal(["step"], refs["${secret:secret:absent:value}"]);
        // From the snippet's code: used by the node that runs it.
        Assert.Equal(["step"], refs["${secret:credential:netops:password}"]);

        var resolution = await NewService(db).ResolveAsync(WorkflowBundleReader.Parse(Wire(bundle)), default);
        Assert.Contains(resolution.Notes, n => n.Contains("${secret:secret:absent:value}", StringComparison.Ordinal));
        Assert.Contains(resolution.Notes, n => n.Contains("${secret:credential:netops:password}", StringComparison.Ordinal));
        Assert.DoesNotContain(resolution.Notes, n => n.Contains("${secret:secret:present:value}", StringComparison.Ordinal));
    }

    // ─── §4 portable keys ─────────────────────────────────────────────────────

    // Export replaces the local id with the portable name (§4: the canonical key
    // travels, the legacy id key MUST NOT be emitted).
    //
    // This assertion is the inverse of what it was: §4 used to let an exporter keep
    // the id beside the name. It no longer may — node objects are hashed, and a GUID
    // that means something only on the instance that wrote it makes the fingerprint
    // instance-specific, so the §8 round trip could never compare two instances.
    [Fact]
    public async Task Export_replaces_local_credential_and_repository_ids_with_portable_names()
    {
        var db = NewDb();
        var cred = new CredentialEntity { CredentialId = Guid.NewGuid(), Name = "netops-ssh", Type = "ssh", AuthMethod = "password", Username = "ops", EncryptedPassword = [9, 9], IsActive = true };
        var repo = new RepositoryEntity { GitRepositoryId = Guid.NewGuid(), Name = "configs", Url = "https://git.example/configs.git", IsActive = true };
        db.Credentials.Add(cred);
        db.GitRepositories.Add(repo);
        var git = Snippet("read config", "git");
        db.Snippets.Add(git);
        var wf = Workflow("backup", Raw(git.SnippetId,
            $$"""{"operation":"read_file","repository_id":"{{repo.GitRepositoryId}}","credential_id":"{{cred.CredentialId}}"}"""), Chain);
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var bundle = await NewService(db).BuildAsync(wf.WorkflowId, default);

        var o = Overrides(bundle.Nodes, "step");
        Assert.Equal("configs", o.GetProperty("repository").GetString());
        Assert.Equal("netops-ssh", o.GetProperty("credential").GetString());
        Assert.False(o.TryGetProperty("repository_id", out _));
        Assert.False(o.TryGetProperty("credential_id", out _));
        var c = Assert.Single(bundle.Dependencies.Credentials);
        Assert.Equal(("netops-ssh", "ssh", "password", "ops"), (c.Name, c.Type, c.AuthMethod, c.Username));
        var r = Assert.Single(bundle.Dependencies.Repositories);
        Assert.Equal(("configs", "https://git.example/configs.git"), (r.Name, r.RemoteUrl));
    }

    // FlowWeaver's legacy keys, translated through the mapping the bundle carries, and
    // resolved to this instance's rows.
    [Fact]
    public async Task Legacy_id_keys_are_translated_when_the_bundle_carries_the_mapping()
    {
        var db = NewDb();
        var localIntegration = new IntegrationEntity { IntegrationId = Guid.NewGuid(), Name = "NetBox", Slug = "netbox", Type = "netbox", BaseUrl = "https://nb", IsActive = true, Enabled = true };
        db.Integrations.Add(localIntegration);
        db.IntegrationActions.Add(new nashira_backend.Data.Models.IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(), IntegrationId = localIntegration.IntegrationId,
            Name = "list_devices", OperationId = "list_devices", Method = "GET", Path = "/api/dcim/devices/", Enabled = true, IsActive = true,
        });
        db.McpServers.Add(new nashira_backend.Data.Models.McpServer { McpServerId = Guid.NewGuid(), Name = "docs", Transport = "http", IsActive = true });
        var localCred = new CredentialEntity { CredentialId = Guid.NewGuid(), Name = "netops-ssh", AuthMethod = "password", IsActive = true };
        db.Credentials.Add(localCred);
        var localRepo = new RepositoryEntity { GitRepositoryId = Guid.NewGuid(), Name = "configs", Url = "https://git.example/configs.git", IsActive = true };
        db.GitRepositories.Add(localRepo);
        await db.SaveChangesAsync();

        var (fIntegration, fAction, fServer, fCred, fRepo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (call, mcp, ssh, git) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bundle = WorkflowBundleReader.Parse($$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["integration_action","mcp_call","ssh","git"],"capabilities":[],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[
               {"id":"call","snippet_id":"{{{call}}}","config_overrides":{"integration_id":"{{{fIntegration}}}","action_id":"{{{fAction}}}"}},
               {"id":"ask","snippet_id":"{{{mcp}}}","config_overrides":{"mcp_server_id":"{{{fServer}}}","tool":"search"}},
               {"id":"show","snippet_id":"{{{ssh}}}","config_overrides":{"commands":["show ver"],"credential_id":"{{{fCred}}}"}},
               {"id":"read","snippet_id":"{{{git}}}","config_overrides":{"operation":"read_file","repository_id":"{{{fRepo}}}","path":"a.cfg"}}],
             "edges":[],
             "dependencies":{
               "snippets":[{"id":"{{{call}}}","name":"call","type":"integration_action","target_mode":"once"},
                           {"id":"{{{mcp}}}","name":"ask","type":"mcp_call","target_mode":"once"},
                           {"id":"{{{ssh}}}","name":"show","type":"ssh","target_mode":"once"},
                           {"id":"{{{git}}}","name":"read","type":"git","target_mode":"once"}],
               "integrations":[{"id":"{{{fIntegration}}}","slug":"netbox","name":"NetBox","type":"netbox",
                                "actions":[{"id":"{{{fAction}}}","name":"list_devices","method":"GET","path":"/api/dcim/devices/"}]}],
               "mcp_servers":[{"id":"{{{fServer}}}","name":"docs","transport":"http"}],
               "credentials":[{"id":"{{{fCred}}}","name":"netops-ssh","type":"ssh","auth_method":"password"}],
               "repositories":[{"id":"{{{fRepo}}}","name":"configs","remote_url":"https://git.example/configs.git"}]}}
            """);

        var imported = await NewService(db).ImportAsync(bundle, null, null, default);
        var nodes = Json(imported.Workflow.NodesJson);

        var callO = Overrides(nodes, "call");
        Assert.Equal("NetBox", callO.GetProperty("integration").GetString());
        Assert.Equal("list_devices", callO.GetProperty("action").GetString());
        Assert.False(callO.TryGetProperty("integration_id", out _));
        Assert.False(callO.TryGetProperty("action_id", out _));

        var askO = Overrides(nodes, "ask");
        Assert.Equal("docs", askO.GetProperty("server").GetString());
        Assert.False(askO.TryGetProperty("mcp_server_id", out _));

        var showO = Overrides(nodes, "show");
        Assert.Equal("netops-ssh", showO.GetProperty("credential").GetString());
        // The id stayed beside the name and now points at the local row.
        Assert.Equal(localCred.CredentialId.ToString(), showO.GetProperty("credential_id").GetString());

        var readO = Overrides(nodes, "read");
        Assert.Equal("configs", readO.GetProperty("repository").GetString());
        Assert.Equal(localRepo.GitRepositoryId.ToString(), readO.GetProperty("repository_id").GetString());

        // …and back out. What this instance stores is its business (local ids, the local
        // name for the integration); what it puts on the wire is the portable identity
        // and nothing else — §4. Exporting a workflow that was itself imported is the
        // direction that decides whether a bundle can cross twice.
        var again = await NewService(db).BuildAsync(imported.Workflow.WorkflowId, default);

        var outCall = Overrides(again.Nodes, "call");
        // The slug, not "NetBox", which is only what this catalogue calls it.
        Assert.Equal("netbox", outCall.GetProperty("integration").GetString());
        Assert.Equal("list_devices", outCall.GetProperty("action").GetString());
        Assert.Equal("docs", Overrides(again.Nodes, "ask").GetProperty("server").GetString());
        Assert.Equal("netops-ssh", Overrides(again.Nodes, "show").GetProperty("credential").GetString());
        Assert.Equal("configs", Overrides(again.Nodes, "read").GetProperty("repository").GetString());

        // No legacy id key survives the crossing, on any node.
        foreach (var key in new[] { "integration_id", "action_id", "mcp_server_id", "credential_id", "repository_id" })
            Assert.DoesNotContain($"\"{key}\"", Wire(again), StringComparison.Ordinal);
    }

    // §4 the other direction: the exporter writes the SLUG, the half of an integration's
    // identity two catalogues can agree on, and the importer maps it back to whatever
    // this instance calls it — this build's handler resolves an integration by name.
    [Fact]
    public async Task The_integration_key_travels_as_the_slug_and_comes_back_as_the_local_name()
    {
        var source = NewDb();
        source.Integrations.Add(new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(), Name = "NetBox Bogota", Slug = "netbox", Type = "netbox",
            BaseUrl = "https://nb", IsActive = true, Enabled = true,
        });
        var call = Snippet("call", "integration_action");
        source.Snippets.Add(call);
        var wf = Workflow("audit", Raw(call.SnippetId, """{"integration":"NetBox Bogota","action":"list_devices"}"""), Chain);
        source.Workflows.Add(wf);
        await source.SaveChangesAsync();

        var bundle = await NewService(source).BuildAsync(wf.WorkflowId, default);
        Assert.Equal("netbox", Overrides(bundle.Nodes, "step").GetProperty("integration").GetString());

        // A second instance that calls the same system something else.
        var target = NewDb();
        var localId = Guid.NewGuid();
        target.Integrations.Add(new IntegrationEntity
        {
            IntegrationId = localId, Name = "NetBox (prod)", Slug = "netbox", Type = "netbox",
            BaseUrl = "https://nb.prod", IsActive = true, Enabled = true,
        });
        target.IntegrationActions.Add(new nashira_backend.Data.Models.IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(), IntegrationId = localId, Name = "list_devices",
            OperationId = "list_devices", Method = "GET", Path = "/d/", Enabled = true, IsActive = true,
        });
        await target.SaveChangesAsync();

        var imported = await NewService(target).ImportAsync(WorkflowBundleReader.Parse(Wire(bundle)), null, null, default);
        Assert.Equal("NetBox (prod)",
            Overrides(Json(imported.Workflow.NodesJson), "step").GetProperty("integration").GetString());
        Assert.Contains(imported.Notes, n => n.Contains("by slug", StringComparison.Ordinal));

        // The direction that matters: exporting from the second instance puts the SLUG
        // back on the wire, not "NetBox (prod)". Otherwise two instances that named the
        // same system differently would hash the same workflow differently and the §8
        // round trip could never hold across products.
        var again = await NewService(target).BuildAsync(imported.Workflow.WorkflowId, default);
        Assert.Equal("netbox", Overrides(again.Nodes, "step").GetProperty("integration").GetString());
        // The node is otherwise identical to what the first instance exported; only
        // `snippet_id` differs, which §8 normalizes because it is always remapped.
        Assert.Equal("list_devices", Overrides(again.Nodes, "step").GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_legacy_key_without_a_mapping_is_refused_naming_the_node_and_the_key()
    {
        var db = NewDb();
        var call = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var bundle = WorkflowBundleReader.Parse($$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["integration_action"],"capabilities":[],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[{"id":"call","snippet_id":"{{{call}}}","config_overrides":{"integration_id":"{{{orphan}}}"}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{call}}}","name":"call","type":"integration_action","target_mode":"once"}]}}
            """);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => NewService(db).ResolveAsync(bundle, default));
        Assert.Equal("bundle_reference_untranslatable", ex.Code);
        Assert.Contains("'call'", ex.Message);
        Assert.Contains("integration_id", ex.Message);
    }

    // ─── §5.3 identity only ───────────────────────────────────────────────────

    [Fact]
    public async Task A_missing_credential_or_repository_is_refused_by_name_with_what_a_human_needs()
    {
        var db = NewDb();
        var git = Guid.NewGuid();
        var bundle = WorkflowBundleReader.Parse($$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["git"],"capabilities":[],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[{"id":"read","snippet_id":"{{{git}}}","config_overrides":{"operation":"read_file","repository":"configs","credential":"deploy-key"}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{git}}}","name":"read","type":"git","target_mode":"once"}],
                             "credentials":[{"id":"{{{Guid.NewGuid()}}}","name":"deploy-key","type":"git_token","auth_method":"token"}],
                             "repositories":[{"id":"{{{Guid.NewGuid()}}}","name":"configs","remote_url":"https://git.example/configs.git"}]}}
            """);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => NewService(db).ResolveAsync(bundle, default));
        Assert.Equal("bundle_dependencies_missing", ex.Code);
        Assert.Contains("credential 'deploy-key'", ex.Message);
        Assert.Contains("git repository 'configs'", ex.Message);
        Assert.Contains("https://git.example/configs.git", ex.Message);
    }

    // The property that makes a bundle safe to attach to an email. Every kind of
    // material the referenced rows hold is planted and none of it may appear.
    [Fact]
    public async Task An_exported_bundle_never_carries_secret_material()
    {
        var db = NewDb();
        var protector = new BundleTestKit.FakeProtector();
        db.Integrations.Add(new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(), Name = "NetBox", Slug = "netbox", Type = "netbox", BaseUrl = "https://nb",
            AuthConfig = """{"token":"INTEGRATION-TOKEN-LEAK"}""", HeadersJson = """{"X-Api-Key":"HEADER-LEAK"}""",
            IsActive = true, Enabled = true,
        });
        var cred = new CredentialEntity
        {
            CredentialId = Guid.NewGuid(), Name = "netops-ssh", AuthMethod = "password", Username = "ops",
            EncryptedPassword = protector.Encrypt("PASSWORD-LEAK"), EncryptedPrivateKey = protector.Encrypt("KEY-LEAK"), IsActive = true,
        };
        db.Credentials.Add(cred);
        var repoCred = new CredentialEntity { CredentialId = Guid.NewGuid(), Name = "deploy", AuthMethod = "token", EncryptedToken = protector.Encrypt("TOKEN-LEAK"), IsActive = true };
        db.Credentials.Add(repoCred);
        var repo = new RepositoryEntity { GitRepositoryId = Guid.NewGuid(), Name = "configs", Url = "https://git.example/c.git", AuthCredentialId = repoCred.CredentialId, LocalPath = "/srv/LOCAL-PATH-LEAK", IsActive = true };
        db.GitRepositories.Add(repo);
        db.Secrets.Add(new SecretEntity { SecretId = Guid.NewGuid(), Name = "enable", EncryptedValue = protector.Encrypt("ENABLE-LEAK")!, IsActive = true });

        var ssh = Snippet("show", "ssh");
        var call = Snippet("call", "integration_action");
        var git = Snippet("read", "git");
        db.Snippets.AddRange(ssh, call, git);
        var wf = Workflow("audit", $$$"""
            [{"id":"start","snippet_id":"__start__"},
             {"id":"show","snippet_id":"{{{ssh.SnippetId}}}","config_overrides":{"commands":["show run"],"credential_id":"{{{cred.CredentialId}}}","enable_secret":"${secret:secret:enable:value}"}},
             {"id":"call","snippet_id":"{{{call.SnippetId}}}","config_overrides":{"integration":"NetBox","action":"list_devices"}},
             {"id":"read","snippet_id":"{{{git.SnippetId}}}","config_overrides":{"operation":"read_file","repository_id":"{{{repo.GitRepositoryId}}}"}}]
            """);
        db.Workflows.Add(wf);
        db.WorkflowTriggers.Add(new TriggerEntity
        {
            WorkflowTriggerId = Guid.NewGuid(), WorkflowId = wf.WorkflowId, Name = "hook", Type = "webhook",
            Route = "abc123", EncryptedSecret = protector.Encrypt("HMAC-LEAK"), TargetDevicesJson = $"[\"{Guid.NewGuid()}\"]",
            Enabled = true, FireCount = 7, IsActive = true,
        });
        await db.SaveChangesAsync();

        var wire = Wire(await NewService(db).BuildAsync(wf.WorkflowId, default));

        foreach (var leak in new[] { "INTEGRATION-TOKEN-LEAK", "HEADER-LEAK", "PASSWORD-LEAK", "KEY-LEAK", "TOKEN-LEAK", "LOCAL-PATH-LEAK", "ENABLE-LEAK", "HMAC-LEAK" })
            Assert.DoesNotContain(leak, wire, StringComparison.Ordinal);
        // Reversed plaintext is what the fake protector stores; it must not leak either.
        Assert.DoesNotContain("KAEL", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target_devices", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("fire_count", wire, StringComparison.Ordinal);
        // References are the point: they travel as written.
        Assert.Contains("${secret:secret:enable:value}", wire, StringComparison.Ordinal);
        Assert.Contains("\"route\": \"abc123\"", wire, StringComparison.Ordinal);
    }

    // ─── §5.4 sub-workflows ───────────────────────────────────────────────────

    [Fact]
    public async Task Subflows_are_flattened_on_export_and_recreated_then_remapped_on_import()
    {
        var source = NewDb();
        var ping = Snippet("probe", "ping");
        var ssh = Snippet("show", "ssh");
        source.Snippets.AddRange(ping, ssh);
        var grandchild = Workflow("grandchild", Raw(ssh.SnippetId, """{"commands":["show ver"]}"""), Chain);
        var child = Workflow("child", $$$"""
            [{"id":"start","snippet_id":"__start__"},
             {"id":"probe","snippet_id":"{{{ping.SnippetId}}}"},
             {"id":"deeper","snippet_id":"subflow","type":"subflow","config_overrides":{"subflow_workflow_id":"{{{grandchild.WorkflowId}}}","site":"{{ input.site }}"}}]
            """, """[{"source":"start","target":"probe","type":"always"},{"source":"probe","target":"deeper","type":"success"}]""");
        var parent = Workflow("parent", $$$"""
            [{"id":"start","snippet_id":"__start__"},
             {"id":"run-child","snippet_id":"subflow","type":"subflow","config_overrides":{"subflow_workflow_id":"{{{child.WorkflowId}}}"}}]
            """, """[{"source":"start","target":"run-child","type":"always"}]""");
        source.Workflows.AddRange(grandchild, child, parent);
        await source.SaveChangesAsync();

        var bundle = await NewService(source).BuildAsync(parent.WorkflowId, default);

        Assert.Equal(2, bundle.Dependencies.Workflows.Count);
        Assert.Contains(bundle.Dependencies.Workflows, w => w.Id == child.WorkflowId);
        Assert.Contains(bundle.Dependencies.Workflows, w => w.Id == grandchild.WorkflowId);
        // Their snippets are merged into the same sections and requires.
        Assert.Equal(2, bundle.Dependencies.Snippets.Count);
        Assert.Equal(["ping", "ssh"], bundle.Requires.SnippetTypes);
        Assert.Contains("subflow", bundle.Requires.Capabilities);

        // …and the import succeeds, children first, with every reference remapped.
        //
        // This half of the test asserted a REFUSAL for as long as this build could not
        // execute a subflow node, and the refusal was right: the children were created,
        // every `subflow_workflow_id` was remapped, no note was raised — and then at run
        // time the node reported `skipped`, which a `success` edge does not follow, so
        // the whole downstream half of the graph silently never ran while the run
        // reported `completed`. `subflow` is non-degradable in bundle/SPEC.md §2.2
        // precisely so an importer in that position says so instead of importing clean.
        // Execution landed (execution/SPEC.md §5, SubflowRunner), so the capability is
        // claimed and the import is the contract again.
        var target = NewDb();
        var imported = await NewService(target).ImportAsync(
            WorkflowBundleReader.Parse(Wire(bundle)), null, null, default);

        Assert.Equal(2, imported.CreatedWorkflows.Count);
        Assert.Equal(2, imported.CreatedSnippets.Count);
        var newChild = imported.CreatedWorkflows.Single(w => w.Name == "child");
        var newGrandchild = imported.CreatedWorkflows.Single(w => w.Name == "grandchild");
        Assert.Equal(WorkflowEntity.EnvDraft, newChild.Environment);
        Assert.True(Json(newChild.MetadataJson!).GetProperty("is_subflow").GetBoolean());
        Assert.False(imported.Workflow.MetadataJson?.Contains("is_subflow") ?? false);

        // Every subflow_workflow_id now points at a row on this instance.
        Assert.Equal(newChild.WorkflowId.ToString(),
            Overrides(Json(imported.Workflow.NodesJson), "run-child").GetProperty("subflow_workflow_id").GetString());
        var deeper = Overrides(Json(newChild.NodesJson), "deeper");
        Assert.Equal(newGrandchild.WorkflowId.ToString(), deeper.GetProperty("subflow_workflow_id").GetString());
        // The child's other keys are its input, untouched.
        Assert.Equal("{{ input.site }}", deeper.GetProperty("site").GetString());
        // And they exist as rows.
        Assert.Equal(3, await target.Workflows.CountAsync(w => w.IsActive));
    }

    // §8's round trip: export → import → export is the identity, on the bundle's own
    // nodes+edges and on `requires`. If it is not, the two products cannot compare
    // notes on a workflow and the whole format is decorative.
    //
    // Same instance: the child exists under the same name with the same graph, so it is
    // reused rather than duplicated (§5.4) — and the parent's canonical hash comes back
    // identical.
    //
    // This ran through a subflow-free graph for as long as `subflow` refused at import,
    // because the round trip could not complete otherwise; the identity was pinned but
    // sub-workflow reuse went uncovered and was called out rather than silently dropped.
    // Execution landed, so the subflow is back and reuse is asserted again.
    [Fact]
    public async Task A_same_instance_round_trip_keeps_the_canonical_hash_and_reuses_the_sub_workflow()
    {
        var db = NewDb();
        var ping = Snippet("probe", "ping");
        db.Snippets.Add(ping);
        var cred = new CredentialEntity { CredentialId = Guid.NewGuid(), Name = "netops", AuthMethod = "password", IsActive = true };
        db.Credentials.Add(cred);
        // The node names its credential and nothing else: since §4 forbids the legacy id
        // on the wire, a local row that still carried one would be normalized by the
        // export and would no longer match itself on the way back in.
        var child = Workflow("child", Raw(ping.SnippetId, """{"host":"{{ device.ip }}","credential":"netops"}"""), Chain);
        var parent = Workflow("parent", $$$"""
            [{"id":"start","snippet_id":"__start__"},
             {"id":"run-child","snippet_id":"subflow","type":"subflow","config_overrides":{"subflow_workflow_id":"{{{child.WorkflowId}}}"}}]
            """, """[{"source":"start","target":"run-child","type":"always"}]""");
        db.Workflows.AddRange(child, parent);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var wire = Wire(await service.BuildAsync(parent.WorkflowId, default));
        var imported = await service.ImportAsync(WorkflowBundleReader.Parse(wire), "parent (copy)", null, default);

        Assert.Equal(parent.SchemaHash, imported.Workflow.SchemaHash);
        // Reused, not recreated: same name AND identical canonical hash of nodes+edges.
        Assert.Empty(imported.CreatedWorkflows);
        Assert.Empty(imported.CreatedSnippets);
        Assert.Contains(imported.Notes, n => n.Contains("reused", StringComparison.Ordinal));
        // And the copy points at the existing row, not at a second one.
        Assert.Equal(child.WorkflowId.ToString(),
            Overrides(Json(imported.Workflow.NodesJson), "run-child").GetProperty("subflow_workflow_id").GetString());
        Assert.Equal(WorkflowEntity.EnvDraft, imported.Workflow.Environment);
        Assert.Equal(1, imported.Workflow.Version);

        // Export the copy: the requires block AND the canonical hash of the bundle's own
        // nodes+edges are unchanged (§8 round-trip vector). Export → import → export has
        // to be the identity, or the two products cannot compare notes on a workflow.
        var again = await service.BuildAsync(imported.Workflow.WorkflowId, default);
        var first = WorkflowBundleReader.Parse(wire);
        Assert.Equal(JsonSerializer.Serialize(first.Requires), JsonSerializer.Serialize(again.Requires));
        Assert.Equal(
            WorkflowCanonicalizer.ComputeSchemaHash(first.Nodes, first.Edges),
            WorkflowCanonicalizer.ComputeSchemaHash(again.Nodes, again.Edges));
        var firstSub = Assert.Single(first.Dependencies.Workflows);
        var againSub = Assert.Single(again.Dependencies.Workflows);
        Assert.Equal(
            WorkflowCanonicalizer.ComputeSchemaHash(firstSub.Nodes, firstSub.Edges),
            WorkflowCanonicalizer.ComputeSchemaHash(againSub.Nodes, againSub.Edges));
    }

    [Fact]
    public async Task A_subflow_cycle_is_refused_on_export_and_on_import()
    {
        var db = NewDb();
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        string Nodes(Guid other) => $$$"""
            [{"id":"start","snippet_id":"__start__"},
             {"id":"go","snippet_id":"subflow","type":"subflow","config_overrides":{"subflow_workflow_id":"{{{other}}}"}}]
            """;
        const string edges = """[{"source":"start","target":"go","type":"always"}]""";
        db.Workflows.Add(new WorkflowEntity { WorkflowId = aId, Name = "A", NodesJson = Nodes(bId), EdgesJson = edges, IsActive = true });
        db.Workflows.Add(new WorkflowEntity { WorkflowId = bId, Name = "B", NodesJson = Nodes(aId), EdgesJson = edges, IsActive = true });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => NewService(db).BuildAsync(aId, default));
        Assert.Equal("bundle_subflow_cycle", ex.Code);
        Assert.Contains("'A'", ex.Message);
        Assert.Contains("'B'", ex.Message);

        var raw = $$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":[],"capabilities":["subflow"],"secrets":[]},
             "workflow":{"name":"A"},"nodes":{{{Nodes(bId)}}},"edges":{{{edges}}},
             "dependencies":{"snippets":[],"workflows":[
               {"id":"{{{bId}}}","name":"B","nodes":{{{Nodes(aId)}}},"edges":{{{edges}}}},
               {"id":"{{{aId}}}","name":"A","nodes":{{{Nodes(bId)}}},"edges":{{{edges}}}}]}}
            """;
        var parseEx = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(raw));
        Assert.Equal("bundle_subflow_cycle", parseEx.Code);
    }

    [Fact]
    public void A_subflow_whose_workflow_is_not_carried_is_incomplete()
    {
        var missing = Guid.NewGuid();
        var raw = $$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":[],"capabilities":["subflow"],"secrets":[]},
             "workflow":{"name":"A"},
             "nodes":[{"id":"go","snippet_id":"subflow","type":"subflow","config_overrides":{"subflow_workflow_id":"{{{missing}}}"}}],
             "edges":[],"dependencies":{"snippets":[],"workflows":[]}}
            """;
        var ex = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(raw));
        Assert.Equal("bundle_incomplete", ex.Code);
        Assert.Contains(missing.ToString(), ex.Message);
    }

    // ─── §6 triggers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Triggers_travel_without_secrets_and_are_created_disabled_with_a_fresh_secret()
    {
        var source = NewDb();
        var protector = new BundleTestKit.FakeProtector();
        var ping = Snippet("probe", "ping");
        source.Snippets.Add(ping);
        var wf = Workflow("sweep", Raw(ping.SnippetId), Chain);
        source.Workflows.Add(wf);
        source.WorkflowTriggers.Add(new TriggerEntity
        {
            WorkflowTriggerId = Guid.NewGuid(), WorkflowId = wf.WorkflowId, Name = "nightly", Type = "cron",
            CronExpression = "0 3 * * *", Timezone = "America/Bogota", TargetDevicesJson = $"[\"{Guid.NewGuid()}\"]",
            InputDefaultsJson = """{"site":"bog"}""", Enabled = true, IsActive = true,
        });
        var sourceSecret = protector.Encrypt("source-hmac")!;
        source.WorkflowTriggers.Add(new TriggerEntity
        {
            WorkflowTriggerId = Guid.NewGuid(), WorkflowId = wf.WorkflowId, Name = "on-push", Type = "webhook",
            Route = "deadbeef", EncryptedSecret = sourceSecret, AllowTargetOverride = true, Enabled = true, IsActive = true,
        });
        await source.SaveChangesAsync();

        var bundle = await NewService(source).BuildAsync(wf.WorkflowId, default);

        Assert.Equal(2, bundle.Triggers.Count);
        Assert.Contains("triggers", bundle.Requires.Capabilities);
        var hook = bundle.Triggers.Single(t => t.Type == "webhook");
        Assert.Equal("deadbeef", hook.Route);
        Assert.True(hook.AllowTargetOverride);

        var target = NewDb();
        var imported = await NewService(target).ImportAsync(WorkflowBundleReader.Parse(Wire(bundle)), null, null, default);

        Assert.Equal(2, imported.CreatedTriggers.Count);
        var cron = imported.CreatedTriggers.Single(t => t.Type == "cron");
        Assert.False(cron.Enabled);
        Assert.Equal("[]", cron.TargetDevicesJson);
        Assert.Equal("America/Bogota", cron.Timezone);
        Assert.Contains("\"site\"", cron.InputDefaultsJson);

        var webhook = imported.CreatedTriggers.Single(t => t.Type == "webhook");
        Assert.False(webhook.Enabled);
        Assert.Equal("deadbeef", webhook.Route);
        Assert.NotNull(webhook.EncryptedSecret);
        Assert.NotEqual(sourceSecret, webhook.EncryptedSecret);
        Assert.True(webhook.AllowTargetOverride);
        Assert.Contains(imported.Notes, n => n.Contains("'nightly'", StringComparison.Ordinal) && n.Contains("DISABLED", StringComparison.Ordinal));
        Assert.Contains(imported.Notes, n => n.Contains("'on-push'", StringComparison.Ordinal) && n.Contains("fresh secret", StringComparison.Ordinal));

        // A second import finds the route taken and regenerates it, saying so.
        var second = await NewService(target).ImportAsync(WorkflowBundleReader.Parse(Wire(bundle)), "sweep (2)", null, default);
        var secondHook = second.CreatedTriggers.Single(t => t.Type == "webhook");
        Assert.NotEqual("deadbeef", secondHook.Route);
        Assert.Contains(second.Notes, n => n.Contains("already in use", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_trigger_type_this_build_lacks_is_skipped_with_a_note()
    {
        var db = NewDb();
        var ping = Guid.NewGuid();
        var bundle = WorkflowBundleReader.Parse($$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["ping"],"capabilities":["triggers"],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[{"id":"a","snippet_id":"{{{ping}}}"}],"edges":[],
             "dependencies":{"snippets":[{"id":"{{{ping}}}","name":"p","type":"ping","target_mode":"once"}]},
             "triggers":[{"name":"on-event","type":"event","enabled":false}]}
            """);

        var imported = await NewService(db).ImportAsync(bundle, null, null, default);
        Assert.Empty(imported.CreatedTriggers);
        Assert.Contains(imported.Notes, n => n.Contains("'on-event'", StringComparison.Ordinal) && n.Contains("skipped", StringComparison.Ordinal));
    }

    // A field the oracle's triggers carry and this build's rows have no column for is
    // dropped — but never silently. §7's "silence means nothing was degraded" is the
    // whole value of the notes: a trigger that was meant to notify someone on failure
    // would otherwise just stop doing it.
    [Fact]
    public async Task Trigger_fields_this_build_has_no_column_for_are_dropped_with_a_note()
    {
        var db = NewDb();
        var ping = Guid.NewGuid();
        var bundle = WorkflowBundleReader.Parse($$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["ping"],"capabilities":["triggers"],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[{"id":"a","snippet_id":"{{{ping}}}"}],"edges":[],
             "dependencies":{"snippets":[{"id":"{{{ping}}}","name":"p","type":"ping","target_mode":"once"}]},
             "triggers":[{"name":"nightly","type":"cron","cron_expression":"0 3 * * *","timezone":"UTC",
                          "notify_on":["failed"],"notification_webhook_url":"https://hooks.example/x",
                          "input_schema":{"type":"object"},"enabled":true}]}
            """);

        var imported = await NewService(db).ImportAsync(bundle, null, null, default);

        var trigger = Assert.Single(imported.CreatedTriggers);
        Assert.False(trigger.Enabled);
        var note = Assert.Single(imported.Notes, n => n.Contains("no column for", StringComparison.Ordinal)
                                                     || n.Contains("do not have", StringComparison.Ordinal));
        Assert.Contains("notify_on", note);
        Assert.Contains("notification_webhook_url", note);
        Assert.Contains("input_schema", note);
    }

    // ─── execution §3 retry policy ────────────────────────────────────────────

    [Fact]
    public async Task Retry_policy_is_exported_canonical_and_stored_as_received()
    {
        var db = NewDb();
        var ping = Snippet("probe", "ping", retry: """{"max_attempts":3,"delay_seconds":5,"backoff":"exponential"}""");
        db.Snippets.Add(ping);
        var wf = Workflow("sweep", Raw(ping.SnippetId), Chain);
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var bundle = await NewService(db).BuildAsync(wf.WorkflowId, default);
        var policy = Assert.Single(bundle.Dependencies.Snippets).RetryPolicy!.Value;
        Assert.Equal(2, policy.GetProperty("max_retries").GetInt32());
        Assert.Equal(5, policy.GetProperty("initial_delay_seconds").GetDouble());
        Assert.Equal("exponential", policy.GetProperty("backoff").GetString());
        Assert.Equal(30, policy.GetProperty("max_delay_seconds").GetInt32());

        var target = NewDb();
        var imported = await NewService(target).ImportAsync(WorkflowBundleReader.Parse(Wire(bundle)), null, null, default);
        var stored = Json(Assert.Single(imported.CreatedSnippets).RetryPolicyJson!);
        Assert.Equal(2, stored.GetProperty("max_retries").GetInt32());
        Assert.False(stored.TryGetProperty("max_attempts", out _));
    }

    // ─── snippets/SPEC.md rule 3 and the ssh rule ────────────────────────────

    [Fact]
    public async Task Keys_no_alias_covers_are_noted_per_node()
    {
        var db = NewDb();
        var ssh = Guid.NewGuid();
        var bundle = WorkflowBundleReader.Parse($$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["ssh"],"capabilities":[],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[{"id":"show","snippet_id":"{{{ssh}}}","config_overrides":{"comands":["show ver"],"structured":true,"idempotency":"idempotent"}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{ssh}}}","name":"show","type":"ssh","target_mode":"per_device"}]}}
            """);

        var resolution = await NewService(db).ResolveAsync(bundle, default);

        var note = Assert.Single(resolution.Notes, n => n.Contains("does not define", StringComparison.Ordinal));
        Assert.Contains("'show'", note);
        Assert.Contains("comands", note);
        // `structured` is a documented alias, not an unknown key.
        Assert.DoesNotContain("structured", note);
        Assert.Contains(resolution.Notes, n => n.Contains("overrides idempotency", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plain_ssh_credentials_are_refused_but_secret_references_pass()
    {
        var db = NewDb();
        var ssh = Guid.NewGuid();
        string Bundle(string password) => $$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["ssh"],"capabilities":[],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[{"id":"show","snippet_id":"{{{ssh}}}","config_overrides":{"commands":["show ver"],"username":"${secret:credential:ops:username}","password":"{{{password}}}"}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{ssh}}}","name":"show","type":"ssh","target_mode":"per_device"}]}}
            """;

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => NewService(db).ResolveAsync(WorkflowBundleReader.Parse(Bundle("hunter2")), default));
        Assert.Equal("bundle_reference_untranslatable", ex.Code);
        Assert.Contains("'show'", ex.Message);
        Assert.Contains("'password'", ex.Message);

        var ok = await NewService(db).ResolveAsync(
            WorkflowBundleReader.Parse(Bundle("${secret:credential:ops:password}")), default);
        Assert.Equal("${secret:credential:ops:password}",
            Overrides(ok.RootNodes, "show").GetProperty("password").GetString());
    }

    // ─── §7 one refusal naming everything ────────────────────────────────────

    [Fact]
    public async Task Every_refusal_reason_is_reported_at_once()
    {
        var db = NewDb();
        var call = Guid.NewGuid();
        var ansible = Guid.NewGuid();
        var bundle = WorkflowBundleReader.Parse($$$"""
            {"kind":"flow_weaver.workflow_bundle","schema_version":"v3",
             "requires":{"snippet_types":["integration_action","ansible_playbook"],"capabilities":[],"secrets":[]},
             "workflow":{"name":"wf"},
             "nodes":[{"id":"call","snippet_id":"{{{call}}}","config_overrides":{"integration_id":"{{{Guid.NewGuid()}}}"}},
                      {"id":"play","snippet_id":"{{{ansible}}}"}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{call}}}","name":"call","type":"integration_action","target_mode":"once"},
                                         {"id":"{{{ansible}}}","name":"play","type":"ansible_playbook","target_mode":"once"}],
                             "mcp_servers":[{"name":"absent-server"}]}}
            """);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => NewService(db).ResolveAsync(bundle, default));

        Assert.Contains("[bundle_reference_untranslatable]", ex.Message);
        Assert.Contains("[bundle_dependencies_missing]", ex.Message);
        Assert.Contains("integration_id", ex.Message);
        Assert.Contains("ansible_playbook", ex.Message);
        Assert.Contains("absent-server", ex.Message);
        // Nothing was created.
        Assert.Equal(0, await db.Snippets.CountAsync());
    }

    // ─── fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "tester";
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => true;
    }

    private sealed class FakeRegistry : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes =>
            ["ping", "transform", "rest_call", "integration_action", "ssh", "mcp_call", "python_snippet", "git"];

        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => null;
    }
}
