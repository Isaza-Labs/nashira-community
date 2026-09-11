using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using nashira_backend.Data.Models;
using nashira_backend.Services.Git;

namespace nashira_backend.Tests;

// The inbound side of a git webhook is authenticated by a shared secret and nothing
// else — the caller is GitHub, which cannot hold a session. That makes these three
// pure pieces the whole security boundary, so they are pinned here rather than left
// to be exercised only by a live delivery nobody can reproduce.
public class GitWebhookSignatureTests
{
    private const string Secret = "s3cr3t-shared-with-github";

    private static byte[] Body(string s) => Encoding.UTF8.GetBytes(s);

    private static string Sign(string body, string secret = Secret) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Body(body))).ToLowerInvariant();

    [Fact]
    public void Github_accepts_a_signature_over_the_exact_body()
    {
        var body = """{"ref":"refs/heads/main"}""";
        Assert.True(GitWebhookSignature.Verify(
            GitWebhook.ProviderGithub, Body(body), Sign(body), Secret));
    }

    // GitHub documents lowercase hex, but the header is echoed through proxies and
    // test tooling that upper-case it. The digest is the same either way.
    [Fact]
    public void Github_accepts_the_digest_in_either_case()
    {
        var body = """{"ref":"refs/heads/main"}""";
        Assert.True(GitWebhookSignature.Verify(
            GitWebhook.ProviderGithub, Body(body), Sign(body).ToUpperInvariant(), Secret));
    }

    // The whole point: a body that was modified in flight no longer matches.
    [Fact]
    public void Github_rejects_a_signature_computed_over_a_different_body()
    {
        var signed = Sign("""{"ref":"refs/heads/main"}""");
        Assert.False(GitWebhookSignature.Verify(
            GitWebhook.ProviderGithub, Body("""{"ref":"refs/heads/production"}"""), signed, Secret));
    }

    [Fact]
    public void Github_rejects_a_signature_made_with_another_secret()
    {
        var body = """{"ref":"refs/heads/main"}""";
        Assert.False(GitWebhookSignature.Verify(
            GitWebhook.ProviderGithub, Body(body), Sign(body, "not-the-secret"), Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("deadbeef")]                                     // right shape, wrong length
    [InlineData("sha1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // a scheme we do not accept
    public void Github_rejects_a_malformed_header(string? header) =>
        Assert.False(GitWebhookSignature.Verify(
            GitWebhook.ProviderGithub, Body("{}"), header, Secret));

    [Fact]
    public void Gitlab_compares_the_token_itself()
    {
        Assert.True(GitWebhookSignature.Verify(GitWebhook.ProviderGitlab, Body("{}"), Secret, Secret));
        Assert.False(GitWebhookSignature.Verify(GitWebhook.ProviderGitlab, Body("{}"), Secret + "x", Secret));
        Assert.False(GitWebhookSignature.Verify(GitWebhook.ProviderGitlab, Body("{}"), null, Secret));
    }

    // A GitLab token in a GitHub-shaped hook must not verify: the provider decides
    // the scheme, and accepting whichever one happens to match would make the
    // strongest configured scheme optional.
    [Fact]
    public void The_provider_decides_the_scheme_and_the_other_one_does_not_pass()
    {
        Assert.False(GitWebhookSignature.Verify(GitWebhook.ProviderGithub, Body("{}"), Secret, Secret));
        Assert.False(GitWebhookSignature.Verify(
            GitWebhook.ProviderGitlab, Body("{}"), Sign("{}"), Secret));
    }

    [Fact]
    public void An_unknown_provider_verifies_nothing()
    {
        var body = """{"ref":"refs/heads/main"}""";
        Assert.False(GitWebhookSignature.Verify("bitbucket", Body(body), Sign(body), Secret));
    }

    // An empty secret must never verify. Whether an unsigned hook is allowed at all is
    // the receiver's decision (AllowUnsigned) and must not leak into the comparison.
    [Fact]
    public void An_empty_secret_never_verifies()
    {
        Assert.False(GitWebhookSignature.Verify(GitWebhook.ProviderGithub, Body("{}"), Sign("{}", ""), ""));
        Assert.False(GitWebhookSignature.Verify(GitWebhook.ProviderGitlab, Body("{}"), "", ""));
    }

    [Theory]
    [InlineData(GitWebhook.ProviderGithub, "X-Hub-Signature-256")]
    [InlineData(GitWebhook.ProviderGitlab, "X-Gitlab-Token")]
    [InlineData(GitWebhook.ProviderGeneric, "X-Nashira-Signature")]
    public void The_header_named_to_the_operator_is_the_one_that_is_read(string provider, string expected) =>
        Assert.Equal(expected, GitWebhookSignature.HeaderFor(provider));
}

public class GitWebhookPayloadTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Github_push_yields_branch_and_head_commit()
    {
        var parsed = GitWebhookPayload.Parse(GitWebhook.ProviderGithub, "push", B("""
            {"ref":"refs/heads/main","head_commit":{"id":"abc123"}}
            """));

        Assert.True(parsed.IsPush);
        Assert.Equal("main", parsed.Branch);
        Assert.Equal("abc123", parsed.CommitSha);
    }

    // GitLab puts the kind in the body and the sha under a different key, and its
    // event header reads "Push Hook" rather than "push".
    [Fact]
    public void Gitlab_push_is_recognised_from_its_own_shape()
    {
        var parsed = GitWebhookPayload.Parse(GitWebhook.ProviderGitlab, "Push Hook", B("""
            {"object_kind":"push","ref":"refs/heads/release/1.2","checkout_sha":"def456"}
            """));

        Assert.True(parsed.IsPush);
        Assert.Equal("release/1.2", parsed.Branch);
        Assert.Equal("def456", parsed.CommitSha);
    }

    [Fact]
    public void Object_kind_supplies_the_event_when_no_header_does()
    {
        var parsed = GitWebhookPayload.Parse(GitWebhook.ProviderGitlab, null, B("""
            {"object_kind":"push","ref":"refs/heads/main"}
            """));

        Assert.True(parsed.IsPush);
    }

    // A tag push carries refs/tags/…, which is not a branch. Leaving Branch null is
    // what makes a branch filter skip it instead of matching on a tag name.
    [Fact]
    public void A_tag_push_has_no_branch()
    {
        var parsed = GitWebhookPayload.Parse(GitWebhook.ProviderGithub, "push", B("""
            {"ref":"refs/tags/v1.0.0","head_commit":{"id":"abc123"}}
            """));

        Assert.Null(parsed.Branch);
    }

    // A branch deletion reports an all-zero sha. Passing that to a workflow as a
    // commit would send it looking up an object that does not exist.
    [Fact]
    public void A_deleted_branch_reports_no_commit_rather_than_the_zero_sha()
    {
        var parsed = GitWebhookPayload.Parse(GitWebhook.ProviderGithub, "push", B("""
            {"ref":"refs/heads/gone","after":"0000000000000000000000000000000000000000"}
            """));

        Assert.Null(parsed.CommitSha);
    }

    [Fact]
    public void Ping_is_not_a_push()
    {
        var parsed = GitWebhookPayload.Parse(GitWebhook.ProviderGithub, "ping", B("""{"zen":"Speak like a human."}"""));

        Assert.False(parsed.IsPush);
        Assert.Equal("ping", parsed.Event);
    }

    // The generic provider has no event header to send, so a body with a ref is taken
    // as a push. Without this, a custom emitter would always be acknowledged and never
    // act.
    [Fact]
    public void A_generic_emitter_needs_no_event_header()
    {
        var parsed = GitWebhookPayload.Parse(GitWebhook.ProviderGeneric, null, B("""
            {"ref":"refs/heads/main","after":"abc123"}
            """));

        Assert.True(parsed.IsPush);
        Assert.Equal("main", parsed.Branch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")] // valid JSON, wrong shape
    public void A_body_that_cannot_be_read_does_not_throw(string body)
    {
        var parsed = GitWebhookPayload.Parse(GitWebhook.ProviderGithub, "push", B(body));

        Assert.True(parsed.IsPush); // the header still said what it said
        Assert.Null(parsed.Branch);
        Assert.Null(parsed.CommitSha);
    }
}

public class GitWebhookDispatchRulesTests
{
    private static GitWebhook Hook(bool enabled = true, params string[] branches) => new()
    {
        GitWebhookId = Guid.NewGuid(),
        GitRepositoryId = Guid.NewGuid(),
        Provider = GitWebhook.ProviderGithub,
        Enabled = enabled,
        OnPushBranches = branches.ToList(),
    };

    [Fact]
    public void An_empty_filter_means_every_branch()
    {
        Assert.Null(GitWebhookDispatchRules.Refusal(Hook(), "anything"));
        Assert.Null(GitWebhookDispatchRules.Refusal(Hook(), null));
    }

    [Fact]
    public void A_listed_branch_dispatches()
        => Assert.Null(GitWebhookDispatchRules.Refusal(Hook(true, "main", "release"), "main"));

    // The refusal is read by an operator in the deliveries list, so it has to name
    // both what arrived and what the hook is configured for — "filtered out" alone
    // sends them to the database to find out why.
    [Fact]
    public void An_unlisted_branch_is_refused_and_says_what_the_filter_is()
    {
        var refusal = GitWebhookDispatchRules.Refusal(Hook(true, "main"), "feature/x");

        Assert.NotNull(refusal);
        Assert.Contains("feature/x", refusal);
        Assert.Contains("main", refusal);
    }

    [Fact]
    public void Branch_matching_is_exact_rather_than_a_prefix()
    {
        Assert.NotNull(GitWebhookDispatchRules.Refusal(Hook(true, "main"), "maintenance"));
        Assert.NotNull(GitWebhookDispatchRules.Refusal(Hook(true, "main"), "Main"));
    }

    // A tag push reaches a filtered hook with no branch. Treating "no branch" as a
    // match would fire a main-only workflow on every tag.
    [Fact]
    public void A_push_with_no_branch_does_not_satisfy_a_filter()
        => Assert.NotNull(GitWebhookDispatchRules.Refusal(Hook(true, "main"), null));

    [Fact]
    public void A_disabled_webhook_refuses_before_anything_else()
    {
        var refusal = GitWebhookDispatchRules.Refusal(Hook(enabled: false), "main");

        Assert.NotNull(refusal);
        Assert.Contains("disabled", refusal);
    }

    // The run input is a contract: a workflow reads {{ input.branch }} and
    // {{ input.commit_sha }}, so renaming either silently breaks every workflow a
    // webhook drives.
    [Fact]
    public void The_run_input_carries_the_fields_a_workflow_branches_on()
    {
        var hook = Hook();
        var input = GitWebhookDispatchRules.RunInput(hook, "main", "abc123");

        Assert.Equal(hook.GitWebhookId, input.GetProperty("git_webhook_id").GetGuid());
        Assert.Equal(hook.GitRepositoryId, input.GetProperty("repository_id").GetGuid());
        Assert.Equal("github", input.GetProperty("provider").GetString());
        Assert.Equal("main", input.GetProperty("branch").GetString());
        Assert.Equal("abc123", input.GetProperty("commit_sha").GetString());
    }

    [Fact]
    public void The_run_input_keeps_absent_fields_as_null_rather_than_dropping_them()
    {
        var input = GitWebhookDispatchRules.RunInput(Hook(), null, null);

        Assert.Equal(JsonValueKind.Null, input.GetProperty("branch").ValueKind);
        Assert.Equal(JsonValueKind.Null, input.GetProperty("commit_sha").ValueKind);
    }
}
