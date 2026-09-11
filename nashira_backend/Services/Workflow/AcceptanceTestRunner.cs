using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Engine;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Workflow;

public sealed record AcceptanceOutcome(string Status, Guid? RunId, IReadOnlyList<string> Failures);

public interface IAcceptanceTestRunner
{
    Task<AcceptanceOutcome> RunAsync(Guid testId, CancellationToken ct);
}

// Executes a workflow acceptance test and records the verdict.
//
// The test runs the workflow for real, against the devices it names. That is the
// point — a simulation already proves the graph is sound, and only an execution
// proves the behaviour. The device allow trio still applies, so a test can only
// reach hardware the workflow's environment is permitted to touch.
public sealed class AcceptanceTestRunner : IAcceptanceTestRunner
{
    private readonly AppDbContext _db;
    private readonly WorkflowRunService _runs;
    private readonly ILogger<AcceptanceTestRunner> _logger;

    public AcceptanceTestRunner(AppDbContext db, WorkflowRunService runs, ILogger<AcceptanceTestRunner> logger)
    {
        _db = db;
        _runs = runs;
        _logger = logger;
    }

    public async Task<AcceptanceOutcome> RunAsync(Guid testId, CancellationToken ct)
    {
        var test = await _db.WorkflowAcceptanceTests
            .FirstOrDefaultAsync(t => t.WorkflowAcceptanceTestId == testId && t.IsActive, ct)
            ?? throw new Exceptions.NotFoundException("acceptance test not found");

        var workflow = await _db.Workflows
            .FirstOrDefaultAsync(w => w.WorkflowId == test.WorkflowId && w.IsActive, ct)
            ?? throw new Exceptions.NotFoundException("workflow not found");

        var input = ParseOrNull(test.InputJson);
        var targets = ParseGuids(test.TargetDevicesJson);

        WorkflowRun run;
        try
        {
            run = await _runs.RunAsync(workflow, input, targets, ct, RunTrigger.Test);
        }
        catch (Exception ex)
        {
            // The run never happened — a refused target, an unparseable DAG. That is
            // an `error`, distinct from a `failed` assertion: nothing was measured.
            _logger.LogWarning(ex, "workflow.acceptance.run_failed test={Test}", test.Name);
            await RecordAsync(test, WorkflowAcceptanceTest.StatusError, null, [ex.Message], workflow.SchemaHash, ct);
            return new AcceptanceOutcome(WorkflowAcceptanceTest.StatusError, null, [ex.Message]);
        }

        var steps = await _db.StepRuns.AsNoTracking()
            .Where(s => s.WorkflowRunId == run.WorkflowRunId)
            .ToListAsync(ct);

        var failures = Evaluate(test.AssertionsJson, run, steps);
        var status = failures.Count == 0
            ? WorkflowAcceptanceTest.StatusPassed
            : WorkflowAcceptanceTest.StatusFailed;

        await RecordAsync(test, status, run.WorkflowRunId, failures, workflow.SchemaHash, ct);
        return new AcceptanceOutcome(status, run.WorkflowRunId, failures);
    }

    private static List<string> Evaluate(string assertionsJson, WorkflowRun run, List<StepRun> steps)
    {
        var failures = new List<string>();

        JsonElement assertions;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(assertionsJson) ? "[]" : assertionsJson);
            assertions = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return [$"assertions are not valid JSON: {ex.Message}"];
        }
        if (assertions.ValueKind != JsonValueKind.Array) return ["assertions must be a JSON array"];

        // A test with no assertions passes trivially, which would make an empty test
        // look like evidence. Say so instead.
        if (assertions.GetArrayLength() == 0) return ["the test declares no assertions"];

        var byNode = steps.ToDictionary(s => s.NodeId, StringComparer.Ordinal);

        foreach (var a in assertions.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) { failures.Add("an assertion is not an object"); continue; }

            var kind = Str(a, "kind")?.ToLowerInvariant() ?? string.Empty;
            var path = Str(a, "path") ?? string.Empty;
            var expected = a.TryGetProperty("expected", out var e) ? e : default;

            switch (kind)
            {
                case "status_equals":
                    var want = expected.ValueKind == JsonValueKind.String ? expected.GetString() : null;
                    if (!string.Equals(run.Status, want, StringComparison.OrdinalIgnoreCase))
                        failures.Add($"status_equals: expected '{want}', got '{run.Status}'");
                    break;

                case "step_succeeded":
                    if (!byNode.TryGetValue(path, out var okStep))
                        failures.Add($"step_succeeded: node '{path}' did not run");
                    else if (okStep.Result is not (NodeResult.Changed or NodeResult.NoChange))
                        failures.Add($"step_succeeded: node '{path}' was '{okStep.Result}'");
                    break;

                case "step_failed":
                    if (!byNode.TryGetValue(path, out var badStep))
                        failures.Add($"step_failed: node '{path}' did not run");
                    else if (badStep.Result != NodeResult.Failed)
                        failures.Add($"step_failed: node '{path}' was '{badStep.Result}'");
                    break;

                case "output_equals":
                case "output_contains":
                {
                    var (node, rest) = SplitNode(path);
                    if (!byNode.TryGetValue(node, out var step) || step.OutputJson is null)
                    {
                        failures.Add($"{kind}: node '{node}' produced no output");
                        break;
                    }
                    JsonElement output;
                    try
                    {
                        using var doc = JsonDocument.Parse(step.OutputJson);
                        output = doc.RootElement.Clone();
                    }
                    catch (JsonException) { failures.Add($"{kind}: node '{node}' output is not JSON"); break; }

                    var actual = VariableResolver.TryResolvePath(output, rest);
                    if (actual is null) { failures.Add($"{kind}: '{path}' does not resolve"); break; }

                    var actualText = VariableResolver.Stringify(actual, string.Empty);
                    var expectedText = VariableResolver.Stringify(expected, string.Empty);

                    if (kind == "output_equals")
                    {
                        if (!string.Equals(actualText, expectedText, StringComparison.Ordinal))
                            failures.Add($"output_equals: '{path}' expected '{expectedText}', got '{actualText}'");
                    }
                    else if (!actualText.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"output_contains: '{path}' does not contain '{expectedText}'");
                    }
                    break;
                }

                default:
                    // An unknown kind must fail, not be skipped: skipping would let a
                    // typo turn an assertion into no assertion at all.
                    failures.Add($"unknown assertion kind '{kind}'");
                    break;
            }
        }

        return failures;
    }

    private async Task RecordAsync(
        WorkflowAcceptanceTest test, string status, Guid? runId,
        IReadOnlyList<string> failures, string schemaHash, CancellationToken ct)
    {
        test.LastStatus = status;
        test.LastRunId = runId;
        test.LastRunAt = DateTime.UtcNow;
        test.LastFailuresJson = JsonSerializer.Serialize(failures);
        test.LastSchemaHash = schemaHash;
        test.UpdatedAt = test.LastRunAt.Value;
        await _db.SaveChangesAsync(ct);
    }

    // "node.output.field" -> ("node", ".output.field"). The leading node id is the
    // step; the rest is a VariableResolver path over its output.
    private static (string Node, string Path) SplitNode(string path)
    {
        var dot = path.IndexOf('.');
        return dot < 0 ? (path, string.Empty) : (path[..dot], path[dot..]);
    }

    private static JsonElement? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static List<Guid> ParseGuids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out _))
                .Select(e => Guid.Parse(e.GetString()!))
                .ToList();
        }
        catch (JsonException) { return []; }
    }

    private static string? Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
