using System.Security.Cryptography;
using System.Text;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Git;

// Provider-specific signature verification for inbound git webhooks.
//
// Nashira's own webhook trigger already verifies HMAC over the raw body, but it
// reads X-Nashira-Signature. GitHub sends X-Hub-Signature-256 and GitLab sends a
// plain token in X-Gitlab-Token, so pointing a GitHub webhook at that endpoint
// authenticates nothing — the header it signs with is never looked at. This is the
// piece that makes a real provider's delivery verifiable.
//
// Every comparison is constant-time. A byte-by-byte compare on a signature leaks,
// one early return at a time, what the signature should have been.
public static class GitWebhookSignature
{
    // The header an operator must configure on the provider side, per provider.
    public const string GithubHeader = "X-Hub-Signature-256";
    public const string GitlabHeader = "X-Gitlab-Token";
    public const string GenericHeader = "X-Nashira-Signature";

    public static string HeaderFor(string provider) => provider switch
    {
        GitWebhook.ProviderGitlab => GitlabHeader,
        GitWebhook.ProviderGeneric => GenericHeader,
        _ => GithubHeader,
    };

    public static bool Verify(string provider, byte[] body, string? header, string secret) => provider switch
    {
        GitWebhook.ProviderGithub => VerifyHmacSha256(body, header, secret),
        GitWebhook.ProviderGitlab => VerifyToken(header, secret),
        GitWebhook.ProviderGeneric => VerifyHmacSha256(body, header, secret),
        // An unknown provider verifies nothing rather than defaulting to a scheme
        // the sender may not be using.
        _ => false,
    };

    // GitHub and the generic scheme: header is "sha256=<hex>" over HMAC-SHA256(secret, body).
    // https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries
    public static bool VerifyHmacSha256(byte[] body, string? header, string secret)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret)) return false;

        var provided = header.Trim();
        const string prefix = "sha256=";
        if (provided.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            provided = provided[prefix.Length..];
        if (provided.Length != 64) return false;

        var expected = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        return FixedTimeEquals(expected, provided.ToLowerInvariant());
    }

    // GitLab: the header carries the secret itself, no HMAC.
    // https://docs.gitlab.com/ee/user/project/integrations/webhooks.html
    public static bool VerifyToken(string? header, string secret)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret)) return false;
        return FixedTimeEquals(header, secret);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        // Length is not the secret, and FixedTimeEquals requires equal spans.
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }
}
