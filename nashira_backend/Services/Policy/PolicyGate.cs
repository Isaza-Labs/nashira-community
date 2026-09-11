using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using PolicyEntity = nashira_backend.Data.Models.Policy;

namespace nashira_backend.Services.Policy;

// The transition a gate is being asked about.
public sealed record PolicyGateContext(string On, string FromEnvironment, string ToEnvironment, Guid WorkflowId);

public interface IPolicyGate
{
    Task<PolicyDecision> EvaluateAsync(PolicyGateContext context, CancellationToken ct);
}

// Evaluates `action: "gate"` policies against a workflow's run history.
//
// Split from PolicyEvaluator because the two answer different questions and need
// different inputs: a deny is a pure function of the run about to happen, while a
// gate has to query what has already happened. Keeping them in one class would
// have meant a DB round trip on every deny evaluation too.
//
// Fail closed, same as PolicyEvaluator: a gate that cannot be evaluated refuses
// and names itself. A guardrail that silently stops guarding is worse than one
// that blocks until someone fixes it — and unlike a deny, a gate blocks a
// promotion, which is a deliberate act someone is present for.
public sealed class PolicyGate : IPolicyGate
{
    public const string RequireSuccessfulRuns = "successful_runs";
    public const string RequireLastSuccessWithin = "last_successful_run_within";

    public const string ScopeThisWorkflow = "this_workflow";
    public const string ScopeAnyWorkflow = "any_workflow";

    private readonly AppDbContext _db;
    private readonly ILogger<PolicyGate> _logger;

    public PolicyGate(AppDbContext db, ILogger<PolicyGate> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PolicyDecision> EvaluateAsync(PolicyGateContext context, CancellationToken ct)
    {
        var policies = await _db.Policies.AsNoTracking()
            .Where(p => p.IsActive && p.Enabled)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

        foreach (var policy in policies)
        {
            JsonElement rule;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(policy.RuleJson) ? "{}" : policy.RuleJson);
                rule = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // PolicyEvaluator already refuses runs on this. Refusing promotions
                // too keeps a broken policy from being half-enforced.
                return new PolicyDecision(true, policy.Name,
                    "this policy's rule is not valid JSON and cannot be evaluated");
            }

            if (rule.ValueKind != JsonValueKind.Object) continue;
            if (Str(rule, "action")?.Trim().ToLowerInvariant() != PolicyEntity.ActionGate) continue;
            if (!AppliesTo(rule, context)) continue;

            var unmet = await UnmetRequirementsAsync(rule, policy.Name, context, ct);
            if (unmet.Count > 0)
            {
                var reason = Str(rule, "reason") is { Length: > 0 } r
                    ? $"{r} — {string.Join("; ", unmet)}"
                    : string.Join("; ", unmet);

                _logger.LogInformation(
                    "policy.gate.blocked policy={Policy} workflow={Workflow} from={From} to={To}",
                    policy.Name, context.WorkflowId, context.FromEnvironment, context.ToEnvironment);

                return new PolicyDecision(true, policy.Name, reason);
            }
        }

