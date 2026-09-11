using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Engine;

namespace nashira_backend.Tests;

// workflow.v1 templates/SPEC.md §4 (filters), §6 (per-device scoping), §7 (run
// namespace) and §9 (filters inside conditions). Flow Weaver is the oracle: a
// template that resolves there has to resolve to the same value here, or a shared
// workflow runs on one engine and not the other.
public class TemplateFilterTests
{
    private static readonly VariableResolver Resolver = new(NullLogger<VariableResolver>.Instance);
    private static readonly ConditionEvaluator Eval = new(NullLogger<ConditionEvaluator>.Instance);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static Dictionary<string, StepResult> Steps(params (string Node, string Output)[] steps) =>
        steps.ToDictionary(s => s.Node, s => new StepResult(Json(s.Output)), StringComparer.Ordinal);

    private static JsonElement One(string template, IReadOnlyDictionary<string, StepResult> steps,
        JsonElement? device = null, JsonElement? input = null, JsonElement? run = null) =>
        Resolver.Resolve(Json($$"""{"v":{{JsonSerializer.Serialize(template)}}}"""), steps, device, input, run)
            .GetProperty("v");

    // ── default ─────────────────────────────────────────────────────────

    // The one filter allowed to recover an unresolved reference.
    [Fact]
    public void Default_recovers_an_unresolved_reference_and_keeps_the_literal_otherwise()
    {
        var steps = Steps(("s", """{"present":"yes"}"""));

        Assert.Equal("n/a", One("{{ steps.s.output.missing | default('n/a') }}", steps).GetString());
        Assert.Equal("n/a", One("{{ steps.typo.output.x | default(\"n/a\") }}", steps).GetString());
        Assert.Equal("yes", One("{{ steps.s.output.present | default('n/a') }}", steps).GetString());
    }

    // Null and the empty string are "nothing there" too, same rule as the oracle.
    [Fact]
    public void Default_also_stands_in_for_null_and_empty()
    {
        var steps = Steps(("s", """{"n":null,"e":""}"""));

        Assert.Equal("-", One("{{ steps.s.output.n | default('-') }}", steps).GetString());
        Assert.Equal("-", One("{{ steps.s.output.e | default('-') }}", steps).GetString());
    }

    [Fact]
    public void Any_other_filter_on_an_unresolved_reference_leaves_the_template_literal()
    {
        var steps = Steps(("s", """{"n":null}"""));

        Assert.Equal("{{ steps.s.output.missing | upper }}", One("{{ steps.s.output.missing | upper }}", steps).GetString());
        Assert.Equal("{{ steps.s.output.n | trim }}", One("{{ steps.s.output.n | trim }}", steps).GetString());
        // …but default earlier in the chain feeds the rest of it.
        Assert.Equal("N/A", One("{{ steps.s.output.missing | default('n/a') | upper }}", steps).GetString());
    }

    // ── string filters ───────────────────────────────────────────────────

    [Theory]
    [InlineData("{{ steps.s.output.t | trim }}", "  padded  ", "padded")]
    [InlineData("{{ steps.s.output.t | upper }}", "Up", "UP")]
    [InlineData("{{ steps.s.output.t | lower }}", "Down", "down")]
    [InlineData("{{ steps.s.output.t | truncate(3) }}", "abcdef", "abc")]
    [InlineData("{{ steps.s.output.t | trim | upper }}", "  x ", "X")]
    public void String_filters_transform_strings(string template, string value, string expected)
    {
        var steps = Steps(("s", $$"""{"t":{{JsonSerializer.Serialize(value)}}}"""));
        Assert.Equal(expected, One(template, steps).GetString());
    }

    [Fact]
    public void Truncate_defaults_to_two_hundred_characters()
    {
        var steps = Steps(("s", $$"""{"t":"{{new string('x', 250)}}"}"""));
        Assert.Equal(200, One("{{ steps.s.output.t | truncate }}", steps).GetString()!.Length);
    }

    // §4: "on strings; non-strings pass through". Whole-string mode keeps the type.
    [Fact]
    public void String_filters_pass_non_strings_through_with_their_type()
    {
        var steps = Steps(("s", """{"n":42,"b":true}"""));

        var n = One("{{ steps.s.output.n | upper }}", steps);
        Assert.Equal(JsonValueKind.Number, n.ValueKind);
        Assert.Equal(42, n.GetInt32());
        Assert.Equal(JsonValueKind.True, One("{{ steps.s.output.b | trim }}", steps).ValueKind);
    }

    [Fact]
    public void Json_renders_the_value_as_a_json_string()
    {
        var steps = Steps(("s", """{"o":{"a":1},"t":"x"}"""));

        var o = One("{{ steps.s.output.o | json }}", steps);
        Assert.Equal(JsonValueKind.String, o.ValueKind);
        Assert.Equal("""{"a":1}""", o.GetString());
        Assert.Equal("\"x\"", One("{{ steps.s.output.t | json }}", steps).GetString());
    }

