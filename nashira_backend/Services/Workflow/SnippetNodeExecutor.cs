using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Worker;

namespace nashira_backend.Services.Workflow;

// Resolves a snippet type to its handler. Singleton over the registered types so a
// lookup does not construct every handler just to read its Type.
public sealed class SnippetHandlerRegistry : ISnippetHandlerRegistry
{
    private readonly Dictionary<string, Type> _byType;

    public SnippetHandlerRegistry(IEnumerable<Type> handlerTypes, IServiceProvider root)
    {
        _byType = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        using var scope = root.CreateScope();
        foreach (var t in handlerTypes)
        {
            var instance = (ISnippetHandler)scope.ServiceProvider.GetRequiredService(t);
            _byType[instance.Type] = t;
        }
    }

    public IReadOnlyCollection<string> KnownTypes => _byType.Keys;

    public ISnippetHandler? Resolve(string type, IServiceProvider scope) =>
        _byType.TryGetValue(type, out var t) ? (ISnippetHandler)scope.GetRequiredService(t) : null;
}

// Executes a workflow node by running the snippet its `snippet_id` names.
//
// This replaces ToolNodeExecutor, which bound nodes to agent tools through a
// `config_overrides.tool` key that is not part of workflow.v1. A workflow authored
// against the schema — here or on another implementation — now runs as written.
//
// Per node it: resolves the snippet row, resolves `{{ … }}` references in the
// payload against the outputs of the steps already completed, dispatches to the
// handler, and records the output so downstream nodes can address it.
public sealed class SnippetNodeExecutor : INodeExecutor
{
    private static readonly JsonElement EmptyInput = JsonDocument.Parse("{}").RootElement.Clone();

    // Enough to read a step's configuration back, bounded so a node that carries a
    // file body does not store it twice per run.
    private const int MaxInputChars = 4_000;

    // The third literal workflow.v1 allows in `snippet_id`, alongside __start__ and
    // __end__. Unlike those two it denotes real work: running another workflow
    // (execution/SPEC.md §5).
    private const string SubflowLiteral = "subflow";

    // The key that names the child. Everything else under `config_overrides` is the
    // child's input.
    private const string SubflowWorkflowIdKey = "subflow_workflow_id";

    private readonly AppDbContext _db;
    private readonly ISnippetHandlerRegistry _registry;
    private readonly IServiceProvider _sp;
    private readonly IVariableResolver _resolver;
    private readonly WorkflowExecutionScope _scope;
    private readonly ISubflowRunner? _subflows;
    private readonly ILogger<SnippetNodeExecutor> _logger;

    // Outputs of the nodes completed so far in this run, keyed by node id. Scoped
    // to the run because the executor instance is.
    private readonly Dictionary<string, StepResult> _completed = new(StringComparer.Ordinal);

    public SnippetNodeExecutor(
        AppDbContext db, ISnippetHandlerRegistry registry, IServiceProvider sp,
        IVariableResolver resolver, WorkflowExecutionScope scope, ILogger<SnippetNodeExecutor> logger,
        ISubflowRunner? subflows = null)
    {
        _db = db;
        _registry = registry;
        _sp = sp;
        _resolver = resolver;
        _scope = scope;
        _logger = logger;
        _subflows = subflows;
    }

    // The run's trigger payload, for `{{ input.* }}`. Set by WorkflowRunService.
    public JsonElement? RunInput { get; set; }

    // The run's metadata, for `{{ run.* }}`. Set by WorkflowRunService; the
    // failed_step_* fields are filled in here as steps fail.
    public RunContext? RunContext { get; set; }

    // Devices this run targets, already filtered against the run's environment.
    // A `per_device` snippet fans out over them; `once` snippets ignore them.
    public IReadOnlyList<Data.Models.Device> Targets { get; set; } = [];

    // Where this run sits in the chain of subflow calls (execution/SPEC.md §5). Set by
    // WorkflowRunService; a run started on its own account gets a root chain. It is what
    // the cycle and depth guards below read, so a run that arrives without one is
    // treated as a root — the conservative reading, since a root cannot be part of a
    // loop it does not know about.
    public SubflowInvocation? Subflow { get; set; }

    // Read by the executor to evaluate conditional edges against what ran.
    public IReadOnlyDictionary<string, StepResult> Completed => _completed;

