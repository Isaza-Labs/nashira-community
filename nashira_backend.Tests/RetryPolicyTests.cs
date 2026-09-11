using nashira_backend.Services.Engine;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// Retries are gated twice: the failure must be retryable AND the step idempotent.
// The second gate is why an author's idempotency declaration is load-bearing —
// it decides whether a timeout is retried or surfaced.
public class RetryPolicyTests
{
    [Fact]
    public void An_absent_policy_means_a_single_attempt()
    {
        var p = RetryPolicy.Parse(null);
        Assert.Equal(1, p.MaxAttempts);
        Assert.False(p.ShouldRetry(1, retryable: true, IdempotencyKind.Idempotent));
    }

    // Failing closed costs one attempt; failing open costs a hung run.
    [Fact]
    public void A_malformed_policy_means_a_single_attempt()
    {
        Assert.Equal(1, RetryPolicy.Parse("{not json").MaxAttempts);
    }

    [Fact]
    public void Attempts_are_clamped_so_a_policy_cannot_pin_a_run_open()
    {
        Assert.Equal(5, RetryPolicy.Parse("""{"max_attempts":99}""").MaxAttempts);
        Assert.Equal(1, RetryPolicy.Parse("""{"max_attempts":0}""").MaxAttempts);
    }

    [Fact]
    public void A_non_retryable_failure_is_never_retried()
    {
        // A 404 fails identically three times; retrying only makes it slower.
        var p = RetryPolicy.Parse("""{"max_attempts":3}""");
        Assert.False(p.ShouldRetry(1, retryable: false, IdempotencyKind.Idempotent));
    }

    // The important one: re-running a step that may already have had an effect can
    // create the same resource twice.
    [Theory]
    [InlineData(IdempotencyKind.RequiresCompensation)]
    [InlineData(IdempotencyKind.NonReversible)]
    public void A_non_idempotent_step_is_never_retried(IdempotencyKind tier)
    {
        var p = RetryPolicy.Parse("""{"max_attempts":3}""");
        Assert.False(p.ShouldRetry(1, retryable: true, tier));
    }

    [Fact]
    public void An_idempotent_retryable_failure_retries_until_the_budget_is_spent()
    {
        var p = RetryPolicy.Parse("""{"max_attempts":3}""");
        Assert.True(p.ShouldRetry(1, true, IdempotencyKind.Idempotent));
        Assert.True(p.ShouldRetry(2, true, IdempotencyKind.Idempotent));
        Assert.False(p.ShouldRetry(3, true, IdempotencyKind.Idempotent));
    }

    [Fact]
    public void The_first_attempt_never_waits()
    {
        Assert.Equal(TimeSpan.Zero, RetryPolicy.Parse("""{"delay_seconds":5}""").DelayBefore(1));
    }

    [Fact]
    public void Exponential_backoff_doubles_and_is_capped()
    {
        var p = RetryPolicy.Parse("""{"delay_seconds":2,"backoff":"exponential","max_attempts":5}""");
        Assert.Equal(TimeSpan.FromSeconds(2), p.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(4), p.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(8), p.DelayBefore(4));
        Assert.True(p.DelayBefore(20) <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Fixed_backoff_does_not_grow()
    {
        var p = RetryPolicy.Parse("""{"delay_seconds":3}""");
        Assert.Equal(TimeSpan.FromSeconds(3), p.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(3), p.DelayBefore(4));
    }
}
