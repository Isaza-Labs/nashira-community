using System.Text.Json;
using HookEntity = nashira_backend.Data.Models.GitWebhook;

namespace nashira_backend.Services.Git;

// The decision a delivery turns on: given this webhook and this branch, does anything
// run? Pure, and shared by the receiver and by the dry-run endpoint on purpose — a
// "test this webhook" button that answers from a second copy of the rules is worse
// than no button, because it is trusted and can be wrong.
public static class GitWebhookDispatchRules
{
    // Null means "dispatch". Anything else is the reason nothing will happen, phrased
    // for an operator reading it in the deliveries list.
    public static string? Refusal(HookEntity hook, string? branch)
    {
        if (!hook.Enabled) return "the webhook is disabled";

        if (hook.OnPushBranches.Count == 0) return null;

        if (string.IsNullOrEmpty(branch))
            return $"the push carries no branch (a tag push, or a payload without a refs/heads ref) "
                   + $"and this webhook only fires for: {string.Join(", ", hook.OnPushBranches)}";

        return hook.OnPushBranches.Contains(branch, StringComparer.Ordinal)
            ? null
            : $"branch '{branch}' is not in the filter ({string.Join(", ", hook.OnPushBranches)})";
    }

    // What the triggered run receives as its `input`. Deliberately flat and small: a
    // workflow branches on the branch name and reads the commit, and handing it the
    // provider's whole payload would make every workflow that touches it
    // provider-specific.
    public static JsonElement RunInput(HookEntity hook, string? branch, string? commitSha) =>
        JsonSerializer.SerializeToElement(new
        {
            git_webhook_id = hook.GitWebhookId,
            repository_id = hook.GitRepositoryId,
            provider = hook.Provider,
            branch,
            commit_sha = commitSha,
        });
}
