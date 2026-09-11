using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Workflow;

/// <summary>
/// The limits execution/SPEC.md §5 puts on subflow nesting, in one place so the
/// bundle's static refusal and the executor's run-time refusal cannot drift apart.
/// </summary>
public static class SubflowLimits
{
    /// <summary>
    /// How deep a chain of subflow runs may go. The root run is depth 0, so a run at
    /// depth 8 may not start another child. It bounds the recursion as much as it
    /// expresses a policy: each level is a nested synchronous run holding a DI scope
    /// and a DbContext open, and an unbounded chain would exhaust both long before it
    /// produced an error message anyone could read.
    /// </summary>
    public const int MaxDepth = 8;
}

/// <summary>
/// Where a run sits in a chain of subflow calls. The root run builds one with
/// <see cref="Root"/>; every child gets one from <see cref="Descend"/>.
/// </summary>
/// <param name="ParentRunId">The run whose subflow node started this one; null at the root.</param>
/// <param name="Depth">0 at the root, +1 per nested run.</param>
/// <param name="Ancestry">
/// The workflow ids of every run in the chain, root first, INCLUDING this run's own.
/// It is what the cycle guard tests a candidate child against: a workflow that is
/// already running somewhere above cannot be started again below without the chain
/// never ending.
/// </param>
/// <param name="Environment">
/// The environment the whole chain executes in. A child runs the current version of
/// its workflow <em>in the parent's environment</em> (execution/SPEC.md §5) — a
/// production run must not quietly drop into a child's draft row and skip the device
/// checks production implies.
/// </param>
public sealed record SubflowInvocation(
    Guid? ParentRunId, int Depth, IReadOnlyList<Guid> Ancestry, string Environment)
{
    public static SubflowInvocation Root(Guid workflowId, string environment) =>
        new(null, 0, [workflowId], environment);

    /// <summary>This chain extended by a child run of <paramref name="childWorkflowId"/>.</summary>
    public SubflowInvocation Descend(Guid? parentRunId, Guid childWorkflowId) =>
        new(parentRunId, Depth + 1, [.. Ancestry, childWorkflowId], Environment);

    /// <summary>Whether a child run would breach <see cref="SubflowLimits.MaxDepth"/>.</summary>
    public bool WouldExceedDepth => Depth + 1 > SubflowLimits.MaxDepth;
}

/// <summary>
/// What a child run did, as the parent's <c>subflow</c> step needs to report it.
/// </summary>
/// <param name="RunId">
/// The child run's id, null only when the child was refused before a row could be
/// attributed to it — a workflow that does not exist, or a run the policy engine
/// denied outright.
/// </param>
/// <param name="Unresolved">
/// True only when the child workflow id names nothing on this instance — the case
/// execution/SPEC.md §5 codes <c>subflow_missing</c>. A child that existed and was then
/// refused (a policy denial, a device the environment excludes) is a different failure
/// and must not borrow that code: an operator reading <c>subflow_missing</c> goes
/// looking for a broken reference, and the reference was fine.
/// </param>
public sealed record SubflowOutcome(
    Guid? RunId, string Status, string FinalState, string? Error, IReadOnlyList<NodeExecution> Steps,
    bool Unresolved = false)
{
    /// <summary>The child workflow id does not name a live workflow on this instance.</summary>
    public static SubflowOutcome NotFound(string error) =>
        new(null, WorkflowRun.StatusFailed, "refused", error, [], Unresolved: true);

    /// <summary>The child exists but never began: nothing ran, so nothing was left behind.</summary>
    public static SubflowOutcome Refused(string error) =>
        new(null, WorkflowRun.StatusFailed, "refused", error, []);

    public bool Failed => Status == WorkflowRun.StatusFailed;

    /// <summary>Whether any child step changed something (execution/SPEC.md §5).</summary>
    public bool AnyChanged => Steps.Any(s => s.Result == NodeResult.Changed);

    /// <summary>
    /// The strictest tier among the child's EXECUTED nodes, which becomes the parent
    /// step's tier and therefore decides whether the parent can promise to roll the
    /// subflow back. Skipped nodes are excluded: nothing ran, so nothing about them
    /// constrains the parent. A child in which nothing ran at all is idempotent by
    /// the same argument — it left no side effect to compensate.
    /// </summary>
    public IdempotencyKind StrictestTier
    {
        get
        {
            var executed = Steps.Where(s => s.Result != NodeResult.Skipped).ToList();
            return executed.Count == 0 ? IdempotencyKind.Idempotent : executed.Max(s => s.Tier);
        }
    }
}