    [Fact]
    public void Strip_ansi_removes_escapes_and_strip_also_drops_controls_and_trims()
    {
        var steps = Steps(("s", """{"t":"  \u001b[31mERR\u001b[0m\u0007 done \u001b]0;title\u0007 "}"""));

        Assert.Equal("  ERR\u0007 done  ", One("{{ steps.s.output.t | strip_ansi }}", steps).GetString());
        Assert.Equal("ERR done", One("{{ steps.s.output.t | strip }}", steps).GetString());
    }

    [Fact]
    public void Unknown_filters_are_ignored()
    {
        var steps = Steps(("s", """{"t":"v"}"""));
        Assert.Equal("V", One("{{ steps.s.output.t | frobnicate | upper }}", steps).GetString());
    }

    // ── parsing ──────────────────────────────────────────────────────────

    [Fact]
    public void A_pipe_inside_quotes_or_parentheses_is_not_a_separator()
    {
        var steps = Steps(("s", "{}"));
        Assert.Equal("a|b", One("{{ steps.s.output.x | default('a|b') }}", steps).GetString());
    }

    [Fact]
    public void Filters_work_inline_and_on_every_namespace()
    {
        var steps = Steps(("s", """{"t":"ok"}"""));
        var device = Json("""{"name":"r1"}""");
        var input = Json("""{"env":"lab"}""");
        var run = Json("""{"trigger":"manual"}""");

        var r = Resolver.Resolve(Json("""
            {"msg":"[{{ steps.s.output.t | upper }}] {{ device.name | upper }} {{ input.env | upper }} {{ run.trigger | upper }} {{ input.none | default('x') }}"}
            """), steps, device, input, run);

        Assert.Equal("[OK] R1 LAB MANUAL x", r.GetProperty("msg").GetString());
    }

    [Fact]
    public void ParseExpression_splits_path_and_filters_with_quoted_arguments()
    {
        var parsed = VariableResolver.ParseExpression(".a.b | truncate(5) | default('x, y', \"z\")");

        Assert.Equal(".a.b", parsed.Path);
        Assert.Equal(2, parsed.Filters.Count);
        Assert.Equal("truncate", parsed.Filters[0].Name);
        Assert.Equal(new[] { "5" }, parsed.Filters[0].Args);
        Assert.Equal("default", parsed.Filters[1].Name);
        Assert.Equal(new[] { "x, y", "z" }, parsed.Filters[1].Args);
    }

    // §2: a malformed path is a resolution failure, not the root value.
    [Theory]
    [InlineData("{{ steps.s.output..a }}")]
    [InlineData("{{ steps.s.output.a. }}")]
    [InlineData("{{ steps.s.output.list[x] }}")]
    public void A_malformed_path_is_left_literal(string template)
    {
        var steps = Steps(("s", """{"a":{"b":1},"list":[1]}"""));
        Assert.Equal(template, One(template, steps).GetString());
    }

    [Fact]
    public void Quoted_bracket_access_names_a_key_that_is_not_an_identifier()
    {
        var steps = Steps(("s", """{"by_host":{"r-1.lab":{"up":true}}}"""));
        Assert.Equal(JsonValueKind.True, One("{{ steps.s.output.by_host['r-1.lab'].up }}", steps).ValueKind);
    }

    // ── run namespace ────────────────────────────────────────────────────

