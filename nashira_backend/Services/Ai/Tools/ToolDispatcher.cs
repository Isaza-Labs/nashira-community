using System.Text.Json;
using System.Text.Json.Nodes;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.SelfCorrection;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Trace;

namespace nashira_backend.Services.Ai.Tools;

// Scoped: resolves + executes a tool handler for a tool call. Four gates run
// before a handler is reached:
//   1. Role check (PermissionClassifier.IsAllowed).
//   2. Per-user resource permissions — the grants an administrator configured for
//      this user on this domain and on the specific system the call names.
//   3. Autonomy tier — human_only tools are never executed by the agent.
//   4. Mutation budget — a per-scope counter of non-autonomous calls, so a
//      runaway loop can't burn through many mutations in one turn.
// A ToolDispatcher lives for one chat turn (scoped), so the budget resets per turn.
public sealed class ToolDispatcher
{
    public const int DefaultMutationBudget = 20;

    private readonly ToolRegistry _registry;
    private readonly IServiceProvider _sp;
    private readonly ICurrentUser _user;
    private readonly PermissionClassifier _permissions;
    private readonly IToolResourceGuard _resources;
    private readonly IToolAutonomyResolver _autonomy;
    private readonly IAuditLogger _audit;
    private readonly SelfCorrectionEngine _selfCorrection;
    private readonly ILogger<ToolDispatcher> _logger;
    private readonly ITraceLogger _trace;
    private int _mutationsUsed;
    private HashSet<string> _approved = new(StringComparer.OrdinalIgnoreCase);

    public ToolDispatcher(
        ToolRegistry registry, IServiceProvider sp, ICurrentUser user,
        PermissionClassifier permissions, IToolResourceGuard resources,
        IToolAutonomyResolver autonomy, IAuditLogger audit,
        SelfCorrectionEngine selfCorrection, ILogger<ToolDispatcher> logger,
        ITraceLogger trace)
    {
        _trace = trace;
        _registry = registry;
        _sp = sp;
        _user = user;
        _permissions = permissions;
        _resources = resources;
        _autonomy = autonomy;
        _audit = audit;
        _selfCorrection = selfCorrection;
        _logger = logger;
    }

    public int MutationBudget { get; set; } = DefaultMutationBudget;
    public int MutationsUsed => _mutationsUsed;

    // Tools the user approved for this turn — checked by the runner before dispatch.
    public void ApproveTools(IEnumerable<string> names) =>
        _approved = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    // Returns the confirmation tier if this CALL needs (still-unapproved) confirmation
    // before it may run (governance C: single_confirm / elevated_confirm), else null.
    //
    // Takes the arguments, not just the tool name: `execute_operation` and `mcp_call`
    // each cover reads and writes alike, and gating a GET behind the same approval as a
    // DELETE is what turned every question into a permission dialog.
    public async Task<string?> RequiresConfirmationAsync(
        string toolName, JsonElement args, CancellationToken ct)
    {
        var tier = _permissions.GetPermission(toolName).Tier;
        var needs = string.Equals(tier, PermissionClassifier.TierSingleConfirm, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(tier, PermissionClassifier.TierElevatedConfirm, StringComparison.OrdinalIgnoreCase);
        if (!needs || _approved.Contains(toolName)) return null;

        // elevated_confirm is never waived: those are the calls that cannot be undone
        // from here, and no argument inspection makes one of them a read.
        if (string.Equals(tier, PermissionClassifier.TierSingleConfirm, StringComparison.OrdinalIgnoreCase)
            && await _autonomy.IsReadOnlyCallAsync(toolName, args, ct))
            return null;

        return tier;
    }

    public async Task<ToolCallOutput> DispatchAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var entry = _registry.Get(toolName);
        if (entry is null)
        {
            _logger.LogWarning("ai.tool.dispatch.unknown tool={Tool}", toolName);
            _trace.Event(TraceEvent.CategoryTool, $"tool.{toolName}",
                new { tool = toolName }, error: "unknown tool");
            return ToolCallOutput.Error($"unknown tool: {toolName}");
        }

        var role = _user.Roles.FirstOrDefault() ?? "viewer";
        if (!_permissions.IsAllowed(toolName, role, _user))
        {
            _trace.Event(TraceEvent.CategoryTool, $"tool.{toolName}",
                new { tool = toolName, role, gate = "role" },
                error: $"the {role} role may not call this tool");
            return ToolCallOutput.Error($"permission denied: {toolName} requires a higher role (current: {role})");
        }

        // Resource grants are evaluated against the arguments, not just the tool name:
        // "this user may not touch the Splunk MCP server" is a statement about the
        // server, and the tool that reaches it is the same one that reaches every other.
        if (await _resources.DenyReasonAsync(toolName, args, ct) is { } denial)
        {
            _trace.Event(TraceEvent.CategoryTool, $"tool.{toolName}",
                new { tool = toolName, gate = "resource_grant" }, error: denial);
            return ToolCallOutput.Error(denial);
        }

        var permission = _permissions.GetPermission(toolName);
        if (string.Equals(permission.Tier, PermissionClassifier.TierHumanOnly, StringComparison.OrdinalIgnoreCase))
        {
            _trace.Event(TraceEvent.CategoryTool, $"tool.{toolName}",
                new { tool = toolName, gate = "autonomy" }, error: "human_only — the agent may never run this");
            return ToolCallOutput.Error(
                $"{toolName} is human_only — the agent cannot execute it. Tell the user to do it from the UI.");
        }

        var counts = _permissions.CountsAgainstMutationBudget(toolName);
        if (counts && _mutationsUsed >= MutationBudget)
        {
            _trace.Event(TraceEvent.CategoryTool, $"tool.{toolName}",
                new { tool = toolName, gate = "mutation_budget", used = _mutationsUsed, budget = MutationBudget },
                error: "the turn's mutation budget is spent");
            return ToolCallOutput.Error(
                $"mutation budget exceeded ({_mutationsUsed}/{MutationBudget}). Summarize what changed and ask the user to re-authorize before more mutations this turn.");
        }

        var handler = (IToolHandler)_sp.GetRequiredService(entry.HandlerType);
        // Reads are not audited — only mutations are — so for the majority of what the
        // agent does, this row is the only record that the call happened at all.
        using var trace = _trace.Begin(TraceEvent.CategoryTool, $"tool.{toolName}",
            new { tool = toolName, mutating = counts });
        var (result, error) = await RunAsync(handler, args, ct);
        if (error is null) trace.Complete(new { tool = toolName, mutating = counts });
        else trace.Fail(error, new { tool = toolName, mutating = counts });

        if (counts) _mutationsUsed++;
        _logger.LogInformation(
            "ai.tool.dispatch.end tool={Tool} elapsed_ms={Elapsed} mutations={Used}/{Budget} ok={Ok}",
            toolName, sw.ElapsedMilliseconds, _mutationsUsed, MutationBudget, error is null);

        // Audit the executed artifact — mutating tools only (reads aren't audited).
        if (counts) await AuditAsync(toolName, args, error is null, error, ct);

        if (error is null) return new ToolCallOutput { Success = true, Result = result };

        // The tool FAILED. Self-correction may retry (a fresh handler call) or escalate a
        // hint, but the reported Success must reflect the ACTUAL outcome — only a clean run
        // or a successful retry is a success. `result` still carries the error text either
        // way so the model (and the SSE tool_result frame) can react to it.
        if (!IsCorrectable(toolName)) return new ToolCallOutput { Success = false, Result = result };

        var correction = await _selfCorrection.SuggestCorrectionAsync(toolName, args, error, ct);
        if (correction is null) return new ToolCallOutput { Success = false, Result = result };

        if (correction.Strategy == SelfCorrectionEngine.StrategyParameterAdjust && correction.CorrectedArgs is { } corrected)
        {
            _logger.LogInformation("ai.tool.selfcorrect.retry tool={Tool} source={Source}", toolName, correction.Source);
            var (retryResult, retryError) = await RunAsync(handler, corrected, ct);
            if (counts) await AuditAsync(toolName, corrected, retryError is null, retryError, ct);
            await _selfCorrection.RecordOutcomeAsync(new CorrectionAttempt(toolName, args, error, correction, retryError is null), ct);
            return new ToolCallOutput { Success = retryError is null, Result = retryResult };
        }

        if (correction.Strategy == SelfCorrectionEngine.StrategyEscalate)
            return new ToolCallOutput { Success = false, Result = EnrichHint(result, correction.Message) };

        return new ToolCallOutput { Success = false, Result = result };
    }

