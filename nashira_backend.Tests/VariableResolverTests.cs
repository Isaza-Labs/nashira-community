using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Engine;

namespace nashira_backend.Tests;

// Template resolution is how one step's output reaches the next. A silent
// mistranslation here corrupts a payload that then fails deep inside a handler.
public class VariableResolverTests
{
    private static readonly VariableResolver Resolver = new(NullLogger<VariableResolver>.Instance);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static Dictionary<string, StepResult> Steps(params (string Node, string Output)[] steps) =>
        steps.ToDictionary(s => s.Node, s => new StepResult(Json(s.Output)), StringComparer.Ordinal);

    // The load-bearing distinction: a whole-string template keeps the referenced
    // value's type. Without it every payload field would arrive as a string and a
    // handler expecting a number would have to guess.
    [Fact]
    public void A_whole_string_template_preserves_the_value_type()
    {
        var steps = Steps(("ping", """{"latency_ms":42,"reachable":true,"tags":["a","b"]}"""));

        var result = Resolver.Resolve(
            Json("""{"n":"{{ steps.ping.output.latency_ms }}","b":"{{ steps.ping.output.reachable }}","a":"{{ steps.ping.output.tags }}"}"""),
            steps);

        Assert.Equal(JsonValueKind.Number, result.GetProperty("n").ValueKind);
        Assert.Equal(42, result.GetProperty("n").GetInt32());
        Assert.Equal(JsonValueKind.True, result.GetProperty("b").ValueKind);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("a").ValueKind);
    }

    [Fact]
    public void An_inline_template_splices_the_value_into_the_surrounding_text()
    {
        var steps = Steps(("ping", """{"latency_ms":42}"""));

        var result = Resolver.Resolve(Json("""{"msg":"rtt={{ steps.ping.output.latency_ms }}ms"}"""), steps);

        Assert.Equal("rtt=42ms", result.GetProperty("msg").GetString());
    }

    [Fact]
    public void A_bare_output_reference_returns_the_whole_object()
    {
        var steps = Steps(("ping", """{"latency_ms":42}"""));

        var result = Resolver.Resolve(Json("""{"all":"{{ steps.ping.output }}"}"""), steps);

        Assert.Equal(42, result.GetProperty("all").GetProperty("latency_ms").GetInt32());
    }

    [Fact]
    public void Array_indexing_and_dotted_paths_compose()
    {
        var steps = Steps(("ssh", """{"results":[{"ok":true,"output":"up"},{"ok":false}]}"""));

        var result = Resolver.Resolve(Json("""{"first":"{{ steps.ssh.output.results[0].output }}"}"""), steps);

        Assert.Equal("up", result.GetProperty("first").GetString());
    }

    // An unresolvable reference stays literal rather than becoming null. A payload
    // that silently nulled would fail inside a handler with no trace of the cause;
    // the literal template shows up in the step input and names its own typo.
    [Fact]
    public void An_unknown_step_is_left_literal()
    {
        var result = Resolver.Resolve(Json("""{"x":"{{ steps.typo.output.field }}"}"""), Steps());

        Assert.Equal("{{ steps.typo.output.field }}", result.GetProperty("x").GetString());
    }

    [Fact]
    public void A_missing_path_segment_is_left_literal()
    {
        var steps = Steps(("ping", """{"latency_ms":42}"""));

        var result = Resolver.Resolve(Json("""{"x":"{{ steps.ping.output.absent }}"}"""), steps);

        Assert.Equal("{{ steps.ping.output.absent }}", result.GetProperty("x").GetString());
    }

    [Fact]
    public void Hyphenated_node_ids_resolve()
    {
        // workflow.v1 constrains node id length, not its character set.
        var steps = Steps(("drain-edge", """{"done":true}"""));

        var result = Resolver.Resolve(Json("""{"x":"{{ steps.drain-edge.output.done }}"}"""), steps);

        Assert.Equal(JsonValueKind.True, result.GetProperty("x").ValueKind);
    }

    [Fact]
    public void Device_and_input_contexts_resolve_independently()
    {
        var result = Resolver.Resolve(
            Json("""{"host":"{{ device.ip }}","who":"{{ input.requested_by }}"}"""),
            Steps(),
            deviceContext: Json("""{"ip":"10.1.2.3"}"""),
            runInput: Json("""{"requested_by":"neteng"}"""));

        Assert.Equal("10.1.2.3", result.GetProperty("host").GetString());
        Assert.Equal("neteng", result.GetProperty("who").GetString());
    }

    // `{{ deviceName }}` must not be read as the `device` context — the regex
    // anchors on a word boundary precisely to stop that.
    [Fact]
    public void A_word_starting_with_device_is_not_treated_as_the_device_context()
    {
        var result = Resolver.Resolve(
            Json("""{"x":"{{ deviceName }}"}"""), Steps(), deviceContext: Json("""{"ip":"10.1.2.3"}"""));

        Assert.Equal("{{ deviceName }}", result.GetProperty("x").GetString());
    }

    [Fact]
    public void An_absent_device_context_leaves_the_reference_literal()
    {
        var result = Resolver.Resolve(Json("""{"host":"{{ device.ip }}"}"""), Steps());

        Assert.Equal("{{ device.ip }}", result.GetProperty("host").GetString());
    }

    [Fact]
    public void Nested_objects_and_arrays_are_walked()
    {
        var steps = Steps(("s", """{"v":7}"""));

        var result = Resolver.Resolve(
            Json("""{"outer":{"inner":["{{ steps.s.output.v }}","literal"]}}"""), steps);

        var arr = result.GetProperty("outer").GetProperty("inner");
        Assert.Equal(7, arr[0].GetInt32());
        Assert.Equal("literal", arr[1].GetString());
    }

    [Fact]
    public void A_payload_with_no_templates_survives_unchanged()
    {
        var payload = Json("""{"a":1,"b":"text","c":[true,null],"d":{"e":2.5}}""");

        var result = Resolver.Resolve(payload, Steps());

        Assert.Equal(
            JsonSerializer.Serialize(payload),
            JsonSerializer.Serialize(result));
    }

    [Fact]
    public void An_unbalanced_bracket_resolves_to_nothing_rather_than_a_prefix()
    {
        // Emitting the prefix would silently address the wrong value.
        var steps = Steps(("s", """{"list":[1,2,3]}"""));

        var result = Resolver.Resolve(Json("""{"x":"{{ steps.s.output.list[0 }}"}"""), steps);

        Assert.Equal("{{ steps.s.output.list[0 }}", result.GetProperty("x").GetString());
    }
}