    public async Task<NodeOutcome> ExecuteAsync(WorkflowNode node, CancellationToken ct)
    {
        if (node.SnippetId is "__start__" or "__end__")
            return new NodeOutcome(NodeResult.NoChange);

        // A node that runs another workflow (execution/SPEC.md §5). The schema's node
        // `type` enum settles the word, so either marker alone identifies the node —
        // keying on the sentinel only would let `type: "subflow"` with some other
        // snippet_id fall through to the snippet lookup and fail as unbound.
        if (node.SnippetId == SubflowLiteral || node.NodeType == SubflowLiteral)
            return await ExecuteSubflowAsync(node, ct);

        if (!Guid.TryParse(node.SnippetId, out var snippetId))
        {
            // A node whose snippet reference is not a real snippet did not run, and
            // saying `no_change` claims it did and found nothing to do. That is the
            // one outcome this must never be: the run goes green, the summary reads
            // "0 changed, 0 failed", and a workflow that pings nothing is
            // indistinguishable from one that pinged and found everything already
            // correct. An author reads that as success.
            //
            // This used to no-op so an unimplemented node type (subflow) would not
            // fail the whole run. The cost was paid by every typo and every invented
            // snippet id — an agent writing `__ping__` produced a passing run — and a
            // build that cannot execute a node should say so rather than pretend the
            // node was a no-op. It also aligns with the branch below: a UUID that
            // resolves to nothing already fails with `unknown_snippet`, and there is
            // no reason a reference that is not even a UUID should fare better.
            return Failed(node, "unbound_node",
                $"node '{node.Id}' references snippet '{node.SnippetId}', which is not a snippet id. "
                + "Nothing was executed for this node.");
        }

        var snippet = await _db.Snippets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SnippetId == snippetId && s.IsActive, ct);
        if (snippet is null)
            return Failed(node, "unknown_snippet", $"node '{node.Id}' references a snippet that does not exist");

        var handler = _registry.Resolve(snippet.Type, _sp);
        if (handler is null)
            return Failed(node, "unknown_handler",
                $"no handler for snippet type '{snippet.Type}' (known: {string.Join(", ", _registry.KnownTypes)})");

        // One tier per step, decided here and carried everywhere it is needed: the
        // handler's floor, the snippet's declaration, and a STRICTER-ONLY
        // `config_overrides.idempotency` override (execution/SPEC.md §2).
        var tier = Idempotency.Effective(snippet, handler.DefaultIdempotency, node.ConfigOverrides);
        if (Idempotency.DeclaredOverride(node.ConfigOverrides) is { } asked && asked < tier)
            _logger.LogWarning(
                "workflow.node.idempotency_override_ignored node={Node} asked={Asked} effective={Effective}",
                node.Id, Idempotency.ToWire(asked), Idempotency.ToWire(tier));

        var rawInput = node.ConfigOverrides.ValueKind == JsonValueKind.Object ? node.ConfigOverrides : EmptyInput;
        var perDevice = string.Equals(
            snippet.TargetMode, Data.Models.Snippet.TargetPerDevice, StringComparison.OrdinalIgnoreCase);

        if (!perDevice)
        {
            // No single unambiguous device, so `{{ device.x }}` stays literal and is
            // logged rather than bound to an arbitrary target.
            var input = _resolver.Resolve(rawInput, _completed, deviceContext: null, runInput: RunInput,
                runContext: RunContext?.ToJson());

            // templates/SPEC.md §8: a residual `{{ … }}` fails the step before the
            // handler runs. In a `once` step `{{ device.* }}` is a residual like any
            // other — there is no device to bind it to.
            var residual = VariableResolver.FindUnresolvedTemplates(input);
            if (residual.Count > 0)
                return Failed(node, "unresolved_template", VariableResolver.DescribeUnresolved(residual));

            var single = await RunOnceAsync(node, snippet, handler, tier, input, ct);
            return Record(node, snippet, tier, single, input);
        }

        // A per-device step with no targets is a configuration mistake, not an
        // empty success: the run was asked to do something to devices and there are
        // none. Succeeding here would report work that never happened.
        if (Targets.Count == 0)
            return Failed(node, "no_targets",
                $"node '{node.Id}' is per_device but the run has no target devices");

        var results = new List<DeviceStep>(Targets.Count);
        var failures = 0;
        var anyChanged = false;
        var anyUnspecified = false;
        var maxAttempts = 1;
        var logLines = new List<string>();
        var fanOutStarted = DateTime.UtcNow;

