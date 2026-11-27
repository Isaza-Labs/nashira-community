using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Services.Ssh;

// When the SSH runner retries under the fallback Python environment.
//
// paramiko 5 removed the `ssh-rsa` host key algorithm. A device that offers nothing
// else (Extreme EXOS on OpenSSH 8.1) fails the negotiation with
//   paramiko.ssh_exception.IncompatiblePeer: Incompatible ssh peer (no acceptable host key)
// which the runner reports as `connect failed: …` (exit 5). That message is precise
// enough to act on: retrying with an older paramiko is the fix, and retrying anything
// else with it would only double the time to the same failure.
//
// Pure, so the decision is unit-tested without spawning Python.
public static class SshEnvironmentFallback
{
    private static readonly string[] Markers =
    [
        "Incompatible ssh peer",
        "no acceptable host key",
    ];

    /// <summary>Whether this outcome is paramiko refusing the peer's host key algorithms.</summary>
    /// <remarks>
    /// The runner's error first (a connect failure is fatal and lands there), then every
    /// command's own error: the result is also checked because a transport that fails
    /// after the outcome was parsed reports per command.
    /// </remarks>
    public static bool IsIncompatibleHostKey(SshRunOutcome outcome)
    {
        if (Matches(outcome.Error)) return true;
        return outcome.Result?.Results.Any(r => Matches(r.Error)) == true;
    }

    /// <summary>Whether to retry once under <paramref name="fallback"/>.</summary>
    /// <param name="outcome">The first attempt.</param>
    /// <param name="usedEnvironmentId">The environment the first attempt ran under; null for system.</param>
    /// <param name="fallback">The ready SSH fallback environment, or null when there is none.</param>
    public static bool ShouldRetry(SshRunOutcome outcome, Guid? usedEnvironmentId, ResolvedPythonEnvironment? fallback)
    {
        if (fallback is null) return false;
        // Retrying with the environment that just failed would fail identically.
        if (usedEnvironmentId == fallback.Id) return false;
        return IsIncompatibleHostKey(outcome);
    }

    private static bool Matches(string? text) =>
        !string.IsNullOrEmpty(text)
        && Markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
}
