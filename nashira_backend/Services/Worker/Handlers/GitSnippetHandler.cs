using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// Reads and writes a registered Git repository from inside a workflow run.
//
// Without this, a workflow that needed Git had to bounce back to the chat and ask a
// person to do the commit — which is not automation, and is exactly what the agent
// kept telling users when they asked for "pull the config every night and commit the
// report back". The engine already had every other side of it: a scheduler to fire
// the run, a git service to do the work, and a webhook to start it on a push.
//
// It is a thin adapter over IGitService on purpose. There is one Git implementation
// and three surfaces onto it — the REST controller, the agent's git_* tools, and this
// — so a rule about what a commit does cannot be true in one of them and false in
// another.
//
// config_overrides shape (after variable resolution):
//   {
//     "operation":      "read_file" | "write_file" | "commit" | "pull" | "push"
//                       | "list_files" | "status" | "diff",
//     "repository_id":  "<uuid>",             // required
//     "path":           "configs/r1.cfg",     // read_file / write_file / list_files / diff
//     "ref":            "main",               // read_file / list_files
//     "content":        "...",                // write_file
//     "commit_message": "auto: nightly",      // write_file / commit
//     "branch":         "main",               // write_file / commit / pull / push
//     "push":           true,                 // write_file / commit
//     "paths":          ["a", "b"],           // commit — omit to stage everything
//     "author_name":    "nashira",            // any committing operation
//     "author_email":   "ops@example.com",
//     "from":           "HEAD~1", "to": "HEAD" // diff
//   }
public sealed class GitSnippetHandler : ISnippetHandler
{
    private static readonly string[] Operations =
        ["read_file", "write_file", "commit", "pull", "push", "list_files", "status", "diff"];

    private readonly IGitService _git;
    private readonly AppDbContext _db;
    private readonly ILogger<GitSnippetHandler> _logger;

