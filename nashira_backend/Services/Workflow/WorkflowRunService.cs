using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Exceptions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;
using nashira_backend.Services.DevicePools;
using nashira_backend.Services.Identity;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Workflow;

// Executes a workflow end to end: builds the DAG, runs the deterministic executor against
// the tool-backed node executor, persists the WorkflowRun + StepRun rows, and chains each
// node mutation into the hash-chained audit trail as an audit.v1-shaped event.
public sealed class WorkflowRunService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly INodeExecutor _nodeExecutor;
    private readonly IAuditLogger _audit;
    private readonly WorkflowExecutionScope _scope;
    private readonly Services.Engine.IConditionEvaluator _conditions;
    private readonly Services.Policy.IPolicyEvaluator _policies;
    private readonly IDevicePoolResolver _pools;
    private readonly ILogger<WorkflowRunService> _logger;

    public WorkflowRunService(
        AppDbContext db, ICurrentUser user, INodeExecutor nodeExecutor, IAuditLogger audit,
        WorkflowExecutionScope scope, Services.Engine.IConditionEvaluator conditions,
        Services.Policy.IPolicyEvaluator policies, IDevicePoolResolver pools,
        ILogger<WorkflowRunService> logger)
    {
        _db = db;
        _user = user;
        _nodeExecutor = nodeExecutor;
        _audit = audit;
        _scope = scope;
        _conditions = conditions;
        _policies = policies;
        _pools = pools;
        _logger = logger;
    }

    public async Task<WorkflowRun> RunAsync(
        WorkflowEntity workflow, JsonElement? input, IReadOnlyList<Guid> targetDeviceIds,
        CancellationToken ct, string trigger = RunTrigger.Manual, SubflowInvocation? subflow = null)
        => (await RunDetailedAsync(workflow, input, targetDeviceIds, ct, trigger, subflow)).Run;

    /// <summary>
    /// The same run, with the executor's own result beside the persisted row.
    /// </summary>
    /// <remarks>
    /// A parent's <c>subflow</c> step has to report what its child's nodes did — each
    /// one's output, and the strictest idempotency tier among them, which decides
    /// whether the parent may promise to roll the subflow back (execution/SPEC.md §5).
    /// The tier is not a column on the step row, and re-deriving it from the child's
    /// snippets would score a nested subflow node at the default rather than at its own
    /// child's strictest — exactly the drift <see cref="NodeTierResolver"/> exists to
    /// prevent. So the caller that needs it gets the live result rather than reading the
    /// record back.
    /// </remarks>
    public async Task<(WorkflowRun Run, WorkflowRunResult Result)> RunDetailedAsync(
        WorkflowEntity workflow, JsonElement? input, IReadOnlyList<Guid> targetDeviceIds,
        CancellationToken ct, string trigger = RunTrigger.Manual, SubflowInvocation? subflow = null)
    {
        var startedAt = DateTime.UtcNow;

        // A child runs in the PARENT's environment, not its own row's
        // (execution/SPEC.md §5). Everything downstream of this line — target
        // resolution, the policy context, the device allow-trio, the run row and
        // `{{ run.environment }}` — reads it, so it is resolved once here rather than
        // each of them reaching for `workflow.Environment` again. A production run
        // whose child quietly dropped into that child's draft row would skip exactly the
        // device checks the environments exist to enforce.
        var chain = subflow ?? SubflowInvocation.Root(workflow.WorkflowId, workflow.Environment);
        var environment = chain.Environment;

        IReadOnlyList<Data.Models.Device> targets;
        Dag dag;
        try
        {
            // Resolve targets once, here, rather than per step. Filtering at the point
            // of dispatch would let a workflow silently do less than it was asked to;
            // refusing up front says which device the environment excludes and why.
            targets = await ResolveTargetsAsync(environment, targetDeviceIds, ct);
            using var nodesDoc = JsonDocument.Parse(workflow.NodesJson);
            using var edgesDoc = JsonDocument.Parse(workflow.EdgesJson);
            dag = Dag.Parse(nodesDoc.RootElement, edgesDoc.RootElement);
            // Parsed is not the same as runnable. See Dag.RequireRunnable.
            dag.RequireRunnable();

            // Corporate guardrails, checked before anything executes. Evaluated here
            // rather than per step so a denied run does nothing at all — a policy that
            // stopped a workflow halfway would leave exactly the partial state it
            // exists to prevent.
            await EnforcePoliciesAsync(workflow, environment, targets, dag, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing executed, so no step can explain this. Without a row here the
            // caller got an exception and the runs list showed nothing at all — "I
            // pressed run and nothing happened" was literally true and had no trail.
            await RecordRefusedRunAsync(
                workflow, environment, chain, input, targetDeviceIds, trigger, startedAt, ex, ct);
            throw;
        }

        var ctx = new WorkflowExecutionContext(
            workflow.WorkflowId, workflow.SchemaHash, _user.Username ?? string.Empty, startedAt);

        // Allocated before execution, not after: artifact-producing steps (report,
        // slack_message, email_send) stamp the run they belong to as they execute,
        // and the id is the only part of the run row they need to exist yet.
        var runId = Guid.NewGuid();

        // Publish the run's environment so device-targeting steps can enforce the
        // per-device allow trio — plus the run identity for artifact attribution;
        // cleared as soon as the run finishes.
        WorkflowRunResult result;
        using (_scope.Enter(
            environment, runId, workflow.WorkflowId,
            _user.IsAuthenticated ? _user.UserId : null))
        {
            // The snippet executor accumulates step outputs as it goes; handing the
            // executor a live view of them is what lets a conditional edge branch on
            // what the predecessor actually produced.
            Func<IReadOnlyDictionary<string, Services.Engine.StepResult>>? outputs = null;

            // One run context per run, built here and handed to both the step executor
            // and the edge conditions, so `{{ run.* }}` means the same thing in a step
            // payload and in a condition over the same run.
            var runContext = new Services.Engine.RunContext
            {
                Id = runId,
                WorkflowId = workflow.WorkflowId,
                WorkflowName = workflow.Name,
                Environment = environment,
                Trigger = trigger,
                StartedAt = startedAt,
            };

            if (_nodeExecutor is SnippetNodeExecutor snippets)
            {
                snippets.RunInput = input;
                snippets.Targets = targets;
                snippets.RunContext = runContext;
                // Where this run sits in the chain of subflow calls, so its own
                // `subflow` nodes can refuse a cycle or a ninth level before anything
                // runs.
                snippets.Subflow = chain;
                outputs = () => snippets.Completed;
            }

            // The run input and run context travel with the executor too: without them
            // `{{ input.* }}` and `{{ run.* }}` in an edge condition resolved unresolved
            // and every such branch evaluated false (templates/SPEC.md §9).
            var engine = new WorkflowExecutor(_conditions, outputs, input, runContext);
            result = await engine.RunAsync(dag, _nodeExecutor, ctx, stopOnFailure: true, ct);
        }

        var finishedAt = DateTime.UtcNow;
        var run = new WorkflowRun
        {
            WorkflowRunId = runId,
            WorkflowId = workflow.WorkflowId,
            Environment = environment,
            SchemaHash = workflow.SchemaHash,
            ParentRunId = chain.ParentRunId,
            Status = result.Status,
            FinalState = result.FinalState,
            NodeCount = result.Steps.Count,
            ChangedCount = result.Steps.Count(s => s.Result == NodeResult.Changed),
            FailedCount = result.Steps.Count(s => s.Result == NodeResult.Failed),
            RollbackPlanJson = JsonSerializer.Serialize(result.RollbackPlan),
            InputJson = input?.GetRawText(),
            TargetDevicesJson = JsonSerializer.Serialize(targets.Select(d => d.DeviceId)),
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Trigger = trigger,
            TriggeredByUserId = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = startedAt,
            UpdatedAt = finishedAt,
        };
        _db.WorkflowRuns.Add(run);

        var seq = 0;
        foreach (var step in result.Steps)
        {
            _db.StepRuns.Add(new StepRun
            {
                StepRunId = Guid.NewGuid(),
                WorkflowRunId = runId,
                NodeId = step.NodeId,
                Sequence = seq++,
                Result = step.Result,
                ErrorCode = step.ErrorCode,
                Retryable = step.Retryable,
                OutputJson = step.Output,
                Error = step.Error,
                InputJson = step.Input,
                Logs = step.Logs,
                Attempts = step.Attempts,
                ChildRunId = step.ChildRunId,
                StartedAt = step.StartedAt,
                FinishedAt = step.FinishedAt,
                IsActive = true,
                CreatedAt = startedAt,
                UpdatedAt = startedAt,
            });
        }
        await _db.SaveChangesAsync(ct);

        // The per-node mutations are the executed artifacts — chain them into the audit log.
        foreach (var e in result.AuditEvents)
        {
            await _audit.LogAsync("workflow.node", workflow.WorkflowId, e.Op,
                after: new { node_id = e.NodeId, op = e.Op, idempotency = e.Idempotency, result = e.Result, schema_hash = e.SchemaHash },
                ct: ct);
        }

        return (run, result);
    }

    // A run that was refused before it began. Best-effort: if this cannot be written
    // the original failure is still the one the caller needs, so it is logged and
    // swallowed rather than replacing a policy denial with a database error.
    private async Task RecordRefusedRunAsync(
        WorkflowEntity workflow, string environment, SubflowInvocation chain, JsonElement? input,
        IReadOnlyList<Guid> targetDeviceIds, string trigger, DateTime startedAt, Exception failure,
        CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            _db.WorkflowRuns.Add(new WorkflowRun
            {
                WorkflowRunId = Guid.NewGuid(),
                WorkflowId = workflow.WorkflowId,
                Environment = environment,
                SchemaHash = workflow.SchemaHash,
                ParentRunId = chain.ParentRunId,
                Status = WorkflowRun.StatusFailed,
                // Not `failed`, which in this system means "stopped part-way and left
                // changes behind". Nothing ran, so nothing was left behind.
                FinalState = "refused",
                NodeCount = 0,
                RollbackPlanJson = "[]",
                InputJson = input?.GetRawText(),
                TargetDevicesJson = JsonSerializer.Serialize(targetDeviceIds),
                StartedAt = startedAt,
                FinishedAt = now,
                Trigger = trigger,
                Error = failure.Message,
                TriggeredByUserId = _user.IsAuthenticated ? _user.UserId : null,
                IsActive = true,
                CreatedAt = startedAt,
                UpdatedAt = now,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow.run.refusal_not_recorded workflow={Workflow}", workflow.WorkflowId);
        }
    }

    // Builds the policy context from what this run is actually about to do, and
    // refuses if a guardrail denies it.
    private async Task EnforcePoliciesAsync(
        WorkflowEntity workflow, string environment, IReadOnlyList<Data.Models.Device> targets, Dag dag,
        CancellationToken ct)
    {
        // The snippet types this DAG will dispatch. Resolved from the nodes rather
        // than assumed, so a policy about `ssh` fires on a workflow that reaches SSH
        // through any node, not only one an author remembered to label.
        var snippetIds = dag.Nodes.Values
            .Select(n => Guid.TryParse(n.SnippetId, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();

        var snippetTypes = snippetIds.Count == 0
            ? []
            : await _db.Snippets.AsNoTracking()
                .Where(s => snippetIds.Contains(s.SnippetId))
                .Select(s => s.Type)
                .Distinct()
                .ToListAsync(ct);

        // Pools are matched by name, so a policy reads the way an operator wrote it.
        // Membership resolves through the SAME resolver the rest of the system uses,
        // rules included — the previous shortcut checked only StaticMembersJson, so
        // a `device_pool` policy over a rule-based pool never fired: a silently
        // holed guardrail, which is the one failure mode this evaluator must not
        // have.
        var pools = new List<string>();
        if (targets.Count > 0)
        {
            var targetIds = targets.Select(d => d.DeviceId).ToHashSet();
            var activePools = await _db.DevicePools.AsNoTracking()
                .Where(p => p.IsActive)
                .Select(p => new { p.DevicePoolId, p.Name })
                .ToListAsync(ct);
            foreach (var pool in activePools)
            {
                var members = await _pools.MembersAsync(pool.DevicePoolId, ct);
                if (members.Any(m => targetIds.Contains(m.DeviceId))) pools.Add(pool.Name);
            }
        }

        var context = new Services.Policy.PolicyContext(
            Environment: environment,
            WorkflowDescription: workflow.Description,
            DeviceRoles: targets.Select(d => d.Role).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList(),
            DevicePools: pools,
            SnippetTypes: snippetTypes);

        var decision = await _policies.EvaluateAsync(context, ct);
        if (decision.Denied)
            throw new ForbiddenException(
                $"blocked by policy '{decision.PolicyName}': {decision.Reason}");
    }

    // Loads the requested devices and drops the ones this environment may not
    // reach, refusing rather than silently narrowing.
    //
    // A run that quietly skipped two of five devices would report success having
    // done 60% of the work, and nothing in the record would say so. Refusing names
    // the device and the reason, which is a fixable message.
    private async Task<IReadOnlyList<Data.Models.Device>> ResolveTargetsAsync(
        string environment, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var devices = await _db.Devices.AsNoTracking()
            .Where(d => d.IsActive && ids.Contains(d.DeviceId))
            .ToListAsync(ct);

        // A device that was deleted or deactivated between the request being built and
        // the run starting is a narrowing, and this method's whole contract is that it
        // never narrows silently. It used to end with `ids.Where(byId.ContainsKey)`,
        // which meant a run asked to touch five devices touched three and reported
        // success — the two that vanished left no trace anywhere in the record. Only
        // the API path pre-checks the ids; a trigger, a schedule or a stored target
        // list reaches here without one.
        var found = devices.Select(d => d.DeviceId).ToHashSet();
        var absent = ids.Where(id => !found.Contains(id)).Distinct().ToList();
        if (absent.Count > 0)
            throw new ValidationException(
                $"target device(s) not found or inactive: {string.Join(", ", absent)}. "
                + "The run was refused rather than executed on the remaining targets.");

        var refusals = devices
            .Select(d => DeviceEnvironmentPolicy.Refusal(d, environment))
            .Where(r => r is not null)
            .ToList();
        if (refusals.Count > 0)
            throw new ForbiddenException(string.Join("; ", refusals));

        // Preserve the caller's order: a workflow that drains routers one at a time
        // depends on it, and a set-based reorder would be invisible until it bit.
        var byId = devices.ToDictionary(d => d.DeviceId);
        return ids.Distinct().Select(id => byId[id]).ToList();
    }
}