/// <summary>
/// Runs the child workflow a <c>subflow</c> node names (execution/SPEC.md §5).
/// </summary>
/// <remarks>
/// An interface rather than a direct call into <see cref="WorkflowRunService"/> because
/// the child needs its OWN scope: a fresh <c>DbContext</c>, a fresh
/// <see cref="SnippetNodeExecutor"/> with its own completed-step map, and a fresh
/// <see cref="Services.Engine.RunContext"/>. Reusing the parent's would let the child's
/// steps overwrite the outputs the parent's downstream templates address, which is a
/// data corruption that looks exactly like a template typo.
/// </remarks>
public interface ISubflowRunner
{
    Task<SubflowOutcome> RunAsync(
        Guid childWorkflowId, JsonElement input, IReadOnlyList<Guid> targetDeviceIds,
        SubflowInvocation childChain, CancellationToken ct);
}

/// <inheritdoc cref="ISubflowRunner"/>
public sealed class SubflowRunner : ISubflowRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SubflowRunner> _logger;

    public SubflowRunner(IServiceScopeFactory scopes, ILogger<SubflowRunner> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task<SubflowOutcome> RunAsync(
        Guid childWorkflowId, JsonElement input, IReadOnlyList<Guid> targetDeviceIds,
        SubflowInvocation childChain, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        // No version pinning: the CURRENT row is what runs (execution/SPEC.md §5).
        // That is the oracle's behaviour and it is documented rather than fixed —
        // a parent that pinned a version would keep calling a child its author had
        // already corrected.
        var child = await db.Workflows.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkflowId == childWorkflowId && w.IsActive, ct);
        if (child is null)
            return SubflowOutcome.NotFound(
                $"subflow workflow {childWorkflowId} does not exist on this instance. "
                + "Nothing ran for this node.");

        var runs = sp.GetRequiredService<WorkflowRunService>();
        try
        {
            var (run, result) = await runs.RunDetailedAsync(
                child, input, targetDeviceIds, ct, RunTrigger.Subflow, childChain);
            return new SubflowOutcome(
                run.WorkflowRunId, run.Status, run.FinalState, FirstError(result, run), result.Steps);
        }
        catch (OperationCanceledException)
        {
            // Cancelling the parent cancels the child (§5). The parent's own walk is
            // about to observe the same token, so let it through rather than turning
            // a cancellation into a step failure the operator did not cause.
            throw;
        }
        catch (Exception ex)
        {
            // The child was REFUSED before it began — a policy denial, a target the
            // environment excludes, a graph that will not parse. RunAsync has already
            // written a refused run row for it, so the trail exists; what the parent
            // needs here is the reason, as its own step's failure.
            _logger.LogWarning(
                ex, "workflow.subflow.refused child={Child} depth={Depth}",
                childWorkflowId, childChain.Depth);
            return SubflowOutcome.Refused(ex.Message);
        }
    }

    // The child's error, as the parent's step reports it. A step's own message is the
    // useful one; the run-level Error only exists when no step can explain the failure.
    private static string? FirstError(WorkflowRunResult result, WorkflowRun run)
    {
        if (result.Status != WorkflowRun.StatusFailed) return null;
        var step = result.Steps.FirstOrDefault(
            s => s.Result == NodeResult.Failed && !string.IsNullOrWhiteSpace(s.Error));
        if (step is not null) return $"child step '{step.NodeId}': {step.Error}";
        var coded = result.Steps.FirstOrDefault(s => s.Result == NodeResult.Failed);
        return coded is not null
            ? $"child step '{coded.NodeId}' failed ({coded.ErrorCode ?? "step_failed"})"
            : run.Error ?? "the child run failed";
    }
}