    public GitSnippetHandler(IGitService git, AppDbContext db, ILogger<GitSnippetHandler> logger)
    {
        _git = git;
        _db = db;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypeGit;

    // RequiresCompensation, not NonReversible, and the difference matters. A commit
    // that has not been pushed is local and a later step can reset it; a push is
    // undone by a revert commit, which IS a compensation the author can wire on the
    // failure edge. Declaring the whole type NonReversible would be absolute and
    // unrepealable — every read_file node would then poison its workflow's rollback
    // plan with a step that claims it can never be undone.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var input = request.Input;
        if (input.ValueKind != JsonValueKind.Object)
            return SnippetResult.Fail("a git step needs a config object (operation + repository_id)", "bad_input");

        var operation = (Str(input, "operation") ?? string.Empty).Trim().ToLowerInvariant();
        if (!Operations.Contains(operation))
            return SnippetResult.Fail(
                $"unknown git operation '{operation}'; expected one of: {string.Join(", ", Operations)}",
                "bad_input");

        // `repository` (the contract's key) names the repository; `repository_id`
        // (Nashira's) is its uuid. Either identifies a registered repository.
        Guid repoId;
        if (Str(input, "repository")?.Trim() is { Length: > 0 } repoName)
        {
            var repo = Guid.TryParse(repoName, out var asId)
                ? await _db.GitRepositories.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.IsActive && r.GitRepositoryId == asId, ct)
                : await _db.GitRepositories.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.IsActive && r.Name == repoName, ct);
            if (repo is null)
                return SnippetResult.Fail($"no registered git repository named '{repoName}'", "not_found");
            repoId = repo.GitRepositoryId;
        }
        else if (Str(input, "repository_id")?.Trim() is { Length: > 0 } rawId && Guid.TryParse(rawId, out repoId))
        {
            // The uuid form: the git service reports an unknown id itself.
        }
        else
        {
            return SnippetResult.Fail(
                "`repository` (name) or `repository_id` (uuid) is required and must identify a registered repository",
                "bad_input");
        }

        _logger.LogInformation(
            "workflow.git.start node={NodeId} repo_id={RepoId} operation={Operation}",
            request.NodeId, repoId, operation);

        try
        {
            return operation switch
            {
                "read_file" => await ReadFileAsync(repoId, input, ct),
                "write_file" => await WriteFileAsync(repoId, input, ct),
                "commit" => await CommitAsync(repoId, input, ct),
                "pull" => Op("pull", await _git.PullAsync(repoId, Str(input, "branch"), ct)),
                "push" => Op("push", await _git.PushAsync(repoId, Str(input, "branch"), ct)),
                "list_files" => await ListFilesAsync(repoId, input, ct),
                "status" => await StatusAsync(repoId, ct),
                "diff" => await DiffAsync(repoId, input, ct),
                _ => SnippetResult.Fail($"unsupported operation '{operation}'", "bad_input"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotFoundException ex)
        {
            // Not retryable: a repository that does not exist will not exist on the
            // second attempt either, and retrying only fails more slowly.
            return SnippetResult.Fail(ex.Message, "not_found");
        }
        catch (DomainException ex)
        {
            return SnippetResult.Fail(ex.Message, "bad_input");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow.git.failed node={NodeId} repo_id={RepoId} operation={Operation}",
                request.NodeId, repoId, operation);
            // Network and lock failures are the plausible ones here, and both can
            // clear on their own — so this is the branch that is worth retrying.
            return SnippetResult.Fail($"git {operation} failed: {ex.Message}", "git_error", retryable: true);
        }
    }

    // ─── operations ─────────────────────────────────────────────────

    private async Task<SnippetResult> ReadFileAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        if (Str(input, "path")?.Trim() is not { Length: > 0 } path)
            return SnippetResult.Fail("read_file needs a `path`", "bad_input");

        var file = await _git.ReadFileAsync(repoId, path, Str(input, "ref"), ct);
        return SnippetResult.Ok(new
        {
            path = file.Path,
            @ref = file.Ref,
            content = file.Content,
            size = file.Size,
            is_binary = file.IsBinary,
        }, StepChange.Unchanged, logs: $"read {file.Path}@{file.Ref} ({file.Size} bytes{(file.IsBinary ? ", binary" : "")})");
    }

    private async Task<SnippetResult> WriteFileAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        if (Str(input, "path")?.Trim() is not { Length: > 0 } path)
            return SnippetResult.Fail("write_file needs a `path`", "bad_input");
        if (Str(input, "commit_message")?.Trim() is not { Length: > 0 } message)
            return SnippetResult.Fail("write_file needs a `commit_message`", "bad_input");

        // The whole file is replaced by `content` — same contract as git_write_file.
        // A step that means to append has to read, concatenate and write the result.
        var result = await _git.WriteFileAsync(repoId, new GitWriteFileRequest
        {
            Path = path,
            Content = Str(input, "content") ?? string.Empty,
            CommitMessage = message,
            Branch = Str(input, "branch"),
            Push = Bool(input, "push"),
            AuthorName = Str(input, "author_name"),
            AuthorEmail = Str(input, "author_email"),
        }, ct);
        return Op("write_file", result);
    }

    private async Task<SnippetResult> CommitAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        if (Str(input, "commit_message")?.Trim() is not { Length: > 0 } message)
            return SnippetResult.Fail("commit needs a `commit_message`", "bad_input");

        var result = await _git.CommitAsync(repoId, new GitCommitRequest
        {
            CommitMessage = message,
            Paths = Strings(input, "paths"),
            Push = Bool(input, "push"),
            AuthorName = Str(input, "author_name"),
            AuthorEmail = Str(input, "author_email"),
        }, ct);
        return Op("commit", result);
    }

    private async Task<SnippetResult> ListFilesAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        var listing = await _git.ListFilesAsync(repoId, Str(input, "path"), Str(input, "ref"), ct);
        return SnippetResult.Ok(new
        {
            @ref = listing.Ref,
            path = listing.Path,
            entries = listing.Entries.Select(e => new { path = e.Path, type = e.Type, size = e.Size }),
            count = listing.Entries.Count,
        }, StepChange.Unchanged,
        logs: $"listed {listing.Entries.Count} entr{(listing.Entries.Count == 1 ? "y" : "ies")} "
              + $"under '{(string.IsNullOrEmpty(listing.Path) ? "/" : listing.Path)}' at {listing.Ref}");
    }

    private async Task<SnippetResult> StatusAsync(Guid repoId, CancellationToken ct)
    {
        var status = await _git.StatusAsync(repoId, ct);
        return SnippetResult.Ok(new
        {
            branch = status.Branch,
            clean = status.Clean,
            staged = status.Staged,
            modified = status.Modified,
            untracked = status.Untracked,
            missing = status.Missing,
            ahead = status.Ahead,
            behind = status.Behind,
        }, StepChange.Unchanged,
        logs: $"{status.Branch}: {(status.Clean ? "clean" : $"{status.Staged.Count} staged, "
              + $"{status.Modified.Count} modified, {status.Untracked.Count} untracked")}"
              + $"{(status.Ahead is > 0 ? $", {status.Ahead} ahead" : "")}"
              + $"{(status.Behind is > 0 ? $", {status.Behind} behind" : "")}");
    }

    private async Task<SnippetResult> DiffAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        var diff = await _git.DiffAsync(repoId, Str(input, "from"), Str(input, "to"), Str(input, "path"), ct);
        return SnippetResult.Ok(new
        {
            from = diff.From,
            to = diff.To,
            path = diff.Path,
            patch = diff.Patch,
            // The question a condition edge actually asks. Computing it here keeps
            // every "did anything change" branch from re-implementing it against the
            // patch text.
            has_changes = !string.IsNullOrWhiteSpace(diff.Patch),
        }, StepChange.Unchanged,
        logs: string.IsNullOrWhiteSpace(diff.Patch)
            ? $"no differences between {diff.From} and {diff.To}"
            : $"{diff.From}..{diff.To}: {diff.Patch.Split(Environment.NewLine, StringSplitOptions.None).Length} line(s) of diff");
    }

    // ─── helpers ────────────────────────────────────────────────────

    // A git-level failure (push rejected, merge conflict) comes back as Ok=false
    // rather than an exception, and it has to fail the step: a workflow whose commit
    // was rejected must not carry on as though it landed.
    private static SnippetResult Op(string operation, GitOpResult result)
    {
        if (!result.Ok)
            return SnippetResult.Fail($"git {operation}: {result.Message}", "git_rejected");

        return new SnippetResult
        {
            Success = true,
            Output = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                operation,
                commit_sha = result.CommitSha,
                branch = result.Branch,
                message = result.Message,
            }),
            // A pull that fast-forwards nothing and a commit that produced no sha did
            // not change the repository, and the rollback plan reads this. Measured, not
            // declared: the sha is the evidence.
            Change = result.CommitSha is { Length: > 0 } ? StepChange.Changed : StepChange.Unchanged,
            Logs = $"{operation} on {result.Branch ?? "the checked-out branch"}: {result.Message}"
                   + (result.CommitSha is { Length: > 0 } sha ? $" ({sha[..Math.Min(7, sha.Length)]})" : ""),
        };
    }

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string>? Strings(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var items = v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        return items.Count > 0 ? items : null;
    }
}
