using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.RestExecutor;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Notifications;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using nashira_backend.Services.Ssh;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Workflow;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Tests;

// workflow.v1 snippets/SPEC.md, per type: the canonical key AND every alias the
// contract obliges a handler to accept.
//
// This is the file that decides whether a bundle exported by Flow Weaver runs here.
// The graph, the schema and the hash can all agree and the run still dies on the
// first node because one product writes `command` and the other reads `commands`,
// or because `mapping` means nothing to a handler that only knows `expression`.
// Rule 1 of the contract — FW is the oracle for payload keys, the Nashira key stays
// an accepted alias — is only true if it is tested.
//
// Everything is mocked at the same boundary the existing handler tests mock: the
// db is in-memory, the SSH runner / MCP service / REST executor / messaging
// dispatcher are fakes, and HTTP goes through a stub message handler. Nothing here
// opens a socket except the `ping` probe, which is aimed at a closed local port.
public class SnippetAliasTests
{
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"alias-{Guid.NewGuid()}")
        .Options);

    private static SnippetRequest Request(string json, string? code = null, string? environment = "draft") => new()
    {
        NodeId = "n1",
        WorkflowId = Guid.Empty,
        SnippetId = Guid.NewGuid(),
        SnippetType = "test",
        Input = JsonDocument.Parse(json).RootElement.Clone(),
        Code = code,
        Environment = environment,
    };

    // ── shared fakes ────────────────────────────────────────────────────

    // Substitutes the references it was told about and leaves the rest literal,
    // exactly like the real resolver — the handlers key off that literal marker to
    // tell "resolved" from "does not exist".
    private sealed class FakeSecrets : ISecretResolver
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);

        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string> SubstituteAsync(string template, CancellationToken ct) =>
            SubstituteAsync(template, false, ct);

        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct)
        {
            var result = template;
            foreach (var (k, v) in Values) result = result.Replace(k, v, StringComparison.Ordinal);
            return Task.FromResult(result);
        }
    }

    // Records the URL the handler built. Blocking on demand is how these tests read
    // the composed URL without letting anything reach the network.
    private sealed class RecordingUrlGuard : IUrlGuard
    {
        public string? Last;
        public bool Block;

        public void EnsureSafe(string url, bool allowPrivate = false)
        {
            Last = url;
            if (Block) throw new InvalidOperationException($"blocked: {url}");
        }
    }

    private sealed class StubHttp : HttpMessageHandler
    {
        public HttpMethod? Method;
        public Uri? Uri;
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public string? Body;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Response = """{"ok":true}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            foreach (var h in request.Headers) Headers[h.Key] = string.Join(", ", h.Value);
            if (request.Content is not null)
            {
                foreach (var h in request.Content.Headers) Headers[h.Key] = string.Join(", ", h.Value);
                Body = await request.Content.ReadAsStringAsync(ct);
            }
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubHttpFactory(HttpMessageHandler inner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(inner, disposeHandler: false);
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) =>
            plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);
        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null ? null : Encoding.UTF8.GetString(ciphertext);
    }

    // ── transform ───────────────────────────────────────────────────────

    [Fact]
    public async Task Transform_reads_the_canonical_jmespath_expression()
    {
        var handler = new TransformSnippetHandler();

        var result = await handler.ExecuteAsync(Request("""
            {"expression":"devices[?status=='up'].name","input":{"devices":[
              {"name":"r1","status":"up"},{"name":"r2","status":"down"},{"name":"r3","status":"up"}]}}
            """), default);

        Assert.True(result.Success);
        Assert.Equal(["r1", "r3"], result.Output.EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // `mapping` is Nashira's older spelling of a multiselect hash; a handler that
    // only knew `expression` would silently pass the whole document through.
    [Fact]
    public async Task Transform_accepts_the_mapping_alias_as_a_multiselect_hash()
    {
        var handler = new TransformSnippetHandler();

        var result = await handler.ExecuteAsync(Request("""
            {"mapping":{"hostname":"device.name","addr":"device.ip"},
             "input":{"device":{"name":"core-sw","ip":"10.0.0.9"}}}
            """), default);

        Assert.True(result.Success);
        Assert.Equal("core-sw", result.Output.GetProperty("hostname").GetString());
        Assert.Equal("10.0.0.9", result.Output.GetProperty("addr").GetString());
    }

    // §`transform`: "`language` must be `jmespath` when present". Anything else is a
    // workflow written for an engine this is not, and running it as JMESPath anyway
    // would produce a plausible wrong answer.
    [Fact]
    public async Task Transform_refuses_a_language_that_is_not_jmespath()
    {
        var handler = new TransformSnippetHandler();

        var ok = await handler.ExecuteAsync(
            Request("""{"language":"jmespath","expression":"a","input":{"a":1}}"""), default);
        var bad = await handler.ExecuteAsync(
            Request("""{"language":"jsonata","expression":"a","input":{"a":1}}"""), default);

        Assert.True(ok.Success);
        Assert.Equal(1, ok.Output.GetInt32());
        Assert.False(bad.Success);
        Assert.Equal("bad_input", bad.ErrorCode);
    }

    // ── rest_call ───────────────────────────────────────────────────────

    private sealed class FakeRestExecutor : IRestOperationExecutor
    {
        public string? OperationId;
        public JsonElement PathParams;
        public JsonElement QueryParams;
        public JsonElement Body;
        public RestExecutionResult Next = new()
        {
            StatusCode = 200,
            Body = JsonDocument.Parse("""{"id":7}""").RootElement.Clone(),
            Headers = new Dictionary<string, string> { ["X-Trace"] = "abc" },
        };

        public Task<RestExecutionResult> ExecuteAsync(
            string operationId, JsonElement pathParams, JsonElement queryParams, JsonElement body,
        CancellationToken ct, string? source = null)
        {
            OperationId = operationId;
            PathParams = pathParams;
            QueryParams = queryParams;
            Body = body;
            return Task.FromResult(Next);
        }
    }

    private static (RestCallSnippetHandler Handler, FakeRestExecutor Executor, RecordingUrlGuard Guard,
        FakeSecrets Secrets, StubHttp Http) NewRest()
    {
        var rest = new FakeRestExecutor();
        var guard = new RecordingUrlGuard();
        var secrets = new FakeSecrets();
        var http = new StubHttp();
        return (new RestCallSnippetHandler(rest, guard, secrets, new StubHttpFactory(http)), rest, guard, secrets, http);
    }

    // The FW form: nothing catalogued, everything on the payload.
    [Fact]
    public async Task Rest_call_raw_form_sends_url_method_headers_body_and_query()
    {
        var (handler, rest, guard, secrets, http) = NewRest();
        secrets.Values["${secret:integration:netbox:token}"] = "t0ken";

        var result = await handler.ExecuteAsync(Request("""
            {"url":"https://api.example.test/v1/devices","method":"post",
             "headers":{"Authorization":"Bearer ${secret:integration:netbox:token}","Content-Type":"application/json"},
             "query":{"site":"hq","limit":50},
             "body":{"name":"r1"}}
            """), default);

        Assert.True(result.Success);
        // The catalogued path must not have been taken.
        Assert.Null(rest.OperationId);
        Assert.Equal(HttpMethod.Post, http.Method);
        Assert.Equal("/v1/devices", http.Uri!.AbsolutePath);
        Assert.Contains("site=hq", http.Uri.Query);
        Assert.Contains("limit=50", http.Uri.Query);
        // §rest_call: "${secret:…} references inside headers and url are resolved at
        // run time" — and nowhere earlier, so the stored input keeps the reference.
        Assert.Equal("Bearer t0ken", http.Headers["Authorization"]);
        Assert.Equal("""{"name":"r1"}""", http.Body);
        Assert.StartsWith("application/json", http.Headers["Content-Type"]);

        // Portable output: status_code, body, headers.
        Assert.Equal(200, result.Output.GetProperty("status_code").GetInt32());
        Assert.True(result.Output.GetProperty("body").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, result.Output.GetProperty("headers").ValueKind);
        // The guard saw the composed URL before anything left the process.
        Assert.StartsWith("https://api.example.test/v1/devices?", guard.Last);
    }

    [Fact]
    public async Task Rest_call_resolves_a_secret_reference_inside_the_url_itself()
    {
        var (handler, _, _, secrets, http) = NewRest();
        secrets.Values["${secret:integration:netbox:host}"] = "netbox.example.test";

        var result = await handler.ExecuteAsync(Request(
            """{"url":"https://${secret:integration:netbox:host}/api/status"}"""), default);

        Assert.True(result.Success);
        Assert.Equal("netbox.example.test", http.Uri!.Host);
    }

    // "The raw form is subject to the same outbound URL guard the product applies to
    // integrations" — a portable workflow must not be a way around the SSRF policy.
    [Fact]
    public async Task Rest_call_raw_form_goes_through_the_outbound_url_guard()
    {
        var (handler, _, guard, _, http) = NewRest();
        guard.Block = true;

        var blocked = await handler.ExecuteAsync(
            Request("""{"url":"http://169.254.169.254/latest/meta-data"}"""), default);
        var notAbsolute = await handler.ExecuteAsync(Request("""{"url":"/relative/path"}"""), default);
        var notHttp = await handler.ExecuteAsync(Request("""{"url":"file:///etc/passwd"}"""), default);

        Assert.False(blocked.Success);
        Assert.Equal("blocked", blocked.ErrorCode);
        Assert.False(notAbsolute.Success);
        Assert.Equal("bad_input", notAbsolute.ErrorCode);
        Assert.False(notHttp.Success);
        Assert.Equal("bad_input", notHttp.ErrorCode);
        // Nothing reached the wire on any of the three.
        Assert.Null(http.Uri);
    }

    // The Nashira form: the operation is catalogued, so the executor owns the URL.
    [Fact]
    public async Task Rest_call_catalogued_form_dispatches_to_the_operation_executor()
    {
        var (handler, rest, _, _, http) = NewRest();

        var result = await handler.ExecuteAsync(Request("""
            {"source":"netbox","operation_id":"dcim_devices_retrieve",
             "path_params":{"id":"42"},"query_params":{"brief":"true"},"body":{"note":"x"}}
            """), default);

        Assert.True(result.Success);
        Assert.Equal("dcim_devices_retrieve", rest.OperationId);
        Assert.Equal("42", rest.PathParams.GetProperty("id").GetString());
        Assert.Equal("true", rest.QueryParams.GetProperty("brief").GetString());
        Assert.Equal("x", rest.Body.GetProperty("note").GetString());
        // The catalogued form never opens a raw request of its own.
        Assert.Null(http.Uri);
        Assert.Equal(200, result.Output.GetProperty("status_code").GetInt32());
        Assert.Equal(7, result.Output.GetProperty("body").GetProperty("id").GetInt32());
        Assert.Equal("abc", result.Output.GetProperty("headers").GetProperty("X-Trace").GetString());
    }

    [Fact]
    public async Task Rest_call_with_neither_url_nor_operation_id_is_bad_input()
    {
        var (handler, _, _, _, _) = NewRest();

        var result = await handler.ExecuteAsync(Request("""{"method":"GET"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("bad_input", result.ErrorCode);
    }

    // ── integration_action ──────────────────────────────────────────────

    private sealed class NoAuth : IIntegrationAuthApplier
    {
        public Task ApplyAsync(HttpRequestMessage request, IntegrationEntity integration, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static async Task<(IntegrationActionSnippetHandler Handler, RecordingUrlGuard Guard, AppDbContext Db)>
        NewIntegrationAction(string name = "netbox", string slug = "netbox")
    {
        var db = Db();
        var integration = new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            Type = "rest",
            BaseUrl = "https://netbox.example.test",
            Enabled = true,
            IsActive = true,
        };
        db.Integrations.Add(integration);
        db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = integration.IntegrationId,
            Name = "get_device",
            OperationId = "dcim_devices_retrieve",
            Method = "GET",
            Path = "/api/dcim/devices/{id}/",
            Enabled = true,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        // Blocking in the guard stops the call one line after the URL is composed,
        // which is exactly the line these tests are about.
        var guard = new RecordingUrlGuard { Block = true };
        return (new IntegrationActionSnippetHandler(db, new NoAuth(), new FakeSecrets(), guard,
            new StubHttpFactory(new StubHttp()), NullLogger<IntegrationActionSnippetHandler>.Instance), guard, db);
    }

    // FW writes `params`/`query`; Nashira wrote `path_params`/`query_params`. Both
    // have to build the same URL.
    [Fact]
    public async Task Integration_action_accepts_params_and_query_as_well_as_the_nashira_spellings()
    {
        var (handler, guard, db) = await NewIntegrationAction();

        var canonical = await handler.ExecuteAsync(Request("""
            {"integration":"netbox","action":"get_device","params":{"id":"42"},"query":{"brief":"true"}}
            """), default);
        var canonicalUrl = guard.Last;

        var alias = await handler.ExecuteAsync(Request("""
            {"integration":"netbox","action":"get_device","path_params":{"id":"42"},"query_params":{"brief":"true"}}
            """), default);

        Assert.Equal("blocked", canonical.ErrorCode);
        Assert.Equal("blocked", alias.ErrorCode);
        Assert.Equal("https://netbox.example.test/api/dcim/devices/42/?brief=true", canonicalUrl);
        Assert.Equal(canonicalUrl, guard.Last);
        db.Dispose();
    }

    // A path placeholder with nothing to fill it is an authoring error, not a URL
    // with a literal `{id}` in it.
    [Fact]
    public async Task Integration_action_without_the_path_parameter_fails_before_the_wire()
    {
        var (handler, guard, db) = await NewIntegrationAction();

        var result = await handler.ExecuteAsync(
            Request("""{"integration":"netbox","action":"get_device"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("bad_input", result.ErrorCode);
        Assert.Contains("id", result.Error);
        Assert.Null(guard.Last);
        db.Dispose();
    }

    // §integration_action: `integration` is the integration SLUG, "name accepted on
    // resolution". A bundle carries the slug because it is the stable identifier; a
    // display name is free text an admin renames. Both have to land on the row.
    [Fact]
    public async Task Integration_action_resolves_the_integration_by_slug_and_by_name()
    {
        var (handler, guard, db) = await NewIntegrationAction(name: "NetBox Production", slug: "netbox");

        var bySlug = await handler.ExecuteAsync(Request("""
            {"integration":"netbox","action":"get_device","params":{"id":"42"}}
            """), default);
        Assert.Equal("blocked", bySlug.ErrorCode);
        Assert.Equal("https://netbox.example.test/api/dcim/devices/42/", guard.Last);

        guard.Last = null;
        var byName = await handler.ExecuteAsync(Request("""
            {"integration":"NetBox Production","action":"get_device","params":{"id":"42"}}
            """), default);
        Assert.Equal("blocked", byName.ErrorCode);
        Assert.Equal("https://netbox.example.test/api/dcim/devices/42/", guard.Last);

        // Neither is case-significant to an author typing the key by hand.
        guard.Last = null;
        var casing = await handler.ExecuteAsync(Request("""
            {"integration":"NETBOX","action":"get_device","params":{"id":"42"}}
            """), default);
        Assert.Equal("blocked", casing.ErrorCode);

        guard.Last = null;
        var unknown = await handler.ExecuteAsync(Request("""
            {"integration":"ghost","action":"get_device","params":{"id":"42"}}
            """), default);
        Assert.Equal("not_found", unknown.ErrorCode);
        Assert.Null(guard.Last);
        db.Dispose();
    }

    // The slug wins when a display name collides with another integration's slug:
    // a rename must not be able to redirect a workflow at a different system.
    [Fact]
    public async Task Integration_action_prefers_the_slug_over_a_colliding_name()
    {
        var (handler, guard, db) = await NewIntegrationAction(name: "netbox", slug: "netbox");
        db.Integrations.Add(new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(),
            Name = "netbox",
            Slug = "netbox-staging",
            Type = "rest",
            BaseUrl = "https://staging.example.test",
            Enabled = true,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await handler.ExecuteAsync(Request("""
            {"integration":"netbox","action":"get_device","params":{"id":"42"}}
            """), default);

        Assert.Equal("blocked", result.ErrorCode);
        Assert.StartsWith("https://netbox.example.test/", guard.Last);
        db.Dispose();
    }

    // ── mcp_call ────────────────────────────────────────────────────────

    private sealed class FakeMcp : IMcpServerService
    {
        public string? Tool;
        public JsonElement Arguments;
        public McpCallResult Next = new("done", null, false);

        public Task<McpCallResult> CallAsync(Guid serverId, string toolName, JsonElement arguments, CancellationToken ct)
        {
            Tool = toolName;
            Arguments = arguments;
            return Task.FromResult(Next);
        }

        public Task<McpSyncResult> SyncToolsAsync(Guid serverId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IntegrationHealthLike> CheckAsync(Guid serverId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Mcp_call_accepts_tool_name_as_the_alias_of_tool()
    {
        using var db = Db();
        db.McpServers.Add(new McpServer
        {
            McpServerId = Guid.NewGuid(),
            Name = "inventory-mcp",
            Url = "https://mcp.example.test/sse",
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var mcp = new FakeMcp();
        var handler = new McpCallSnippetHandler(db, mcp);

        var canonical = await handler.ExecuteAsync(Request(
            """{"server":"inventory-mcp","tool":"list_sites","arguments":{"region":"eu"}}"""), default);
        Assert.Equal("list_sites", mcp.Tool);
        Assert.Equal("eu", mcp.Arguments.GetProperty("region").GetString());

        var alias = await handler.ExecuteAsync(Request(
            """{"server":"inventory-mcp","tool_name":"list_devices"}"""), default);

        Assert.True(canonical.Success);
        Assert.True(alias.Success);
        Assert.Equal("list_devices", mcp.Tool);
        // Portable output: ok, result, error.
        Assert.True(alias.Output.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, alias.Output.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Mcp_call_without_a_tool_or_a_registered_server_never_reaches_the_service()
    {
        using var db = Db();
        var mcp = new FakeMcp();
        var handler = new McpCallSnippetHandler(db, mcp);

        var noTool = await handler.ExecuteAsync(Request("""{"server":"inventory-mcp"}"""), default);
        var unknown = await handler.ExecuteAsync(
            Request("""{"server":"ghost","tool":"x"}"""), default);

        Assert.Equal("bad_input", noTool.ErrorCode);
        Assert.Equal("not_found", unknown.ErrorCode);
        Assert.Null(mcp.Tool);
    }

    // ── ssh ─────────────────────────────────────────────────────────────

    private sealed class FakeRunner : ISshCommandRunner
    {
        public SshRunRequest? Last;
        public SshRunOutcome Next = SshRunOutcome.Success(new SshRunResult
        {
            DeviceType = "cisco_ios",
            Results = [new SshCommandResult { Command = "show version", Output = "IOS 17.3", Ok = true, ElapsedMs = 12 }],
        });

        public Task<SshRunOutcome> RunAsync(SshRunRequest request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(Next);
        }
    }

    private sealed class AllowAll : ISshCommandPolicy
    {
        public string Classify(string command) => "read";
        public CommandPolicyDecision Evaluate(IReadOnlyList<string> commands) => new(true, [], []);
    }

    private static async Task<(SshSnippetHandler Handler, FakeRunner Runner, FakeSecrets Secrets, AppDbContext Db)>
        NewSsh(bool withCredential = true)
    {
        var db = Db();
        var protector = new FakeProtector();
        var credential = new Credential
        {
            CredentialId = Guid.NewGuid(),
            Name = "device-default",
            AuthMethod = Credential.AuthMethodPassword,
            Username = "netops",
            EncryptedPassword = protector.Encrypt("device-pw"),
            IsActive = true,
        };
        var other = new Credential
        {
            CredentialId = Guid.NewGuid(),
            Name = "break-glass",
            AuthMethod = Credential.AuthMethodPassword,
            Username = "root",
            EncryptedPassword = protector.Encrypt("break-glass-pw"),
            IsActive = true,
        };
        db.Credentials.AddRange(credential, other);
        db.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = "core-sw",
            IpAddress = "10.0.0.9",
            Platform = "cisco_ios",
            CredentialId = withCredential ? credential.CredentialId : null,
            AllowDraft = true,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var runner = new FakeRunner();
        var secrets = new FakeSecrets();
        return (new SshSnippetHandler(db, protector, secrets, runner, new AllowAll(),
            NullLogger<SshSnippetHandler>.Instance), runner, secrets, db);
    }

    [Fact]
    public async Task Ssh_accepts_a_single_command_string_as_the_alias_of_commands()
    {
        var (handler, runner, _, db) = await NewSsh();

        var single = await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version"}"""), default);
        Assert.True(single.Success);
        Assert.Equal(["show version"], runner.Last!.Commands);

        await handler.ExecuteAsync(
            Request("""{"device":"core-sw","commands":["show version","show ip int brief"]}"""), default);
        Assert.Equal(2, runner.Last!.Commands.Count);

        // Portable output: results[], stdout, exit_code.
        Assert.Equal("IOS 17.3", single.Output.GetProperty("stdout").GetString());
        var row = Assert.Single(single.Output.GetProperty("results").EnumerateArray());
        Assert.Equal("show version", row.GetProperty("command").GetString());
        Assert.True(row.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, single.Output.GetProperty("exit_code").ValueKind);
        db.Dispose();
    }

    // §ssh: `host` is an IP and the handler resolves it to an inventory device.
    // "No match → `not_found`, never an ad-hoc connection" — the environment gate and
    // the host-key pin live on the device row, and a machine with neither must stay
    // unreachable from a workflow.
    [Fact]
    public async Task Ssh_resolves_host_to_an_inventory_device_by_ip_and_refuses_an_unknown_one()
    {
        var (handler, runner, _, db) = await NewSsh();

        var known = await handler.ExecuteAsync(
            Request("""{"host":"10.0.0.9","command":"show version"}"""), default);
        var unknown = await handler.ExecuteAsync(
            Request("""{"host":"192.0.2.77","command":"show version"}"""), default);

        Assert.True(known.Success);
        Assert.Equal("10.0.0.9", runner.Last!.Host);
        Assert.Equal("core-sw", known.Output.GetProperty("device").GetString());
        Assert.False(unknown.Success);
        Assert.Equal("not_found", unknown.ErrorCode);
        db.Dispose();
    }

    [Fact]
    public async Task Ssh_credential_names_a_credential_that_overrides_the_devices_own()
    {
        var (handler, runner, _, db) = await NewSsh();

        var inherited = await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version"}"""), default);
        Assert.True(inherited.Success);
        Assert.Equal("netops", runner.Last!.Username);
        Assert.Equal("device-pw", runner.Last!.Password);

        var overridden = await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version","credential":"break-glass"}"""), default);
        Assert.True(overridden.Success);
        Assert.Equal("root", runner.Last!.Username);
        Assert.Equal("break-glass-pw", runner.Last!.Password);

        var missing = await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version","credential":"nope"}"""), default);
        Assert.Equal("not_found", missing.ErrorCode);
        db.Dispose();
    }

    [Fact]
    public async Task Ssh_takes_use_structured_or_the_structured_alias()
    {
        var (handler, runner, _, db) = await NewSsh();

        await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version","structured":false}"""), default);
        Assert.False(runner.Last!.UseStructured);

        await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version","use_structured":true,"structured":false}"""),
            default);
        Assert.True(runner.Last!.UseStructured);
        db.Dispose();
    }

    [Fact]
    public async Task Ssh_takes_enable_secret_or_the_enable_alias()
    {
        var (handler, runner, secrets, db) = await NewSsh();
        secrets.Values["${secret:credential:enable:password}"] = "en4ble";

        await handler.ExecuteAsync(Request(
            """{"device":"core-sw","command":"show run","enable_secret":"${secret:credential:enable:password}"}"""),
            default);
        Assert.Equal("en4ble", runner.Last!.EnableSecret);

        // The string form of the alias is a reference like enable_secret …
        await handler.ExecuteAsync(Request(
            """{"device":"core-sw","command":"show run","enable":"${secret:credential:enable:password}"}"""), default);
        Assert.Equal("en4ble", runner.Last!.EnableSecret);

        // … and the boolean form means "use the credential's password".
        await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show run","enable":true}"""), default);
        Assert.Equal("device-pw", runner.Last!.EnableSecret);
        db.Dispose();
    }

    // §ssh: username/password/private_key/key_passphrase are "not portable as plain
    // values". A plain secret in a node would land in the stored input snapshot and
    // travel inside any bundle exported from this workflow; a ${secret:…} reference
    // is resolved right before the wire and never stored.
    [Fact]
    public async Task Ssh_refuses_plain_secret_material_but_resolves_a_secret_reference()
    {
        var (handler, runner, secrets, db) = await NewSsh();
        secrets.Values["${secret:credential:break-glass:password}"] = "resolved-pw";

        var plainPassword = await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version","password":"hunter2"}"""), default);
        var plainKey = await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version","private_key":"-----BEGIN KEY-----"}"""), default);
        var plainPassphrase = await handler.ExecuteAsync(
            Request("""{"device":"core-sw","command":"show version","key_passphrase":"hunter2"}"""), default);

        Assert.Equal("bad_input", plainPassword.ErrorCode);
        Assert.Equal("bad_input", plainKey.ErrorCode);
        Assert.Equal("bad_input", plainPassphrase.ErrorCode);
        Assert.Null(runner.Last);

        var referenced = await handler.ExecuteAsync(Request("""
            {"device":"core-sw","command":"show version","username":"admin",
             "password":"${secret:credential:break-glass:password}"}
            """), default);

        Assert.True(referenced.Success);
        Assert.Equal("admin", runner.Last!.Username);
        Assert.Equal("resolved-pw", runner.Last!.Password);

        // A reference that resolves to nothing is refused rather than sent as the
        // literal marker, which would fail as a wrong password.
        var dangling = await handler.ExecuteAsync(Request("""
            {"device":"core-sw","command":"show version","username":"admin",
             "password":"${secret:credential:gone:password}"}
            """), default);
        Assert.Equal("secret_unresolved", dangling.ErrorCode);
        db.Dispose();
    }

    [Fact]
    public async Task Ssh_without_a_target_or_without_commands_is_bad_input()
    {
        var (handler, runner, _, db) = await NewSsh();

        var noTarget = await handler.ExecuteAsync(Request("""{"command":"show version"}"""), default);
        var noCommands = await handler.ExecuteAsync(Request("""{"device":"core-sw"}"""), default);

        Assert.Equal("bad_input", noTarget.ErrorCode);
        Assert.Equal("bad_input", noCommands.ErrorCode);
        Assert.Null(runner.Last);
        db.Dispose();
    }

    // ── ping ────────────────────────────────────────────────────────────

    // Port 1 on loopback: nothing listens there, so the probe returns without
    // waiting and without leaving the machine.
    private const int ClosedPort = 1;

    // The oracle's field set, on a target that does not answer. Nothing listens on
    // port 1, so `reachable` is false here — asserted outright rather than branched
    // on, which is what this test used to do and what made it prove nothing.
    [Fact]
    public async Task Ping_reports_the_oracles_field_set_on_an_unreachable_target()
    {
        using var db = Db();
        var handler = new PingSnippetHandler(db, NullLogger<PingSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request(
            $$"""{"host":"127.0.0.1","port":{{ClosedPort}},"count":3,"timeout_ms":500}"""), default);

        // Unreachable is a result, not a step failure: the graph branches on it.
        Assert.True(result.Success);
        Assert.False(result.Output.GetProperty("reachable").GetBoolean());

        // FlowWeaver's PingHandler emits exactly these; the contract's portable set
        // is theirs, not the quartet the spec once invented.
        Assert.False(result.Output.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.Output.GetProperty("packets_sent").GetInt32());
        Assert.Equal(0, result.Output.GetProperty("packets_received").GetInt32());
        Assert.Equal(1d, result.Output.GetProperty("packet_loss").GetDouble());
        // 0, not the elapsed time of a refused connect — what FW reports with no reply.
        Assert.Equal(0d, result.Output.GetProperty("rtt_avg_ms").GetDouble());
        Assert.Contains("did not answer", result.Output.GetProperty("raw_output").GetString());

        // `count` is an ICMP notion; a TCP prober accepts and ignores it rather than
        // failing a portable workflow over a key it has no use for — which is why
        // packets_sent is 1 above and not the 3 that was asked for.
        Assert.Equal("tcp", result.Output.GetProperty("method").GetString());
        Assert.Equal("127.0.0.1", result.Output.GetProperty("host").GetString());
        // Null latency on an unreachable host: the elapsed time of a refused connect
        // is not a latency, and a template averaging latencies must not count it.
        Assert.Equal(JsonValueKind.Null, result.Output.GetProperty("latency_ms").ValueKind);
    }

    // The same fields on a target that does answer, so the mapping is pinned at both
    // ends: one connection attempt is one packet, and a completed handshake is its
    // reply. A listener on port 0 gets a free ephemeral port from the OS, so the test
    // never collides with whatever else the machine is running.
    [Fact]
    public async Task Ping_maps_one_tcp_connect_onto_the_oracles_packet_counts()
    {
        using var db = Db();
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var handler = new PingSnippetHandler(db, NullLogger<PingSnippetHandler>.Instance);

            var result = await handler.ExecuteAsync(Request(
                $$"""{"host":"127.0.0.1","port":{{port}},"timeout_ms":2000}"""), default);

            Assert.True(result.Success);
            Assert.True(result.Output.GetProperty("success").GetBoolean());
            Assert.True(result.Output.GetProperty("reachable").GetBoolean());
            Assert.Equal(1, result.Output.GetProperty("packets_sent").GetInt32());
            Assert.Equal(1, result.Output.GetProperty("packets_received").GetInt32());
            Assert.Equal(0d, result.Output.GetProperty("packet_loss").GetDouble());
            // The connect time, averaged over the single sample there is.
            Assert.True(result.Output.GetProperty("rtt_avg_ms").GetDouble() >= 0);
            Assert.Equal(JsonValueKind.Number, result.Output.GetProperty("latency_ms").ValueKind);
            Assert.Contains("answered in", result.Output.GetProperty("raw_output").GetString());
            Assert.Equal("tcp", result.Output.GetProperty("method").GetString());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Ping_takes_device_as_the_alias_of_host_and_resolves_it_through_inventory()
    {
        using var db = Db();
        db.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = "core-sw",
            IpAddress = "127.0.0.1",
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var handler = new PingSnippetHandler(db, NullLogger<PingSnippetHandler>.Instance);

        var byName = await handler.ExecuteAsync(Request(
            $$"""{"device":"core-sw","port":{{ClosedPort}},"timeout_ms":500}"""), default);
        var unknown = await handler.ExecuteAsync(Request(
            $$"""{"device":"ghost","port":{{ClosedPort}},"timeout_ms":500}"""), default);
        var noTarget = await handler.ExecuteAsync(Request("{}"), default);

        Assert.True(byName.Success);
        // The output names the address actually probed, not the inventory name.
        Assert.Equal("127.0.0.1", byName.Output.GetProperty("host").GetString());
        Assert.False(unknown.Success);
        Assert.Equal("not_found", unknown.ErrorCode);
        Assert.Equal("bad_input", noTarget.ErrorCode);
    }

    // ── report ──────────────────────────────────────────────────────────

    // FW's structured document renders through the same pipeline as Nashira's
    // markdown `content`, so a shared workflow produces the same artifact either way.
    [Fact]
    public async Task Report_renders_the_flow_weaver_document_shape()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request("""
            {"document":{"title":"Link audit","subtitle":"nightly",
              "sections":[
                {"title":"Down links","markdown":"Two links are down.",
                 "tables":[{"headers":["device","port"],"rows":[["r1","Gi0/1"],["r2","Gi0/2"]]}]}]}}
            """), default);

        Assert.True(result.Success);
        var body = Encoding.UTF8.GetString(
            Convert.FromBase64String(result.Output.GetProperty("base64").GetString()!));
        Assert.Contains("# Link audit", body);
        Assert.Contains("## Down links", body);
        Assert.Contains("Two links are down.", body);
        Assert.Contains("| device | port |", body);
        Assert.Contains("| r2 | Gi0/2 |", body);

        // Portable output, whichever body form was used.
        foreach (var key in new[]
        {
            "report_artifact_id", "filename", "content_type", "format", "size_bytes", "sha256",
            "download_url", "base64",
        })
            Assert.True(result.Output.TryGetProperty(key, out _), key);
    }

    [Fact]
    public async Task Report_takes_content_markdown_as_the_alias_of_document()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request(
            """{"title":"Link audit","content":"Two links are down."}"""), default);

        Assert.True(result.Success);
        Assert.Equal("markdown", result.Output.GetProperty("format").GetString());
        var body = Encoding.UTF8.GetString(
            Convert.FromBase64String(result.Output.GetProperty("base64").GetString()!));
        Assert.Contains("# Link audit", body);
        Assert.Contains("Two links are down.", body);
    }

    // csv and xlsx carry the document's tables; this body is prose with none, so the
    // step fails naming that — an author who asked for a spreadsheet and got a
    // markdown file would ship the wrong artifact to whoever reads it.
    [Fact]
    public async Task Report_refuses_a_format_it_cannot_render_instead_of_substituting_one()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        var csv = await handler.ExecuteAsync(
            Request("""{"title":"t","content":"c","format":"csv"}"""), default);
        var xlsx = await handler.ExecuteAsync(
            Request("""{"title":"t","content":"c","format":"xlsx"}"""), default);
        var nonsense = await handler.ExecuteAsync(
            Request("""{"title":"t","content":"c","format":"docx"}"""), default);

        Assert.Equal("not_supported", csv.ErrorCode);
        Assert.Equal("not_supported", xlsx.ErrorCode);
        // The refusal has to be actionable: it names the missing table, not the build.
        Assert.Contains("tables", csv.Error);
        Assert.Contains("tables", xlsx.Error);
        // A format outside the contract's list is a typo, not a capability gap.
        Assert.Equal("bad_input", nonsense.ErrorCode);
        Assert.Empty(await db.ReportArtifacts.ToListAsync());
    }

    // ── slack_message ───────────────────────────────────────────────────

    private sealed class FakeDispatcher : INotificationDispatcher
    {
        public Guid? ChannelId;
        public NotificationResult Next = new(true, 200, null, 1, 5);

        public Task<NotificationResult> SendAsync(Guid channelId, string text, Guid? workflowRunId, CancellationToken ct, string? blocksJson = null)
        {
            ChannelId = channelId;
            return Task.FromResult(Next);
        }
    }

    private static NotificationChannel Notification(string name, string kind = NotificationChannel.KindSlack) => new()
    {
        NotificationChannelId = Guid.NewGuid(),
        Name = name,
        Slug = name,
        Kind = kind,
        Enabled = true,
        IsActive = true,
    };

    // `via` names the record that carries the post; `channel` stays the Slack
    // destination the message claims, because a webhook is bound to its channel and
    // cannot be redirected.
    [Fact]
    public async Task Slack_via_picks_the_messaging_record_even_when_several_could_match()
    {
        using var db = Db();
        var primary = Notification("ops-slack");
        var secondary = Notification("noc-slack");
        db.NotificationChannels.AddRange(primary, secondary);
        await db.SaveChangesAsync();
        var dispatcher = new FakeDispatcher();
        var handler = new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(), new PassthroughSecretProtector(), NullLogger<SlackMessageSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request(
            """{"channel":"#net-ops","text":"maintenance done","via":"noc-slack"}"""), default);

        Assert.True(result.Success);
        Assert.Equal(secondary.NotificationChannelId, dispatcher.ChannelId);
        // Portable output: ok, ts, channel, error. `ts` is null on a webhook post —
        // present and null, so a template reading it resolves.
        Assert.True(result.Output.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Output.GetProperty("ts").ValueKind);
        Assert.Equal("#net-ops", result.Output.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Slack_via_that_names_nothing_is_not_found_rather_than_a_silent_fallback()
    {
        using var db = Db();
        db.NotificationChannels.Add(Notification("ops-slack"));
        await db.SaveChangesAsync();
        var dispatcher = new FakeDispatcher();
        var handler = new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(), new PassthroughSecretProtector(), NullLogger<SlackMessageSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request(
            """{"channel":"#net-ops","text":"x","via":"ghost"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("not_found", result.ErrorCode);
        // The one Slack record must NOT have been used as a consolation prize: an
        // explicit `via` that does not exist is a mistake, not a preference.
        Assert.Null(dispatcher.ChannelId);
    }

    // Without `via`: the only Slack record if there is exactly one, else the record
    // whose name matches `channel`, with or without the `#`.
    [Fact]
    public async Task Without_via_the_destination_selects_the_record_by_name()
    {
        using var db = Db();
        var netOps = Notification("net-ops");
        db.NotificationChannels.AddRange(netOps, Notification("noc-slack"));
        await db.SaveChangesAsync();
        var dispatcher = new FakeDispatcher();
        var handler = new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(), new PassthroughSecretProtector(), NullLogger<SlackMessageSnippetHandler>.Instance);

        var hashed = await handler.ExecuteAsync(Request("""{"channel":"#net-ops","text":"x"}"""), default);
        Assert.True(hashed.Success);
        Assert.Equal(netOps.NotificationChannelId, dispatcher.ChannelId);

        dispatcher.ChannelId = null;
        var bare = await handler.ExecuteAsync(Request("""{"channel":"net-ops","text":"x"}"""), default);
        Assert.True(bare.Success);
        Assert.Equal(netOps.NotificationChannelId, dispatcher.ChannelId);

        dispatcher.ChannelId = null;
        var nothing = await handler.ExecuteAsync(Request("""{"channel":"#nowhere","text":"x"}"""), default);
        Assert.Equal("not_found", nothing.ErrorCode);
        Assert.Null(dispatcher.ChannelId);
    }

    [Fact]
    public async Task Without_via_a_single_slack_record_carries_any_destination()
    {
        using var db = Db();
        var only = Notification("ops-slack");
        db.NotificationChannels.AddRange(only, Notification("ops-teams", NotificationChannel.KindTeams));
        await db.SaveChangesAsync();
        var dispatcher = new FakeDispatcher();
        var handler = new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(), new PassthroughSecretProtector(), NullLogger<SlackMessageSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request("""{"channel":"#anything","text":"x"}"""), default);

        Assert.True(result.Success);
        Assert.Equal(only.NotificationChannelId, dispatcher.ChannelId);
    }

    // ── ansible_playbook ────────────────────────────────────────────────

    // `hosts` is canonical; `targets` and `device` are aliases; `all` means the run's
    // target devices. Read directly, because the handler itself only runs on a Linux
    // worker and the key translation is what a bundle depends on.
    [Fact]
    public void Ansible_reads_hosts_targets_and_device_into_one_inventory()
    {
        var hosts = Ansible("""{"hosts":["r1","r2"]}""");
        Assert.Equal(["r1", "r2"], hosts.Refs);
        Assert.False(hosts.All);

        var lone = Ansible("""{"hosts":"r1"}""");
        Assert.Equal(["r1"], lone.Refs);

        var targets = Ansible("""{"targets":["r3"]}""");
        Assert.Equal(["r3"], targets.Refs);

        var device = Ansible("""{"device":"core-sw"}""");
        Assert.Equal(["core-sw"], device.Refs);

        // Combined, and a device named twice is one inventory line.
        var combined = Ansible("""{"hosts":["r1","r2"],"targets":["r2","r3"],"device":"r1"}""");
        Assert.Equal(["r1", "r2", "r3"], combined.Refs);

        var none = Ansible("""{"extra_vars":{"a":1}}""");
        Assert.Empty(none.Refs);
        Assert.False(none.All);
    }

    // `all` is the run's target devices — which this handler is not handed. Saying so
    // beats guessing: a playbook run against the wrong set of devices is not a
    // recoverable mistake.
    [Fact]
    public void Ansible_recognises_hosts_all_as_the_runs_targets()
    {
        Assert.True(Ansible("""{"hosts":"all"}""").All);
        Assert.True(Ansible("""{"hosts":["all"]}""").All);
        Assert.True(Ansible("""{"targets":"ALL"}""").All);
        // `all` beside explicit names still means "all" — the names do not narrow it.
        var mixed = Ansible("""{"hosts":["all","r1"]}""");
        Assert.True(mixed.All);
        Assert.Equal(["r1"], mixed.Refs);
    }

    private static (List<string> Refs, bool All) Ansible(string json) =>
        AnsiblePlaybookSnippetHandler.HostRefs(JsonDocument.Parse(json).RootElement.Clone());
}
