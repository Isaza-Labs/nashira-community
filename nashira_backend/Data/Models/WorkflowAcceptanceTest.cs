namespace nashira_backend.Data.Models;

// A golden-path test bound to a workflow: a synthetic input plus assertions over
// the run it produces.
//
// The promotion gate already refuses an unsimulated definition, but a simulation
// only proves the graph is well-formed — it never touches a device. An acceptance
// test proves the workflow does what it claims against real QA hardware, which is
// the evidence a production promotion actually wants.
public class WorkflowAcceptanceTest : BaseModel
{
    public const string StatusPassed = "passed";
    public const string StatusFailed = "failed";
    public const string StatusError = "error";

    public Guid WorkflowAcceptanceTestId { get; set; }
    public Guid WorkflowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // The run's `input` payload. JSON object.
    public string InputJson { get; set; } = "{}";

    // Devices the test run targets. JSON array of ids.
    public string TargetDevicesJson { get; set; } = "[]";

    // JSON array of { kind, path, op, expected }. Kinds the runner understands:
    //   status_equals   — the run's status (completed | failed)
    //   step_succeeded  — `path` is a node id
    //   step_failed     — `path` is a node id
    //   output_equals   — `path` addresses a step output, compared to `expected`
    //   output_contains — same, substring match
    public string AssertionsJson { get; set; } = "[]";

    // Denormalized last outcome, for the list view and the promotion gate.
    //
    // Reset to null whenever the workflow is structurally edited, so the gate can
    // never be satisfied by evidence gathered against a different definition. That
    // reset is the entire reason this is cached rather than derived: derived, it
    // would keep reporting a pass for a workflow that no longer exists.
    public string? LastStatus { get; set; }
    public Guid? LastRunId { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastFailuresJson { get; set; }

    // The schema hash the last result was obtained against. Belt and braces with
    // the reset above: if a reset is ever missed, a mismatch here still tells the
    // gate the evidence is stale.
    public string? LastSchemaHash { get; set; }
}
