using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using PolicyEntity = nashira_backend.Data.Models.Policy;

namespace nashira_backend.Services.Policy;

// What a policy is evaluated against. Assembled by the caller from the run it is
// about to perform.
public sealed record PolicyContext(
    string Environment,
    string? WorkflowDescription = null,
    IReadOnlyCollection<string>? DeviceRoles = null,
    IReadOnlyCollection<string>? DevicePools = null,
    IReadOnlyCollection<string>? SnippetTypes = null);

public sealed record PolicyDecision(bool Denied, string? PolicyName, string? Reason)
{
    public static readonly PolicyDecision Allowed = new(false, null, null);
}

public interface IPolicyEvaluator
{
    Task<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken ct);

    // Same evaluation against an ad-hoc rule, for the dry-run endpoint. An admin
    // has to be able to test a guardrail before enabling it — a rule first
    // exercised in production is a rule nobody has read carefully.
    PolicyDecision Test(string ruleJson, PolicyContext context, string policyName = "(test)");
}

// Walks the enabled policies and returns the first deny.
//
// Short-circuits: guardrails are independent prohibitions, so one match is enough
// and evaluating the rest would only cost time. The reported reason is that
// policy's own, because "denied by policy" without saying which is unactionable.
public sealed class PolicyEvaluator : IPolicyEvaluator
{
    private readonly AppDbContext _db;
    private readonly ILogger<PolicyEvaluator> _logger;

    public PolicyEvaluator(AppDbContext db, ILogger<PolicyEvaluator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken ct)
    {
        var policies = await _db.Policies.AsNoTracking()
            .Where(p => p.IsActive && p.Enabled)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        foreach (var policy in policies)
        {
            var decision = Test(policy.RuleJson, context, policy.Name);
            if (decision.Denied)
            {
                _logger.LogInformation(
                    "policy.denied policy={Policy} environment={Environment}",
                    policy.Name, context.Environment);
                return decision;
            }
        }

        return PolicyDecision.Allowed;
    }

    public PolicyDecision Test(string ruleJson, PolicyContext context, string policyName = "(test)")
    {
        JsonElement rule;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(ruleJson) ? "{}" : ruleJson);
            rule = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A rule that does not parse cannot be shown to match, and a guardrail
            // that silently stops guarding is the worst outcome here. Deny, and name
            // the policy so an admin can find and fix it.
            //
            // This is the opposite of the ConditionEvaluator's fail-closed, which
            // returns false: there, false means "do not take this branch"; here,
            // denying is the cautious answer.
            _logger.LogWarning("policy.unparseable policy={Policy}", policyName);
            return new PolicyDecision(true, policyName, "this policy's rule is not valid JSON and cannot be evaluated");
        }

        if (rule.ValueKind != JsonValueKind.Object) return PolicyDecision.Allowed;

        // Only `deny` is understood. An unrecognised action is not treated as a
        // deny — an author who typed "warn" wanted something this build does not
        // do, and blocking their work is not a reasonable reading of that.
        var action = Str(rule, "action")?.Trim().ToLowerInvariant();
        if (action != PolicyEntity.ActionDeny) return PolicyDecision.Allowed;

        if (!rule.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.Object)
        {
            // No conditions means the policy denies everything. That is a legitimate
            // thing to express ("nothing runs during the freeze"), so it is honoured
            // rather than treated as a mistake.
            return new PolicyDecision(true, policyName, Reason(rule));
        }

        var matched =
            ClauseMatches(when, "environment", [context.Environment])
            && ClauseMatches(when, "device_role", context.DeviceRoles)
            && ClauseMatches(when, "device_pool", context.DevicePools)
            && ClauseMatches(when, "snippet_type", context.SnippetTypes)
            && SubstringMatches(when, "description_contains", context.WorkflowDescription);

        return matched ? new PolicyDecision(true, policyName, Reason(rule)) : PolicyDecision.Allowed;
    }

    // An absent clause matches: it places no restriction. A present clause matches
    // when any of its values appears in the context, so clauses AND together and
    // values within a clause OR — adding a clause narrows, never widens.
    //
    // A present clause against an empty context does NOT match: a policy about
    // device roles cannot fire on a run that targets no devices.
    private static bool ClauseMatches(JsonElement when, string key, IReadOnlyCollection<string>? actual)
    {
        if (!when.TryGetProperty(key, out var clause)) return true;

        var wanted = Values(clause);
        if (wanted.Count == 0) return true;
        if (actual is null || actual.Count == 0) return false;

        return actual.Any(a => wanted.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    private static bool SubstringMatches(JsonElement when, string key, string? actual)
    {
        if (!when.TryGetProperty(key, out var clause)) return true;

        var wanted = Values(clause);
        if (wanted.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(actual)) return false;

        return wanted.Any(w => actual.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // Accepts a single string as well as an array, because writing one value as a
    // bare string is what everybody does first.
    private static List<string> Values(JsonElement clause)
    {
        if (clause.ValueKind == JsonValueKind.String)
        {
            var single = clause.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single!];
        }
        if (clause.ValueKind != JsonValueKind.Array) return [];

        return clause.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(v => v.Length > 0)
            .ToList();
    }

    private static string Reason(JsonElement rule) =>
        Str(rule, "reason") is { Length: > 0 } r ? r : "denied by policy";

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
