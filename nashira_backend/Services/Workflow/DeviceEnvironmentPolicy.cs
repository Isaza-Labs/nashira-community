using DeviceEntity = nashira_backend.Data.Models.Device;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Workflow;

// Scoped ambient: the environment of the workflow whose nodes are being dispatched
// right now. WorkflowRunService sets it for the duration of a run; it stays null on
// every other path (agent chat, direct API calls), and a null environment means "not
// a workflow run" — device targeting is unrestricted there, exactly as before.
//
// Scoped rather than passed as an argument because the device is resolved deep inside
// the tool handlers (device_connect / device_ping), several layers below the executor,
// and IToolHandler.ExecuteAsync is a fixed (args, ct) contract shared by ~50 handlers.
public sealed class WorkflowExecutionScope
{
    public string? Environment { get; private set; }

    // The run being executed, when there is one. The run id is allocated BEFORE
    // execution (WorkflowRunService) precisely so artifact-producing steps — a
    // report row, a messaging delivery — can stamp the run they came from; the
    // run row itself is persisted after the walk, and nothing enforces a foreign
    // key in between.
    public Guid? WorkflowRunId { get; private set; }
    public Guid? WorkflowId { get; private set; }

    // The user who triggered the run, for artifact attribution. Null on
    // scheduled/system runs.
    public Guid? TriggeredBy { get; private set; }

    // Marks the scope as executing `environment` until the returned handle is
    // disposed. Restores the previous values so nesting can't leak.
    public IDisposable Enter(
        string environment, Guid? workflowRunId = null, Guid? workflowId = null, Guid? triggeredBy = null)
    {
        var restore = new Restore(this, Environment, WorkflowRunId, WorkflowId, TriggeredBy);
        Environment = environment;
        WorkflowRunId = workflowRunId;
        WorkflowId = workflowId;
        TriggeredBy = triggeredBy;
        return restore;
    }

    private sealed class Restore(
        WorkflowExecutionScope scope, string? environment, Guid? runId, Guid? workflowId, Guid? triggeredBy)
        : IDisposable
    {
        public void Dispose()
        {
            scope.Environment = environment;
            scope.WorkflowRunId = runId;
            scope.WorkflowId = workflowId;
            scope.TriggeredBy = triggeredBy;
        }
    }
}

// Decides whether a workflow run in a given environment may dispatch to a device.
//
// The three Allow flags are independent, so any combination is valid — including all
// three (reachable from anywhere) and none. A device that allows nothing is "parked":
// no run can target it, and that is reported as an explicit refusal rather than a
// silent skip, because silently doing nothing looks identical to a successful run.
public static class DeviceEnvironmentPolicy
{
    public static bool Allows(DeviceEntity device, string environment) => environment switch
    {
        WorkflowEntity.EnvDraft => device.AllowDraft,
        WorkflowEntity.EnvQa => device.AllowQa,
        WorkflowEntity.EnvProduction => device.AllowProduction,
        // An unknown environment is not a licence to run: fail closed.
        _ => false,
    };

    // Null when the dispatch is permitted, otherwise the reason to surface to the
    // caller. `environment` null = not inside a workflow run, so nothing to enforce.
    public static string? Refusal(DeviceEntity device, string? environment)
    {
        if (string.IsNullOrEmpty(environment)) return null;
        if (Allows(device, environment)) return null;

        var allowed = AllowedList(device);
        return allowed.Length == 0
            ? $"device '{device.DeviceName}' is not reachable from any environment " +
              "(allow_draft/allow_qa/allow_production are all off)"
            : $"device '{device.DeviceName}' does not allow the '{environment}' environment " +
              $"(allowed: {allowed})";
    }

    private static string AllowedList(DeviceEntity d)
    {
        var names = new List<string>(3);
        if (d.AllowDraft) names.Add(WorkflowEntity.EnvDraft);
        if (d.AllowQa) names.Add(WorkflowEntity.EnvQa);
        if (d.AllowProduction) names.Add(WorkflowEntity.EnvProduction);
        return string.Join(", ", names);
    }
}
