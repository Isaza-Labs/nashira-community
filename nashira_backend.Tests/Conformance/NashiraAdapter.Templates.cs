using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Engine;

namespace nashira_backend.Tests.Conformance;

// Family `templates` (workflow-v1-conformance/templates/SPEC.md).
//
// Two input forms, per adapters/README.md:
//
//   { outputs, input, device, run, consumer_target_mode, template } -> { resolved }
//   { context: { outputs, input, run, device }, condition }          -> { result }
//
// `resolved` is the template's value after resolution, or the string "unresolved"
// when a residual `{{ … }}` survived — §8's rule is that an unresolvable reference is
// left literal and the step then fails, so "a residual is left" is the observable the
// vectors assert.
//
// Two deliberate shaping decisions, both of them the spec's rule rather than Nashira's:
//
//   - `device` is given as the ELEVEN-FIELD projection plus whatever a product would
//     hold beside it (credential_id, host-key fingerprint, sync timestamp). The adapter
//     loads it into a real inventory row and asks the engine for the view, so
//     `{{ device }}` is answered by VariableResolver.DeviceView and a vector can assert
//     the projection is exactly the eleven §5 names.
//   - the condition form NEVER passes a device view to the evaluator. §9 is explicit
//     that `{{ device.* }}` does not resolve in a condition, because an edge fires once
//     at DAG level after its source completes; in Nashira that rule lives at the call
//     site (WorkflowExecutor.EvaluateCondition passes deviceContext: null), and
//     executor.conditional.device_reference_does_not_fire covers it end to end.
public sealed partial class NashiraAdapter
{
    private const string Unresolved = "unresolved";

    private static JsonElement? Templates(JsonElement input)
    {
        if (input.TryGetProperty("condition", out var condition))
            return Condition(input, condition.GetString() ?? string.Empty);

        if (!input.TryGetProperty("template", out var template))
            throw new InvalidOperationException("a templates vector needs either `template` or `condition`");

        var outputs = StepOutputs(Prop(input, "outputs"));
        var device = Prop(input, "device");
        var deviceView = device is { } d ? VariableResolver.DeviceView(DeviceRow(d)) : (JsonElement?)null;

        var mode = Str(input, "consumer_target_mode") ?? "once";
        if (string.Equals(mode, "per_device", StringComparison.Ordinal) && device is { } scoped)
            outputs = VariableResolver.ScopeOutputsToDevice(outputs, DeviceId(scoped));

        var resolver = new VariableResolver(NullLogger<VariableResolver>.Instance);
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["v"] = template });
        var resolved = resolver.Resolve(payload, outputs, deviceView, Prop(input, "input"), RunJson(input, "run"))
            .GetProperty("v");

        return VariableResolver.FindUnresolvedTemplates(resolved).Count > 0
            ? JsonSerializer.SerializeToElement(new { resolved = Unresolved })
            : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["resolved"] = resolved });
    }

    private static JsonElement Condition(JsonElement input, string condition)
    {
        var ctx = Prop(input, "context") ?? default;
        var evaluator = new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance);
        var result = evaluator.Evaluate(
            condition,
            StepOutputs(Prop(ctx, "outputs")),
            deviceContext: null,
            runInput: Prop(ctx, "input"),
            runContext: RunJson(ctx, "run"));
        return JsonSerializer.SerializeToElement(new { result });
    }

    // ── shared plumbing, also used by the executor family ────────────────────

    internal static IReadOnlyDictionary<string, StepResult> StepOutputs(JsonElement? outputs)
    {
        var map = new Dictionary<string, StepResult>(StringComparer.Ordinal);
        if (outputs is not { ValueKind: JsonValueKind.Object } o) return map;
        foreach (var p in o.EnumerateObject()) map[p.Name] = new StepResult(p.Value.Clone());
        return map;
    }

    // The run namespace is answered by the engine's own RunContext, so a vector asserts
    // the real ten-field projection (§7) rather than whatever the vector happened to
    // supply — that is what makes "null, never absent" testable.
    internal static JsonElement? RunJson(JsonElement holder, string property)
    {
        if (Prop(holder, property) is not { ValueKind: JsonValueKind.Object } run) return null;
        return new RunContext
        {
            Id = GuidOf(run, "id"),
            WorkflowId = GuidOf(run, "workflow_id"),
            WorkflowName = Str(run, "workflow_name") ?? string.Empty,
            Environment = Str(run, "environment") ?? string.Empty,
            Trigger = Str(run, "trigger") ?? string.Empty,
            StartedAt = Str(run, "started_at") is { } at
                ? DateTime.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : default,
            OwnerEmail = Str(run, "owner_email"),
            Url = Str(run, "url"),
        }.ToJson();
    }

    private static nashira_backend.Data.Models.Device DeviceRow(JsonElement d) => new()
    {
        DeviceId = DeviceId(d),
        DeviceName = Str(d, "name") ?? string.Empty,
        IpAddress = Str(d, "ip") ?? string.Empty,
        Platform = Str(d, "platform") ?? string.Empty,
        Vendor = Str(d, "vendor") ?? string.Empty,
        OsVersion = Str(d, "os_version") ?? string.Empty,
        Site = Str(d, "site") ?? string.Empty,
        Role = Str(d, "role") ?? string.Empty,
        Status = Str(d, "status") ?? string.Empty,
        ExternalId = Str(d, "external_id"),
        Properties = Prop(d, "properties") ?? JsonDocument.Parse("{}").RootElement.Clone(),
        // Present on the row and absent from the view: that asymmetry is the point of §5.
        CredentialId = Str(d, "credential_id") is { } c && Guid.TryParse(c, out var cid) ? cid : null,
        ExpectedSshHostKeyFingerprint = Str(d, "expected_ssh_host_key_fingerprint"),
        LastSyncAt = Str(d, "last_sync_at") is { } ls
            ? DateTime.Parse(ls, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null,
    };

    private static Guid DeviceId(JsonElement d) => GuidOf(d, "id");

    private static Guid GuidOf(JsonElement el, string prop) =>
        Str(el, prop) is { } s && Guid.TryParse(s, out var g) ? g : Guid.Empty;

    internal static JsonElement? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : null;

    internal static JsonElement? Prop(JsonElement? el, string name) =>
        el is { } e ? Prop(e, name) : null;
}