    private static RunContext Run() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        WorkflowId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        WorkflowName = "nightly",
        Environment = "qa",
        Trigger = "schedule",
        StartedAt = new DateTime(2026, 8, 27, 1, 2, 3, DateTimeKind.Utc),
    };

    // Every field PRESENT, and the two kinds of absence distinguished. `owner_email` and
    // `url` are genuinely unavailable in this build, so they are null. The two failed-step
    // fields are seeded PLACEHOLDERS and are empty strings: a notify node reached by an
    // `always` edge reads them on the success path, often as the whole value of a message
    // body, and a JSON null there makes a handler that requires a body refuse a step that used
    // to work. The contract's oracle seeds them the same way; a vector said otherwise and was
    // corrected rather than followed.
    [Fact]
    public void The_run_context_carries_all_ten_fields_distinguishing_absent_from_unset()
    {
        var json = Run().ToJson();

        foreach (var field in new[] { "id", "workflow_id", "workflow_name", "environment", "trigger",
                     "started_at", "owner_email", "url", "failed_step_id", "failed_step_error" })
            Assert.True(json.TryGetProperty(field, out _), $"missing run.{field}");

        Assert.Equal("nightly", json.GetProperty("workflow_name").GetString());
        Assert.Equal("2026-08-27T01:02:03.0000000Z", json.GetProperty("started_at").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("owner_email").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("url").ValueKind);
        Assert.Equal(string.Empty, json.GetProperty("failed_step_id").GetString());
        Assert.Equal(string.Empty, json.GetProperty("failed_step_error").GetString());
    }

    [Fact]
    public void Run_references_resolve_whole_string_and_inline()
    {
        var run = Run().ToJson();
        var steps = Steps();

        Assert.Equal("11111111-1111-1111-1111-111111111111", One("{{ run.id }}", steps, run: run).GetString());
        Assert.Equal("run nightly in qa (schedule)",
            One("run {{ run.workflow_name }} in {{ run.environment }} ({{ run.trigger }})", steps, run: run).GetString());
        // A null field resolves — to null — rather than being left literal.
        Assert.Equal(JsonValueKind.Null, One("{{ run.owner_email }}", steps, run: run).ValueKind);
        Assert.Equal("owner: ", One("owner: {{ run.owner_email }}", steps, run: run).GetString());
        Assert.Equal("nobody", One("{{ run.owner_email | default('nobody') }}", steps, run: run).GetString());
        // The whole object, typed.
        Assert.Equal(JsonValueKind.Object, One("{{ run }}", steps, run: run).ValueKind);
    }

    [Fact]
    public void The_failed_step_fields_fill_in_when_a_step_fails()
    {
        var ctx = Run();
        ctx.MarkFailed("push-config", "HTTP 500");
        var json = ctx.ToJson();

        Assert.Equal("push-config", json.GetProperty("failed_step_id").GetString());
        Assert.Equal("HTTP 500", json.GetProperty("failed_step_error").GetString());
        Assert.Equal("push-config failed: HTTP 500",
            One("{{ run.failed_step_id }} failed: {{ run.failed_step_error }}", Steps(), run: json).GetString());
    }

    [Fact]
    public void Without_a_run_context_a_run_reference_stays_literal_and_runs_is_not_run()
    {
        Assert.Equal("{{ run.id }}", One("{{ run.id }}", Steps()).GetString());
        Assert.Equal("{{ runs.id }}", One("{{ runs.id }}", Steps(), run: Run().ToJson()).GetString());
    }

    // ── per-device scoping ───────────────────────────────────────────────

    private static readonly Guid R1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid R2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static Dictionary<string, StepResult> FanOut() => Steps(
        ("show", $$$"""
            {"per_device":true,"total":2,"failed":0,"devices":[
              {"device_id":"{{{R1}}}","device":"r1","success":true,"output":{"stdout":"r1 up"}},
              {"device_id":"{{{R2}}}","device":"r2","success":true,"output":{"stdout":"r2 up"}}]}
            """),
        ("once", """{"devices":[{"name":"inventory row"}],"count":1}"""));

    [Fact]
    public void A_per_device_consumer_reads_its_own_devices_output()
    {
        var scoped = VariableResolver.ScopeOutputsToDevice(FanOut(), R2);

        Assert.Equal("r2 up", One("{{ steps.show.output.stdout }}", scoped).GetString());
        // Nashira's extra envelope fields are gone with the envelope.
        Assert.Equal("{{ steps.show.output.total }}", One("{{ steps.show.output.total }}", scoped).GetString());
    }

    [Fact]
    public void A_once_consumer_sees_the_full_aggregate()
    {
        var steps = FanOut();
        Assert.Equal(2, One("{{ steps.show.output.total }}", steps).GetInt32());
        Assert.Equal("r1 up", One("{{ steps.show.output.devices[0].output.stdout }}", steps).GetString());
    }

    // A `devices` list without device ids is data, not a fan-out envelope.
    [Fact]
    public void Scoping_leaves_outputs_that_merely_contain_a_devices_list_alone()
    {
        var scoped = VariableResolver.ScopeOutputsToDevice(FanOut(), R1);
        Assert.Equal("inventory row", One("{{ steps.once.output.devices[0].name }}", scoped).GetString());
    }

    // §6: a producer with no entry for this device resolves to unresolved.
    [Fact]
    public void A_producer_without_an_entry_for_the_device_is_unresolved()
    {
        var scoped = VariableResolver.ScopeOutputsToDevice(FanOut(), Guid.NewGuid());

        Assert.False(scoped.ContainsKey("show"));
        Assert.Equal("{{ steps.show.output.stdout }}", One("{{ steps.show.output.stdout }}", scoped).GetString());
        Assert.Equal("none", One("{{ steps.show.output.stdout | default('none') }}", scoped).GetString());
    }

    // ── conditions ───────────────────────────────────────────────────────

    [Fact]
    public void Conditions_accept_filters_inside_references()
    {
        var steps = Steps(("ssh", """{"stdout":"  \u001b[32mOK\u001b[0m  ","code":null}"""));

        Assert.True(Eval.Evaluate("{{ steps.ssh.output.stdout | strip | lower }} == \"ok\"", steps));
        Assert.True(Eval.Evaluate("{{ steps.ssh.output.code | default('0') }} == 0", steps));
        Assert.False(Eval.Evaluate("{{ steps.ssh.output.stdout | strip }} == ko", steps));
    }

    [Fact]
    public void Conditions_read_the_run_namespace()
    {
        var run = Run().ToJson();
        Assert.True(Eval.Evaluate("{{ run.trigger }} == schedule && {{ run.environment }} != production", Steps(), runContext: run));
        Assert.False(Eval.Evaluate("{{ run.trigger }} == manual", Steps(), runContext: run));
    }

    // §9: an unresolved comparison is false whatever the operator — `!=` included.
    [Fact]
    public void An_unresolved_comparison_is_false_even_with_not_equals()
    {
        Assert.False(Eval.Evaluate("{{ steps.ghost.output.x }} != 1", Steps()));
        Assert.False(Eval.Evaluate("{{ steps.ghost.output.x }}", Steps()));
        Assert.True(Eval.Evaluate("{{ steps.ghost.output.x | default('1') }} == 1", Steps()));
    }

    // ── residual scan (§8) ───────────────────────────────────────────────

    // §8: an unresolved reference is left literal in the payload — the operator has
    // to be able to see the typo — and then the engine scans for it and fails the
    // step with `unresolved_template` before the handler can act on the literal.
    // Without the scan, a report node emits a table cell reading
    // `{{ steps.audit.output.rows[0] }}` and the run goes green.
    [Fact]
    public void The_residual_scan_finds_every_leftover_reference_and_says_where_it_is()
    {
        var steps = Steps(("s", """{"stdout":"ok"}"""));
        var payload = Resolver.Resolve(Json("""
            {"good":"{{ steps.s.output.stdout }}",
             "commands":["show ver","{{ steps.typo.output.cmd }}"],
             "document":{"sections":[{"markdown":"count: {{ steps.s.output.missing }}"}]}}
            """), steps);

        var residual = VariableResolver.FindUnresolvedTemplates(payload);

        Assert.Equal(2, residual.Count);
        Assert.Contains(residual, u => u.Location == "$.commands[1]"
                                       && u.TemplateText == "{{ steps.typo.output.cmd }}");
        Assert.Contains(residual, u => u.Location == "$.document.sections[0].markdown"
                                       && u.TemplateText == "{{ steps.s.output.missing }}");

        var message = VariableResolver.DescribeUnresolved(residual);
        Assert.Contains("$.commands[1]", message);
        Assert.Contains("{{ steps.typo.output.cmd }}", message);
    }

    [Fact]
    public void A_fully_resolved_payload_has_nothing_to_report()
    {
        var steps = Steps(("s", """{"stdout":"ok","code":0}"""));
        var payload = Resolver.Resolve(Json("""
            {"a":"{{ steps.s.output.stdout }}","b":"{{ steps.s.output.code }}",
             "c":"{{ steps.s.output.missing | default('n/a') }}","d":"no templates here"}
            """), steps);

        Assert.Empty(VariableResolver.FindUnresolvedTemplates(payload));
    }

    // Secret references are not templates (§8): the resolver never touches them and
    // the residual scan must not mistake one for a broken reference — the handler
    // resolves it right before the wire.
    [Fact]
    public void A_secret_reference_is_not_a_residual_template()
    {
        var payload = Resolver.Resolve(
            Json("""{"password":"${secret:credential:sw:password}"}"""), Steps());

        Assert.Equal("${secret:credential:sw:password}", payload.GetProperty("password").GetString());
        Assert.Empty(VariableResolver.FindUnresolvedTemplates(payload));
    }

    // The §8 exception, from the other side: a per_device step's stored input
    // snapshot is resolved WITHOUT a device context on purpose, so `{{ device.* }}`
    // survives in it. That snapshot is a record, not the payload a handler runs on —
    // the executor scans the per-device payload, which does have the device bound.
    [Fact]
    public void A_device_reference_survives_a_snapshot_resolved_without_a_device()
    {
        var snapshot = Resolver.Resolve(
            Json("""{"host":"{{ device.ip }}","site":"{{ input.site }}"}"""), Steps(),
            deviceContext: null, runInput: Json("""{"site":"hq"}"""));

        Assert.Equal("{{ device.ip }}", snapshot.GetProperty("host").GetString());
        Assert.Equal("hq", snapshot.GetProperty("site").GetString());

        var bound = Resolver.Resolve(
            Json("""{"host":"{{ device.ip }}"}"""), Steps(), deviceContext: Json("""{"ip":"10.0.0.9"}"""));
        Assert.Empty(VariableResolver.FindUnresolvedTemplates(bound));
    }
}
