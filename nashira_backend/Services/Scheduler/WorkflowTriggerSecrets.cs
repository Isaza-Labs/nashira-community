using System.Security.Cryptography;

namespace nashira_backend.Services.Scheduler;

// The two random values a webhook trigger is born with. One place, because both the
// trigger endpoint and the bundle importer create triggers and the security property
// — random, never derived from the name — must not depend on which path made the row.
public static class WorkflowTriggerSecrets
{
    // Public path segment: /api/hooks/{route}. Random, not derived from the name: a
    // guessable path plus allow_unsigned would be an open trigger.
    public static string NewRoute() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // HMAC-SHA256 shared secret. Returned in plaintext exactly once by whoever calls
    // this; only the encrypted form is stored.
    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
