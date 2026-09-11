using System.Text.Json;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Git;

// What the receiver needs out of a push payload: which event, which branch, which
// commit. Nothing more is parsed on purpose — reading further would tie the receiver
// to one provider's schema, and none of the rest reaches the workflow anyway.
public readonly record struct ParsedGitPush(string? Event, string? Branch, string? CommitSha)
{
    // GitHub sends "push"; GitLab sends object_kind "push" and event header "Push Hook".
    public bool IsPush =>
        string.Equals(Event, "push", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Event, "Push Hook", StringComparison.OrdinalIgnoreCase);
}

public static class GitWebhookPayload
{
    // A branch deletion reports this as the new head; it is not a commit.
    private const string ZeroSha = "0000000000000000000000000000000000000000";

    public static ParsedGitPush Parse(string provider, string? eventHeader, byte[] body)
    {
        if (body.Length == 0) return new ParsedGitPush(eventHeader, null, null);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new ParsedGitPush(eventHeader, null, null); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ParsedGitPush(eventHeader, null, null);

            var evt = eventHeader;
            // GitLab does not send the kind in a header the way GitHub does.
            if (string.IsNullOrEmpty(evt) && Str(root, "object_kind") is { Length: > 0 } kind)
                evt = kind;
            // The generic provider has no event header at all; a body carrying a ref
            // is a push by construction.
            if (string.IsNullOrEmpty(evt) && provider == GitWebhook.ProviderGeneric
                && root.TryGetProperty("ref", out _))
                evt = "push";

            // "refs/heads/main" → "main". A tag push ("refs/tags/v1") has no branch,
            // and leaving it null is what makes the branch filter skip it.
            string? branch = null;
            if (Str(root, "ref") is { } rawRef)
            {
                const string prefix = "refs/heads/";
                if (rawRef.StartsWith(prefix, StringComparison.Ordinal))
                    branch = rawRef[prefix.Length..];
            }

            // GitHub: head_commit.id. GitLab: checkout_sha. Generic: after.
            string? sha = null;
            if (root.TryGetProperty("head_commit", out var head) && head.ValueKind == JsonValueKind.Object)
                sha = Str(head, "id");
            sha ??= Str(root, "checkout_sha") ?? Str(root, "after");
            if (sha == ZeroSha) sha = null;

            return new ParsedGitPush(evt, branch, sha);
        }
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
