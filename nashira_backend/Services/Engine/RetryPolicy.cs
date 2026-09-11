using System.Text.Json;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Engine;

// How many times a failing step is re-attempted, and how long between attempts.
//
// The shape is workflow.v1's canonical one (execution/SPEC.md §3, Flow Weaver is
// the oracle):
//
//   { "max_retries": 2, "initial_delay_seconds": 5, "backoff": "exponential",
//     "max_delay_seconds": 300 }
//
// `max_retries` counts the extra attempts after the first — 0 means no retry.
// The delay before retry k (1-based) is initial·2^(k-1) for exponential, initial·k
// for linear and initial for fixed, clamped to max_delay_seconds. Nashira's legacy
// shape `{ max_attempts, delay_seconds, backoff }` is still read: max_retries =
// max_attempts − 1, initial = delay_seconds, max_delay = 30. Exporters write the
// canonical shape; this class only has to understand both.
//
// Two conditions gate every retry, and both matter:
//
//   1. The handler marked the failure retryable. A 404 or a malformed input will
//      fail identically three times; retrying it only makes the run slower and the
//      logs longer.
//
//   2. The step is idempotent. Re-running a step that already had an effect is
//      worse than failing: a `rest_call` that timed out may well have been
//      received, so a retry can create the same resource twice. Only a step whose
//      effective tier says "safe to re-run with the same input" qualifies, which is
//      exactly what IdempotencyKind.Idempotent means.
//
// The second gate is why an author's idempotency declaration is load-bearing and
// not documentation: it decides whether a timeout is retried or surfaced. It is
// also Nashira's declared profile difference from the oracle, together with the
// caps — 5 attempts, 30 s between them — which a bundle cannot raise: a policy
// must not be able to pin a synchronous run open.
public sealed class RetryPolicy
{
    public const string BackoffFixed = "fixed";
    public const string BackoffExponential = "exponential";
    public const string BackoffLinear = "linear";

    public static readonly RetryPolicy None = new();

    private const int MaxAttemptsCeiling = 5;
    private static readonly TimeSpan MaxDelayCeiling = TimeSpan.FromSeconds(30);

    // The oracle's defaults, used when the canonical shape omits a field.
    private const double CanonicalInitialDelay = 5;
    private const double CanonicalMaxDelay = 300;

    // Extra attempts after the first; 0 = no retry. Capped at 4 so the total
    // never exceeds 5 attempts.
    public int MaxRetries { get; private set; }
    public double InitialDelaySeconds { get; private set; } = CanonicalInitialDelay;
    public string Backoff { get; private set; } = BackoffExponential;
    public double MaxDelaySeconds { get; private set; } = MaxDelayCeiling.TotalSeconds;

    // Total attempts, not extra ones: 1 means "no retry". What the executor logs.
    public int MaxAttempts => MaxRetries + 1;

    public static RetryPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;

            // The discriminator is the attempt-count key: `max_retries` is the
            // canonical shape, `max_attempts` the legacy one. A policy naming
            // neither is canonical with the oracle's defaults, which means no retry.
            var legacy = !root.TryGetProperty("max_retries", out _)
                         && (root.TryGetProperty("max_attempts", out _) || root.TryGetProperty("delay_seconds", out _));

            var policy = new RetryPolicy();
            if (legacy)
            {
                var maxAttempts = Int(root, "max_attempts", 1);
                policy.MaxRetries = maxAttempts - 1;
                policy.InitialDelaySeconds = Num(root, "delay_seconds", 1);
                policy.Backoff = Str(root, "backoff") ?? BackoffFixed;
                policy.MaxDelaySeconds = MaxDelayCeiling.TotalSeconds;
            }
            else
            {
                policy.MaxRetries = Int(root, "max_retries", 0);
                policy.InitialDelaySeconds = Num(root, "initial_delay_seconds", CanonicalInitialDelay);
                policy.Backoff = Str(root, "backoff") ?? BackoffExponential;
                policy.MaxDelaySeconds = Num(root, "max_delay_seconds", CanonicalMaxDelay);
            }

            policy.MaxRetries = Math.Clamp(policy.MaxRetries, 0, MaxAttemptsCeiling - 1);
            policy.InitialDelaySeconds = Math.Clamp(policy.InitialDelaySeconds, 0, MaxDelayCeiling.TotalSeconds);
            policy.MaxDelaySeconds = Math.Clamp(policy.MaxDelaySeconds, 0, MaxDelayCeiling.TotalSeconds);
            policy.Backoff = policy.Backoff.Trim().ToLowerInvariant();
            return policy;
        }
        catch (JsonException)
        {
            // A malformed policy means "no retry", not "retry forever". Failing
            // closed here costs one attempt; failing open costs a hung run.
            return None;
        }
    }

    // Delay before attempt number `attempt` (1-based; attempt 1 has no delay).
    public TimeSpan DelayBefore(int attempt)
    {
        if (attempt <= 1) return TimeSpan.Zero;
        var k = attempt - 1; // the retry's own 1-based index
        var seconds = Backoff switch
        {
            BackoffExponential => InitialDelaySeconds * Math.Pow(2, k - 1),
            BackoffLinear => InitialDelaySeconds * k,
            _ => InitialDelaySeconds,
        };
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelaySeconds));
    }

    // Both gates. `tier` is the step's effective idempotency.
    public bool ShouldRetry(int attemptsSoFar, bool retryable, IdempotencyKind tier) =>
        attemptsSoFar < MaxAttempts && retryable && tier == IdempotencyKind.Idempotent;

    private static int Int(JsonElement o, string key, int fallback) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static double Num(JsonElement o, string key, double fallback) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