        foreach (var device in Targets)
        {
            ct.ThrowIfCancellationRequested();

            var deviceView = VariableResolver.DeviceView(device);
            // A per-device consumer reads each per-device producer's output for THIS
            // device, not the fan-out envelope (templates/SPEC.md §6): scope the
            // completed outputs before resolving.
            var scoped = VariableResolver.ScopeOutputsToDevice(_completed, device.DeviceId);
            var input = _resolver.Resolve(rawInput, scoped, deviceView, RunInput, RunContext?.ToJson());

            // §8 per device: a reference may resolve for one target and not for
            // another (a producer with no entry for this device), so the scan runs
            // per device and only that device's slice fails.
            var deviceResidual = VariableResolver.FindUnresolvedTemplates(input);
            if (deviceResidual.Count > 0)
            {
                var message = VariableResolver.DescribeUnresolved(deviceResidual);
                _logger.LogWarning(
                    "workflow.node.unresolved_template node={Node} device={Device} count={Count}",
                    node.Id, device.DeviceName, deviceResidual.Count);
                failures++;
                results.Add(new DeviceStep(
                    device.DeviceId, device.DeviceName, false, message, "unresolved_template", 0, 0,
                    JsonSerializer.SerializeToElement(new { error = message })));
                continue;
            }

            var attempted = await RunOnceAsync(node, snippet, handler, tier, input, ct);
            var result = attempted.Result;

            if (!result.Success) failures++;
            if (ResolveChange(result.Change, snippet, node) is not { } deviceChanged)
                return Failed(node, "change_undeclared", UndeclaredMessage(snippet, node));
            if (deviceChanged) anyChanged = true;
            if (attempted.Attempts > maxAttempts) maxAttempts = attempted.Attempts;
            if (!string.IsNullOrWhiteSpace(result.Logs))
                logLines.Add($"{device.DeviceName}: {result.Logs!.Trim()}");

            results.Add(new DeviceStep(
                device.DeviceId,
                device.DeviceName,
                result.Success,
                string.IsNullOrEmpty(result.Error) ? null : result.Error,
                result.ErrorCode,
                // Per device, because a fan-out where one device needed three tries and
                // the rest went first time is a different situation from all of them
                // struggling, and the node-level count cannot tell them apart.
                attempted.Attempts,
                (int)(attempted.FinishedAt - attempted.StartedAt).TotalMilliseconds,
                result.Output));
        }

        var fanOutFinished = DateTime.UtcNow;
        // `{{ device.* }}` is left literal on purpose: it resolves to something
        // different for every target, so a single stored snapshot cannot honestly show
        // one value. Everything else — `{{ input.* }}`, `{{ steps.*.output }}` — is
        // resolved, which is the part a reader cannot reconstruct afterwards.
        var inputSnapshot = _resolver.Resolve(rawInput, _completed, deviceContext: null, runInput: RunInput,
            runContext: RunContext?.ToJson());

        // Aggregate into one node outcome so the DAG walker stays a plain
        // topological walk. Downstream templates address
        // `{{ steps.<node>.output.devices[i].output.… }}`.
        var aggregate = JsonSerializer.SerializeToElement(new
        {
            per_device = true,
            total = Targets.Count,
            failed = failures,
            devices = results.Select(r => new
            {
                device_id = r.DeviceId,
                device = r.Device,
                success = r.Success,
                error = r.Error,
                error_code = r.ErrorCode,
                attempts = r.Attempts,
                duration_ms = r.DurationMs,
                output = r.Output,
            }),
        });
        _completed[node.Id] = new StepResult(aggregate);

        if (failures > 0)
        {
            // Name the devices that failed. "3 of 12 failed" sends the reader into the
            // payload to find out which; the step's own error line should say already.
            var failed = results.Where(r => !r.Success).Select(r => r.Device).ToList();
            var error = $"{failures} of {Targets.Count} device(s) failed: {string.Join(", ", failed)}";
            RunContext?.MarkFailed(node.Id, error);

            return new NodeOutcome(
                NodeResult.Failed, "device_failures", false, aggregate.GetRawText())
            {
                Tier = tier,
                Error = error,
                Logs = logLines.Count > 0 ? string.Join(Environment.NewLine, logLines) : null,
                Input = InputSnapshot(inputSnapshot),
                Attempts = maxAttempts,
                StartedAt = fanOutStarted,
                FinishedAt = fanOutFinished,
            };
        }