        return PolicyDecision.Allowed;
    }

    // A gate with no `on`/`from`/`to` applies to every transition. Each present
    // field must match, so adding one narrows the gate.
    private static bool AppliesTo(JsonElement rule, PolicyGateContext context)
    {
        if (Str(rule, "on") is { Length: > 0 } on
            && !on.Equals(context.On, StringComparison.OrdinalIgnoreCase)) return false;
        if (Str(rule, "from") is { Length: > 0 } from
            && !from.Equals(context.FromEnvironment, StringComparison.OrdinalIgnoreCase)) return false;
        if (Str(rule, "to") is { Length: > 0 } to
            && !to.Equals(context.ToEnvironment, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Every unmet requirement, not just the first: discovering them one promotion
    // at a time is a bad way to learn what the organisation expects.
    private async Task<List<string>> UnmetRequirementsAsync(
        JsonElement rule, string policyName, PolicyGateContext context, CancellationToken ct)
    {
        if (!rule.TryGetProperty("require", out var require) || require.ValueKind != JsonValueKind.Array)
            return [$"policy '{policyName}' is a gate with no `require` list, so it can never be satisfied"];

        if (require.GetArrayLength() == 0)
            return [$"policy '{policyName}' has an empty `require` list, so it can never be satisfied"];

        var unmet = new List<string>();

        foreach (var req in require.EnumerateArray())
        {
            if (req.ValueKind != JsonValueKind.Object)
            {
                unmet.Add($"policy '{policyName}' has a malformed requirement");
                continue;
            }

            switch (Str(req, "type")?.Trim().ToLowerInvariant())
            {
                case RequireSuccessfulRuns:
                {
                    var min = Int(req, "min");
                    if (min is null)
                    {
                        unmet.Add($"policy '{policyName}': `min` is missing or not an integer");
                        break;
                    }

                    var scope = Str(req, "scope")?.Trim().ToLowerInvariant() ?? ScopeThisWorkflow;
                    if (scope is not (ScopeThisWorkflow or ScopeAnyWorkflow))
                    {
                        unmet.Add($"policy '{policyName}': unknown scope '{scope}'");
                        break;
                    }

                    // Optional recency window. A count with no horizon is satisfied by
                    // runs from a year ago, which is not what "three clean runs" means
                    // to anyone saying it out loud. Absent or 0 keeps the old
                    // behaviour — every successful run in scope counts.
                    var withinDays = Int(req, "within_days");
                    if (withinDays is <= 0) withinDays = null;
                    var window = withinDays is { } w ? DateTime.UtcNow.AddDays(-w) : (DateTime?)null;

                    var count = await SuccessfulRunsAsync(context, scope, window, ct);
                    if (count < min)
                    {
                        unmet.Add(withinDays is { } days
                            ? $"needs {min} successful run(s) in the last {days} day(s), has {count}"
                            : $"needs {min} successful run(s), has {count}");
                    }
                    break;
                }

                case RequireLastSuccessWithin:
                {
                    var days = Int(req, "days");
                    if (days is null)
                    {
                        unmet.Add($"policy '{policyName}': `days` is missing or not an integer");
                        break;
                    }

                    var since = DateTime.UtcNow.AddDays(-days.Value);
                    var scope = Str(req, "scope")?.Trim().ToLowerInvariant() ?? ScopeThisWorkflow;
                    var count = await SuccessfulRunsAsync(context, scope, since, ct);
                    if (count == 0) unmet.Add($"needs a successful run in the last {days} day(s)");
                    break;
                }

                default:
                    // Fail closed. Skipping an unknown requirement would let a typo
                    // turn a gate into no gate at all, while it still reads as a
                    // guardrail on the policy list.
                    unmet.Add(
                        $"policy '{policyName}' requires '{Str(req, "type")}', which this build cannot evaluate");
                    break;
            }
        }

        return unmet;
    }

    // Only runs that actually completed count. A failed or rolled-back run is
    // evidence the workflow does not work, which is the opposite of what a gate
    // asking for successful runs wants to see.
    private Task<int> SuccessfulRunsAsync(
        PolicyGateContext context, string scope, DateTime? since, CancellationToken ct)
    {
        var q = _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive && r.Status == Data.Models.WorkflowRun.StatusCompleted);

        // `this_workflow` means the row being promoted. A promotion produces a NEW
        // row, so history accrues against the draft/qa row it came from — which is
        // the one whose runs prove anything.
        if (scope == ScopeThisWorkflow) q = q.Where(r => r.WorkflowId == context.WorkflowId);
        else q = q.Where(r => r.Environment == context.FromEnvironment);

        if (since is { } from) q = q.Where(r => r.StartedAt >= from);

        return q.CountAsync(ct);
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : null;
}
