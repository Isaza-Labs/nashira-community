using nashira_backend.Services.Engine;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// workflow.v1 execution/SPEC.md §3: the canonical retry shape (Flow Weaver's) is
// read and preferred, the legacy Nashira shape is translated, and Nashira's own
// profile — 5 attempts, 30 s, retryable AND idempotent — is kept on both.
public class RetryPolicyCanonicalTests
{
    [Fact]
    public void The_canonical_shape_is_read()
    {
        var p = RetryPolicy.Parse(
            """{"max_retries":2,"initial_delay_seconds":3,"backoff":"exponential","max_delay_seconds":10}""");

        Assert.Equal(2, p.MaxRetries);
        Assert.Equal(3, p.MaxAttempts);
        Assert.Equal(3, p.InitialDelaySeconds);
        Assert.Equal(10, p.MaxDelaySeconds);
        Assert.Equal(RetryPolicy.BackoffExponential, p.Backoff);
    }

    // max_retries counts EXTRA attempts: 0 is no retry, and the oracle's defaults
    // apply to what the policy leaves out.
    [Fact]
    public void Canonical_defaults_mean_no_retry()
    {
        var p = RetryPolicy.Parse("""{"backoff":"linear"}""");
        Assert.Equal(0, p.MaxRetries);
        Assert.Equal(1, p.MaxAttempts);
        Assert.False(p.ShouldRetry(1, retryable: true, IdempotencyKind.Idempotent));
        Assert.Equal(5, p.InitialDelaySeconds);
    }

    // exponential initial·2^(k-1), linear initial·k, fixed initial — k the retry's
    // own 1-based index — clamped to max_delay_seconds.
    [Theory]
    [InlineData("exponential", 2, 4, 8)]
    [InlineData("linear", 2, 4, 6)]
    [InlineData("fixed", 2, 2, 2)]
    public void Each_backoff_shape_computes_the_contract_delays(string backoff, int d1, int d2, int d3)
    {
        var p = RetryPolicy.Parse(
            $$"""{"max_retries":4,"initial_delay_seconds":2,"backoff":"{{backoff}}","max_delay_seconds":30}""");

        Assert.Equal(TimeSpan.Zero, p.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(d1), p.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(d2), p.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(d3), p.DelayBefore(4));
    }

    [Fact]
    public void Delays_are_clamped_to_max_delay_seconds()
    {
        var p = RetryPolicy.Parse(
            """{"max_retries":4,"initial_delay_seconds":4,"backoff":"linear","max_delay_seconds":6}""");
        Assert.Equal(TimeSpan.FromSeconds(4), p.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(6), p.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(6), p.DelayBefore(5));
    }

    // The profile: a bundle cannot raise the caps.
    [Fact]
    public void Nashiras_caps_apply_to_the_canonical_shape_too()
    {
        var p = RetryPolicy.Parse(
            """{"max_retries":50,"initial_delay_seconds":100,"backoff":"fixed","max_delay_seconds":300}""");

        Assert.Equal(4, p.MaxRetries);
        Assert.Equal(5, p.MaxAttempts);
        Assert.Equal(30, p.MaxDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(30), p.DelayBefore(2));
        Assert.False(p.ShouldRetry(5, retryable: true, IdempotencyKind.Idempotent));
        // …and the two-gate rule is unchanged.
        Assert.False(p.ShouldRetry(1, retryable: true, IdempotencyKind.RequiresCompensation));
        Assert.False(p.ShouldRetry(1, retryable: false, IdempotencyKind.Idempotent));
    }

    // Legacy: max_retries = max_attempts − 1, initial = delay_seconds, max_delay 30.
    [Fact]
    public void The_legacy_shape_translates()
    {
        var p = RetryPolicy.Parse("""{"max_attempts":3,"delay_seconds":2,"backoff":"exponential"}""");

        Assert.Equal(2, p.MaxRetries);
        Assert.Equal(3, p.MaxAttempts);
        Assert.Equal(2, p.InitialDelaySeconds);
        Assert.Equal(30, p.MaxDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(2), p.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(4), p.DelayBefore(3));
    }

    [Fact]
    public void Legacy_accepts_linear_as_well()
    {
        var p = RetryPolicy.Parse("""{"max_attempts":4,"delay_seconds":3,"backoff":"linear"}""");
        Assert.Equal(TimeSpan.FromSeconds(3), p.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(6), p.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(9), p.DelayBefore(4));
    }

    // When both attempt keys appear the canonical one wins.
    [Fact]
    public void Canonical_is_preferred_when_both_shapes_are_present()
    {
        var p = RetryPolicy.Parse("""{"max_retries":1,"max_attempts":5,"delay_seconds":9,"initial_delay_seconds":1}""");
        Assert.Equal(1, p.MaxRetries);
        Assert.Equal(1, p.InitialDelaySeconds);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"exponential\"")]
    [InlineData("{\"max_retries\":\"three\"}")]
    public void A_malformed_policy_means_no_retry(string json)
    {
        Assert.Equal(1, RetryPolicy.Parse(json).MaxAttempts);
    }
}