        // A fan-out changed something if ANY device did. One device that mutated is enough
        // to put the node in the rollback plan.
        return new NodeOutcome(
            anyChanged ? NodeResult.Changed : NodeResult.NoChange, Output: aggregate.GetRawText())
        {
            Tier = tier,
            Logs = logLines.Count > 0 ? string.Join(Environment.NewLine, logLines) : null,
            Input = InputSnapshot(inputSnapshot),
            Attempts = maxAttempts,
            StartedAt = fanOutStarted,
            FinishedAt = fanOutFinished,
        };
    }

    // One device's slice of a per-device fan-out. A named type rather than an
    // anonymous one so the failure summary can query it without reflection.
    private sealed record DeviceStep(
        Guid DeviceId, string Device, bool Success, string? Error, string? ErrorCode,
        int Attempts, int DurationMs, JsonElement Output);

    // The result of running a step to completion, with what it took to get there.
    private sealed record Attempted(
        SnippetResult Result, int Attempts, DateTime StartedAt, DateTime FinishedAt);

    // Runs the step, re-attempting while the snippet's retry policy allows it.
    // Both gates live in RetryPolicy.ShouldRetry: the failure must be retryable and
    // the step must be idempotent.
    private async Task<Attempted> RunOnceAsync(
        WorkflowNode node, Data.Models.Snippet snippet, ISnippetHandler handler,
        IdempotencyKind tier, JsonElement input, CancellationToken ct)
    {
        var policy = Services.Engine.RetryPolicy.Parse(snippet.RetryPolicyJson);
        var startedAt = DateTime.UtcNow;

        SnippetResult result;
        var attempt = 0;
        while (true)
        {
            attempt++;
            var delay = policy.DelayBefore(attempt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);

            result = await AttemptAsync(node, snippet, handler, input, ct);
            if (result.Success) break;

            if (!policy.ShouldRetry(attempt, result.Retryable, tier)) break;

            _logger.LogInformation(
                "workflow.node.retry node={Node} attempt={Attempt}/{Max} code={Code}",
                node.Id, attempt, policy.MaxAttempts, result.ErrorCode);
        }

        // Attempts used to be spliced into the output payload, which put a diagnostic
        // in the middle of the data a downstream node addresses — and only when the
        // count was interesting, so `output.attempts` existed on some steps and not
        // others. It has its own column on the step row now, always present.
        return new Attempted(result, attempt, startedAt, DateTime.UtcNow);
    }

    private async Task<SnippetResult> AttemptAsync(
        WorkflowNode node, Data.Models.Snippet snippet, ISnippetHandler handler,
        JsonElement input, CancellationToken ct)
    {
        // The one place the canonical-vs-alias question is answered
        // (snippets/SPEC.md rule 1, bundle/SPEC.md §4). Handlers still read both keys —
        // they are called directly by tests and by nothing else — but a node that
        // carries both now reaches the handler with the CANONICAL one, instead of
        // whichever key that particular handler happened to check first.
        //
        // It runs here rather than beside the resolver so the step row keeps the
        // payload as the author wrote it: the run history should reproduce the node,
        // not this rewrite of it.
        var request = new SnippetRequest
        {
            NodeId = node.Id,
            WorkflowId = _scope.WorkflowId ?? Guid.Empty,
            SnippetId = snippet.SnippetId,
            SnippetType = snippet.Type,
            Input = SnippetInputNormalizer.Normalize(snippet.Type, input),
            Code = snippet.Code,
            ScriptLanguage = snippet.ScriptLanguage,
            TimeoutSeconds = snippet.TimeoutSeconds <= 0 ? 60 : snippet.TimeoutSeconds,
            NetworkEnabled = snippet.NetworkEnabled,
            Environment = _scope.Environment,
            WorkflowRunId = _scope.WorkflowRunId,
            TriggeredBy = _scope.TriggeredBy,
        };

        try
        {
            return await handler.ExecuteAsync(request, ct);
        }
        catch (Exception ex)
        {
            // A handler that throws is a bug in the handler, not a workflow failure
            // the author can act on — but the run still has to end, so it becomes a
            // failed step with the message attached.
            _logger.LogError(ex, "workflow.node.handler_threw node={Node} type={Type}", node.Id, snippet.Type);
            return SnippetResult.Fail(ex.Message, "exception");
        }
    }

    private NodeOutcome Record(
        WorkflowNode node, Data.Models.Snippet snippet, IdempotencyKind tier,
        Attempted attempted, JsonElement input)
    {
        var result = attempted.Result;

        // Record the output either way: a failure branch reads the failed step's
        // output to decide what to do about it.
        _completed[node.Id] = new StepResult(result.Output);

        // Everything a reader needs that the result word does not carry. Attached to
        // both outcomes, not only failures: a step that succeeded and changed nothing
        // is the case where "what was it actually handed, and what did it say" is the
        // whole question.
        var diagnostics = new
        {
            result.Logs,
            Input = InputSnapshot(input),
            attempted.Attempts,
            attempted.StartedAt,
            attempted.FinishedAt,
        };

        if (!result.Success)
        {
            RunContext?.MarkFailed(node.Id, result.Error);
            return new NodeOutcome(
                NodeResult.Failed, result.ErrorCode ?? "step_failed", result.Retryable, result.Output.GetRawText())
            {
                Tier = tier,
                Error = result.Error,
                Logs = diagnostics.Logs,
                Input = diagnostics.Input,
                Attempts = diagnostics.Attempts,
                StartedAt = diagnostics.StartedAt,
                FinishedAt = diagnostics.FinishedAt,
            };
        }

        // Whether the step changed anything drives both the audit event and the rollback
        // plan. The handler answers, or names the author as the one who can — there is no
        // third path, and the tier is not consulted.
        if (ResolveChange(result.Change, snippet, node) is not { } changed)
            return Failed(node, "change_undeclared", UndeclaredMessage(snippet, node));

        return new NodeOutcome(
            changed ? NodeResult.Changed : NodeResult.NoChange, Output: result.Output.GetRawText())
        {
            Tier = tier,
            Logs = diagnostics.Logs,
            Input = diagnostics.Input,
            Attempts = diagnostics.Attempts,
            StartedAt = diagnostics.StartedAt,
            FinishedAt = diagnostics.FinishedAt,
        };
    }

    // The resolved input, as stored on the step row.
    //
    // Redacted through the same helper the agent's tool telemetry uses, so a value an
    // author typed under `password` does not become readable to anyone with Viewer on
    // the runs screen — the run record is a diagnostic, not a place secrets get a
    // second home. Capped for the same reason a step that writes a large file should
    // not put that file in the run history twice.
    private static string? InputSnapshot(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        return Ai.Conversation.ToolTelemetry.Redact(input, MaxInputChars)?.ToJsonString();
    }

    // The per-device verdict, mirroring the single-device rule in Record(): an
    // explicit Changed=false from a handler is respected, and the idempotency-tier
    // fallback only applies to devices whose handler said nothing. Before this, a
    // per-device step where every device explicitly reported "no change" (a
    // read-only integration_action, say) was still marked Changed by the tier —
    // polluting the audit trail and the rollback plan with mutations that never
    // happened.
    /// <summary>
    /// The step's change signal, resolved: the handler's answer, or the author's where the
    /// handler said it could not know. Null means nobody declared, which is a defect.
    /// </summary>
    /// <remarks>
    /// This replaces `AggregateChanged`, which read
    /// <c>anyChanged || (anyUnspecified &amp;&amp; tier != Idempotent)</c> — silence meant
    /// "changed" unless the handler was idempotent. Most handlers are not, so most steps were
    /// recorded as having mutated something because nobody had said otherwise, and the audit
    /// trail, the rollback plan and the run's final state were all computed on top of that.
    ///
    /// A tier says whether an action COULD be undone. This says whether anything WAS done.
    /// Answering the second with the first is what made the signal describe the handler's
    /// category instead of the run.
    ///
    /// The author declares in the place the ACTION lives, which differs by type: on the NODE
    /// for the types whose action the node carries (ssh commands, an mcp tool), on the SNIPPET
    /// for the types whose code it carries (a python script, a playbook). The node wins where
    /// both are present, being the more specific.
    /// </remarks>
    internal static bool? ResolveChange(StepChange reported, Data.Models.Snippet snippet, WorkflowNode node)
    {
        if (reported == StepChange.Changed) return true;
        if (reported == StepChange.Unchanged) return false;

        if (DeclaredChange(node.ConfigOverrides) is { } onNode) return onNode;
        return snippet.ChangesState;
    }

    // `config_overrides.changes` on the node, or null when it says nothing. Deliberately
    // strict: only a real boolean counts, so a typo is "nobody declared" and fails loudly
    // rather than being coerced into an answer.
    internal static bool? DeclaredChange(JsonElement configOverrides) =>
        configOverrides.ValueKind == JsonValueKind.Object
        && configOverrides.TryGetProperty("changes", out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static string UndeclaredMessage(Data.Models.Snippet snippet, WorkflowNode node) =>
        $"snippet '{snippet.Name}' is a '{snippet.Type}', whose handler cannot tell whether a step "
        + "changed anything — the author has to say. Set `changes` to true or false in node "
        + $"'{node.Id}'s config_overrides, or set it on the snippet itself.";

    // ─── subflow (execution/SPEC.md §5) ───────────────────────────────────────

    // Runs the workflow this node names, as a real run of its own: its own
    // WorkflowRun row with ParentRunId set and `trigger: "subflow"`, its own steps, its
    // own audit events. The parent's step records the child's id and reports what it
    // did.
    private async Task<NodeOutcome> ExecuteSubflowAsync(WorkflowNode node, CancellationToken ct)
    {
        // A run that reached here without a chain is a root: it has no ancestors, so
        // nothing above it can be re-entered and depth starts at zero.
        var chain = Subflow ?? SubflowInvocation.Root(
            _scope.WorkflowId ?? Guid.Empty, _scope.Environment ?? string.Empty);

        // The child is named by `config_overrides.subflow_workflow_id` and by nothing
        // else. A node without it is not "a subflow with no input" — there is no
        // workflow to run — so it fails rather than being skipped: a skipped node stops
        // every `success` edge out of it, and the whole downstream half of the graph
        // would silently never run while the run reported completed.
        if (node.ConfigOverrides.ValueKind != JsonValueKind.Object
            || !node.ConfigOverrides.TryGetProperty(SubflowWorkflowIdKey, out var rawId)
            || rawId.ValueKind != JsonValueKind.String
            || !Guid.TryParse(rawId.GetString(), out var childId)
            || childId == Guid.Empty)
            return Failed(node, "subflow_missing",
                $"node '{node.Id}' is a subflow but config_overrides.{SubflowWorkflowIdKey} "
                + "is missing or is not a GUID. Nothing ran for this node.");

        // Both guards run BEFORE the child is created, and before its input is even
        // resolved: a run that can never finish should cost one query, not eight nested
        // runs' worth of side effects on the way to finding out.
        if (chain.WouldExceedDepth)
            return Failed(node, "subflow_depth_exceeded",
                $"node '{node.Id}' would start a subflow at depth {chain.Depth + 1}, and subflow "
                + $"nesting is capped at {SubflowLimits.MaxDepth} (execution/SPEC.md §5). "
                + "Nothing ran for this node.");

        if (await FindSubflowCycleAsync(childId, chain, ct) is { } cycle)
            return Failed(node, "subflow_cycle", cycle);

        if (_subflows is null)
            return Failed(node, "subflow_missing",
                $"node '{node.Id}' is a subflow but this executor was built without a subflow "
                + "runner, so it cannot start a child run.");

        // The child's input: the parent run's input, shallow-merged with this node's
        // OTHER config_overrides keys, config_overrides winning — then resolved in the
        // PARENT's context. Resolving in the parent is the whole point: `{{ steps.x.output }}`
        // means the parent's step x, and the child has no such step.
        var merged = MergePayloads(RunInput, WithoutSubflowKey(node.ConfigOverrides));
        var input = _resolver.Resolve(
            merged, _completed, deviceContext: null, runInput: RunInput,
            runContext: RunContext?.ToJson());

        // templates/SPEC.md §8, and here it matters more than anywhere else: a residual
        // `{{ … }}` left in the merged payload becomes the child run's input, and from
        // there the recorded input of every step the child runs. The step fails instead
        // of passing fiction down a level where nobody will connect it back.
        var residual = VariableResolver.FindUnresolvedTemplates(input);
        if (residual.Count > 0)
            return Failed(node, "unresolved_template", VariableResolver.DescribeUnresolved(residual));

        var startedAt = DateTime.UtcNow;
        // Targets and the environment are inherited, not re-derived: the child does the
        // parent's work on the parent's devices under the parent's scope.
        var outcome = await _subflows.RunAsync(
            childId, input, Targets.Select(d => d.DeviceId).ToList(),
            chain.Descend(_scope.WorkflowRunId, childId), ct);
        var finishedAt = DateTime.UtcNow;

        var payload = SubflowOutput(outcome);
        _completed[node.Id] = new StepResult(payload);

        // The strictest tier among the child's executed nodes becomes this step's tier,
        // which is what puts the subflow into — or keeps it out of — the parent's
        // rollback plan. A child that sent an email is not reversible, and a parent that
        // listed this node as rollback-able would report `rolled_back` for a run whose
        // effects are still out there.
        var tier = outcome.StrictestTier;

        if (outcome.Failed)
        {
            RunContext?.MarkFailed(node.Id, outcome.Error);
            _logger.LogWarning(
                "workflow.node.subflow_failed node={Node} child={Child} run={Run}",
                node.Id, childId, outcome.RunId);
            // A child id that names nothing is a broken reference, not a child that
            // failed — §5 codes it `subflow_missing`, and sending an operator to read a
            // child run that does not exist is the wrong next step.
            var code = outcome.Unresolved ? "subflow_missing" : "subflow_failed";
            return new NodeOutcome(NodeResult.Failed, code, false, payload.GetRawText())
            {
                Tier = tier,
                ChildRunId = outcome.RunId,
                Error = outcome.Error,
                Input = InputSnapshot(input),
                StartedAt = startedAt,
                FinishedAt = finishedAt,
            };
        }

        return new NodeOutcome(
            outcome.AnyChanged ? NodeResult.Changed : NodeResult.NoChange,
            Output: payload.GetRawText())
        {
            Tier = tier,
            ChildRunId = outcome.RunId,
            Input = InputSnapshot(input),
            StartedAt = startedAt,
            FinishedAt = finishedAt,
        };
    }

    // The step's output (execution/SPEC.md §5): the child run's identity and verdict,
    // plus each of its steps' output keyed by node id, so a downstream template reads
    // `{{ steps.<subflow-node>.output.steps.<child-node>.… }}`.
    private static JsonElement SubflowOutput(SubflowOutcome outcome)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (outcome.RunId is { } id) w.WriteString("run_id", id); else w.WriteNull("run_id");
            w.WriteString("status", outcome.Status);
            w.WriteString("final_state", outcome.FinalState);
            if (outcome.Error is not null) w.WriteString("error", outcome.Error);
            w.WritePropertyName("steps");
            w.WriteStartObject();
            foreach (var step in outcome.Steps)
            {
                w.WritePropertyName(step.NodeId);
                if (string.IsNullOrWhiteSpace(step.Output)) { w.WriteNullValue(); continue; }
                try
                {
                    using var doc = JsonDocument.Parse(step.Output!);
                    doc.RootElement.WriteTo(w);
                }
                catch (JsonException)
                {
                    // A handler that returned something unparseable is a handler bug;
                    // it must not take the parent's whole step down with it.
                    w.WriteStringValue(step.Output);
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    // `config_overrides` minus the key that names the child. §5 calls the child's input
    // "the OTHER config_overrides keys": passing the id through too would put a
    // meaningless GUID in the child's `{{ input.* }}` namespace under a name that reads
    // like configuration.
    internal static JsonElement WithoutSubflowKey(JsonElement configOverrides)
    {
        if (configOverrides.ValueKind != JsonValueKind.Object) return EmptyInput;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var prop in configOverrides.EnumerateObject())
            {
                if (prop.NameEquals(SubflowWorkflowIdKey)) continue;
                w.WritePropertyName(prop.Name);
                prop.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    // Shallow merge, `config_overrides` winning (execution/SPEC.md §5). Shallow on
    // purpose: a deep merge would let a node half-override a nested object the child's
    // schema treats as one value, and the author could not tell from the graph which
    // half arrived.
    internal static JsonElement MergePayloads(JsonElement? runInput, JsonElement overrides)
    {
        var baseline = runInput is { ValueKind: JsonValueKind.Object } b ? b : EmptyInput;
        if (overrides.ValueKind != JsonValueKind.Object) return baseline;

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var prop in baseline.EnumerateObject())
            {
                if (overrides.TryGetProperty(prop.Name, out _)) continue;
                w.WritePropertyName(prop.Name);
                prop.Value.WriteTo(w);
            }
            foreach (var prop in overrides.EnumerateObject())
            {
                w.WritePropertyName(prop.Name);
                prop.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// The loop this node would start, described, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The walk starts at the candidate child with every ancestor already coloured grey,
    /// so "the child reaches a workflow that is already running above it" and "the child
    /// reaches itself" are the same test. It runs over the CURRENT rows rather than over
    /// the ancestry alone because a cycle can close through workflows nobody has started
    /// yet: A → B → A is not visible from inside A's run until B has already run, and
    /// §5 says the step fails before anything runs. A repeat visit that is not a loop —
    /// a diamond, where two branches call the same shared subflow — is coloured black
    /// and skipped, not reported.
    /// </remarks>
    private async Task<string?> FindSubflowCycleAsync(
        Guid childId, SubflowInvocation chain, CancellationToken ct)
    {
        const int White = 0, Grey = 1, Black = 2;
        var colour = new Dictionary<Guid, int>();
        foreach (var ancestor in chain.Ancestry) colour[ancestor] = Grey;

        var names = new Dictionary<Guid, string>();
        var children = new Dictionary<Guid, IReadOnlyList<Guid>>();
        var path = new List<Guid>(chain.Ancestry);
        var stack = new Stack<(Guid Id, bool Leaving)>();
        stack.Push((childId, false));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (id, leaving) = stack.Pop();
            if (leaving)
            {
                colour[id] = Black;
                path.RemoveAt(path.Count - 1);
                continue;
            }

            var seen = colour.GetValueOrDefault(id, White);
            if (seen == Grey)
            {
                path.Add(id);
                return await DescribeCycleAsync(path, id, names, ct);
            }
            if (seen == Black) continue;

            colour[id] = Grey;
            path.Add(id);
            stack.Push((id, true));
            foreach (var next in await ChildrenOfAsync(id, names, children, ct))
                stack.Push((next, false));
        }

        return null;
    }

    // The subflow ids a workflow's nodes name, cached per walk. A workflow row that no
    // longer exists has no children here; the runner reports it as `subflow_missing`
    // when it tries to start it, which is the accurate diagnosis.
    private async Task<IReadOnlyList<Guid>> ChildrenOfAsync(
        Guid workflowId, Dictionary<Guid, string> names,
        Dictionary<Guid, IReadOnlyList<Guid>> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(workflowId, out var cached)) return cached;

        var row = await _db.Workflows.AsNoTracking()
            .Where(w => w.WorkflowId == workflowId && w.IsActive)
            .Select(w => new { w.Name, w.NodesJson })
            .FirstOrDefaultAsync(ct);

        IReadOnlyList<Guid> refs = [];
        if (row is not null)
        {
            names[workflowId] = row.Name;
            try
            {
                using var doc = JsonDocument.Parse(row.NodesJson);
                refs = [.. NodeReferences.Extract(doc.RootElement).SubflowWorkflowIds];
            }
            catch (JsonException)
            {
                // A graph that will not parse cannot be walked. The run that reaches it
                // fails on its own terms; the cycle guard has nothing to say about it.
            }
        }

        cache[workflowId] = refs;
        return refs;
    }

    // Names the loop rather than saying one exists: "'deploy' → 'notify' → 'deploy'" is
    // a message an author can act on, "a cycle was detected" is not.
    private async Task<string> DescribeCycleAsync(
        IReadOnlyList<Guid> path, Guid repeated, Dictionary<Guid, string> names, CancellationToken ct)
    {
        var loop = path.SkipWhile(id => id != repeated).ToList();
        var unknown = loop.Where(id => !names.ContainsKey(id)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            var rows = await _db.Workflows.AsNoTracking()
                .Where(w => unknown.Contains(w.WorkflowId))
                .Select(w => new { w.WorkflowId, w.Name })
                .ToListAsync(ct);
            foreach (var r in rows) names[r.WorkflowId] = r.Name;
        }

        var trail = loop.Select(id => names.TryGetValue(id, out var n) ? $"'{n}'" : id.ToString());
        return $"this subflow node would start a workflow that reaches itself: {string.Join(" → ", trail)}. "
            + "A run that re-enters a workflow already running above it can never finish, so nothing was "
            + "started (execution/SPEC.md §5).";
    }

    private NodeOutcome Failed(WorkflowNode node, string code, string message)
    {
        _logger.LogWarning("workflow.node.failed node={Node} code={Code} message={Message}", node.Id, code, message);
        var payload = JsonSerializer.SerializeToElement(new { error = message });
        _completed[node.Id] = new StepResult(payload);
        RunContext?.MarkFailed(node.Id, message);
        var at = DateTime.UtcNow;
        // These never reached a handler — a snippet that does not resolve, a node with
        // no targets. The message is the whole diagnosis, so it goes in the error
        // column rather than only inside the payload.
        return new NodeOutcome(NodeResult.Failed, code, false, payload.GetRawText())
        {
            Error = message,
            Attempts = 0,
            StartedAt = at,
            FinishedAt = at,
        };
    }
}