    private async Task<(JsonElement Result, string? Error)> RunAsync(IToolHandler handler, JsonElement args, CancellationToken ct)
    {
        try
        {
            var r = await handler.ExecuteAsync(args, ct);
            return (r, ExtractError(r));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.execute.error tool={Tool}", handler.Name);
            return (JsonSerializer.SerializeToElement(new { error = ex.Message }), ex.Message);
        }
    }

    private static readonly string[] SkipCorrectionSubstrings = ["csv", "html", "bulk", "export"];

    private static bool IsCorrectable(string toolName)
    {
        var n = toolName.ToLowerInvariant();
        return !SkipCorrectionSubstrings.Any(s => n.Contains(s, StringComparison.Ordinal));
    }

    private static JsonElement EnrichHint(JsonElement result, string? hint)
    {
        if (string.IsNullOrEmpty(hint)) return result;
        try
        {
            if (JsonNode.Parse(result.GetRawText()) is JsonObject obj)
            {
                obj["_correction_hint"] = hint;
                return JsonSerializer.SerializeToElement(obj);
            }
        }
        catch (JsonException) { }
        return result;
    }

    private async Task AuditAsync(string toolName, JsonElement args, bool ok, string? error, CancellationToken ct)
    {
        try
        {
            // Redacted, not raw: `set_secret` takes the value and `create_credential`
            // takes a password, so auditing the arguments verbatim wrote plaintext
            // secrets into the one table built to be handed to an auditor.
            await _audit.LogAsync("agent.tool", entityId: null, action: toolName,
                before: null,
                after: new { arguments = Conversation.ToolTelemetry.Redact(args), ok, error }, ct);
        }
        catch (Exception ex)
        {
            // Audit must never break tool dispatch, but a gap is a serious signal.
            _logger.LogError(ex, "audit.tool.write_failed tool={Tool}", toolName);
        }
    }

    private static string? ExtractError(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
}

public sealed class ToolCallOutput
{
    public bool Success { get; init; }
    public JsonElement Result { get; init; }
    public string? ErrorMessage { get; init; }

    // Serialize the error via the serializer so quotes/backslashes in the
    // message can't break downstream JSON parsing.
    public static ToolCallOutput Error(string message) => new()
    {
        Success = false,
        ErrorMessage = message,
        Result = JsonSerializer.SerializeToElement(new { error = message }),
    };
}
