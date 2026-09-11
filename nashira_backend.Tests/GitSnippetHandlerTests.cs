using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// The `git` workflow node. What it must get right is not the git part — IGitService
// owns that and is shared with the REST controller and the agent tools — but the
// translation: a bad config has to fail as bad_input rather than reaching git, and a
// git-level rejection has to fail the step rather than passing a false success down
// the DAG to a node that then commits on top of it.
public class GitSnippetHandlerTests
{
    private static readonly Guid RepoId = Guid.NewGuid();

    private sealed class FakeGitService : IGitService
    {
        public GitOpResult NextOp { get; set; } = new() { Ok = true, Message = "done", CommitSha = "abc1234" };
        public Exception? Throw { get; set; }
        public GitWriteFileRequest? LastWrite { get; private set; }
        public GitCommitRequest? LastCommit { get; private set; }
        public string? LastBranch { get; private set; }

        private T Guard<T>(T value) => Throw is not null ? throw Throw : value;

        public Task<GitReadFileResponse> ReadFileAsync(Guid id, string path, string? @ref, CancellationToken ct) =>
            Task.FromResult(Guard(new GitReadFileResponse
            {
                Path = path,
                Ref = @ref ?? "main",
                Content = "hello",
                Size = 5,
                IsBinary = false,
            }));

        public Task<GitOpResult> WriteFileAsync(Guid id, GitWriteFileRequest req, CancellationToken ct)
        {
            LastWrite = req;
            return Task.FromResult(Guard(NextOp));
        }

        public Task<GitOpResult> CommitAsync(Guid id, GitCommitRequest req, CancellationToken ct)
        {
            LastCommit = req;
            return Task.FromResult(Guard(NextOp));
        }

        public Task<GitOpResult> PullAsync(Guid id, string? branch, CancellationToken ct)
        {
            LastBranch = branch;
            return Task.FromResult(Guard(NextOp));
        }

        public Task<GitOpResult> PushAsync(Guid id, string? branch, CancellationToken ct)
        {
            LastBranch = branch;
            return Task.FromResult(Guard(NextOp));
        }

        // Which repository the handler resolved the node's `repository` /
        // `repository_id` to. The whole point of the name alias is that it lands on
        // the right row.
        public Guid? LastRepoId { get; private set; }

        public Task<GitStatusResponse> StatusAsync(Guid id, CancellationToken ct)
        {
            LastRepoId = id;
            return Task.FromResult(Guard(new GitStatusResponse { Branch = "main", Clean = true, Ahead = 0, Behind = 2 }));
        }

        public Task<GitDiffResponse> DiffAsync(Guid id, string? from, string? to, string? path, CancellationToken ct) =>
            Task.FromResult(Guard(new GitDiffResponse { From = "HEAD", To = "worktree", Patch = "" }));

        public Task<GitListFilesResponse> ListFilesAsync(Guid id, string? path, string? @ref, CancellationToken ct) =>
            Task.FromResult(Guard(new GitListFilesResponse
            {
                Ref = @ref ?? "main",
                Path = path ?? "",
                Entries = [new GitFileEntry { Path = "README.md", Type = "blob", Size = 12 }],
            }));

        // Not reachable from a workflow node — the node operates on a repository that
        // is already registered.
        public Task<ListResponse<GitRepositoryResponse>> ListAsync(int l, int o, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitRepositoryResponse> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitRepositoryResponse> CreateAsync(CreateGitRepository dto, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitRepositoryResponse> UpdateAsync(Guid id, UpdateGitRepository dto, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitRepositoryResponse> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitBranchesResponse> ListBranchesAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitOpResult> CheckoutAsync(Guid id, string branch, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (GitSnippetHandler Handler, FakeGitService Git) New()
    {
        var (handler, git, _) = NewWithDb();
        return (handler, git);
    }

    // The db is only reachable when a test needs to register a repository row —
    // which the `repository` (name) form of the node does, and the `repository_id`
    // (uuid) form does not.
    private static (GitSnippetHandler Handler, FakeGitService Git, nashira_backend.Data.Db.AppDbContext Db) NewWithDb()
    {
        var git = new FakeGitService();
        var db = new nashira_backend.Data.Db.AppDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<nashira_backend.Data.Db.AppDbContext>()
                .UseInMemoryDatabase($"git-snippet-{Guid.NewGuid()}").Options);
        return (new GitSnippetHandler(git, db, NullLogger<GitSnippetHandler>.Instance), git, db);
    }

    private static SnippetRequest Request(string json) => new()
    {
        NodeId = "n1",
        WorkflowId = Guid.NewGuid(),
        SnippetId = Guid.NewGuid(),
        SnippetType = "git",
        Input = JsonDocument.Parse(json).RootElement.Clone(),
    };

    private static string Config(string operation, string extra = "") =>
        $$"""{"operation":"{{operation}}","repository_id":"{{RepoId}}"{{extra}}}""";

    [Fact]
    public void The_handler_answers_to_the_snippet_type_the_seeder_writes()
    {
        var (handler, _) = New();
        Assert.Equal(nashira_backend.Data.Models.Snippet.TypeGit, handler.Type);
    }

    // Not NonReversible, and the distinction is load-bearing: NonReversible cannot be
    // declared away, so every read_file node would then claim it can never be undone
    // and poison its workflow's rollback plan.
    [Fact]
    public void The_default_tier_leaves_room_for_a_read_to_declare_itself_idempotent()
    {
        var (handler, _) = New();
        Assert.Equal(IdempotencyKind.RequiresCompensation, handler.DefaultIdempotency);
        Assert.True(Idempotency.IsReversible(handler.DefaultIdempotency));
        Assert.Equal(IdempotencyKind.Idempotent, Idempotency.Effective(
            new nashira_backend.Data.Models.Snippet { Idempotency = "idempotent" }, handler.DefaultIdempotency));
    }

    [Fact]
    public async Task Read_file_returns_the_content_a_downstream_node_addresses()
    {
        var (handler, _) = New();

        var result = await handler.ExecuteAsync(Request(Config("read_file", ",\"path\":\"README.md\"")), default);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Output.GetProperty("content").GetString());
        Assert.False(result.Output.GetProperty("is_binary").GetBoolean());
        // A read changed nothing, and the rollback plan reads this.
        Assert.Equal(StepChange.Unchanged, result.Change);
    }

    [Fact]
    public async Task Write_file_passes_the_commit_message_and_push_flag_through()
    {
        var (handler, git) = New();

        var result = await handler.ExecuteAsync(Request(Config("write_file",
            ",\"path\":\"report.md\",\"content\":\"# report\",\"commit_message\":\"auto: nightly\",\"push\":true")), default);

        Assert.True(result.Success);
        Assert.Equal("report.md", git.LastWrite!.Path);
        Assert.Equal("auto: nightly", git.LastWrite.CommitMessage);
        Assert.True(git.LastWrite.Push);
        Assert.Equal("abc1234", result.Output.GetProperty("commit_sha").GetString());
        Assert.Equal(StepChange.Changed, result.Change);
    }

    // A push rejected by the remote comes back as Ok=false rather than an exception.
    // Reporting it as success is the dangerous failure: the next node commits on top
    // of a tree the remote never accepted.
    [Fact]
    public async Task A_rejected_push_fails_the_step()
    {
        var (handler, git) = New();
        git.NextOp = new GitOpResult { Ok = false, Message = "updates were rejected (non-fast-forward)" };

        var result = await handler.ExecuteAsync(Request(Config("push")), default);

        Assert.False(result.Success);
        Assert.Equal("git_rejected", result.ErrorCode);
        Assert.Contains("non-fast-forward", result.Error);
    }

    // Nothing to commit is not a failure, but it is not a change either.
    [Fact]
    public async Task A_commit_that_produced_no_sha_reports_no_change()
    {
        var (handler, git) = New();
        git.NextOp = new GitOpResult { Ok = true, Message = "nothing to commit", CommitSha = null };

        var result = await handler.ExecuteAsync(Request(Config("commit", ",\"commit_message\":\"noop\"")), default);

        Assert.True(result.Success);
        Assert.Equal(StepChange.Unchanged, result.Change);
    }

    [Fact]
    public async Task Commit_paths_narrow_what_is_staged()
    {
        var (handler, git) = New();

        await handler.ExecuteAsync(Request(Config("commit",
            ",\"commit_message\":\"partial\",\"paths\":[\"a.txt\",\"b.txt\"]")), default);

        Assert.Equal(["a.txt", "b.txt"], git.LastCommit!.Paths);
    }

    // Omitted means "stage everything", which is IGitService's contract. An empty
    // list must not arrive as "stage these zero files".
    [Fact]
    public async Task Omitted_paths_stay_null_rather_than_becoming_an_empty_list()
    {
        var (handler, git) = New();

        await handler.ExecuteAsync(Request(Config("commit", ",\"commit_message\":\"all\"")), default);

        Assert.Null(git.LastCommit!.Paths);
    }

    [Fact]
    public async Task Diff_answers_the_question_a_condition_edge_asks()
    {
        var (handler, _) = New();

        var result = await handler.ExecuteAsync(Request(Config("diff")), default);

        Assert.True(result.Success);
        Assert.False(result.Output.GetProperty("has_changes").GetBoolean());
    }

    [Fact]
    public async Task Status_exposes_the_upstream_gap()
    {
        var (handler, _) = New();

        var result = await handler.ExecuteAsync(Request(Config("status")), default);

        Assert.True(result.Output.GetProperty("clean").GetBoolean());
        Assert.Equal(2, result.Output.GetProperty("behind").GetInt32());
    }

    [Theory]
    [InlineData("""{"operation":"read_file"}""")]                      // no repository_id
    [InlineData("""{"operation":"read_file","repository_id":"nope"}""")] // not a uuid
    [InlineData("""{"repository_id":"11111111-1111-1111-1111-111111111111"}""")] // no operation
    public async Task A_config_that_cannot_name_its_target_fails_before_reaching_git(string json)
    {
        var (handler, _) = New();

        var result = await handler.ExecuteAsync(Request(json), default);

        Assert.False(result.Success);
        Assert.Equal("bad_input", result.ErrorCode);
    }

    [Fact]
    public async Task An_unknown_operation_lists_the_ones_that_exist()
    {
        var (handler, _) = New();

        var result = await handler.ExecuteAsync(Request(Config("force_push")), default);

        Assert.False(result.Success);
        Assert.Contains("read_file", result.Error);
        Assert.Contains("write_file", result.Error);
    }

    [Theory]
    [InlineData("read_file", "path")]
    [InlineData("write_file", "path")]
    [InlineData("commit", "commit_message")]
    public async Task Each_operation_names_the_field_it_is_missing(string operation, string field)
    {
        var (handler, _) = New();

        var result = await handler.ExecuteAsync(Request(Config(operation)), default);

        Assert.False(result.Success);
        Assert.Contains(field, result.Error);
    }

    // A repository that does not exist will not exist on the retry either; saying so
    // stops the retry policy burning three attempts on it.
    [Fact]
    public async Task A_missing_repository_is_not_retryable()
    {
        var (handler, git) = New();
        git.Throw = new NotFoundException("repository not found");

        var result = await handler.ExecuteAsync(Request(Config("pull")), default);

        Assert.False(result.Success);
        Assert.Equal("not_found", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    // A network or lock failure can clear on its own, and that is the branch retries
    // are for.
    [Fact]
    public async Task A_transport_failure_is_retryable()
    {
        var (handler, git) = New();
        git.Throw = new InvalidOperationException("failed to connect to github.com");

        var result = await handler.ExecuteAsync(Request(Config("pull")), default);

        Assert.False(result.Success);
        Assert.Equal("git_error", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task A_non_object_config_is_refused_rather_than_indexed_into()
    {
        var (handler, _) = New();

        var result = await handler.ExecuteAsync(Request("\"just a string\""), default);

        Assert.False(result.Success);
        Assert.Equal("bad_input", result.ErrorCode);
    }

    // workflow.v1 snippets/SPEC.md: identity keys are NAMES, not ids. A bundle
    // carries `repository: "network-configs"` because a uuid from the exporting
    // instance means nothing here; `repository_id` stays an accepted alias for the
    // nodes Nashira already wrote.
    [Fact]
    public async Task A_repository_name_resolves_to_the_registered_repository()
    {
        var (handler, git, db) = NewWithDb();
        var repo = new nashira_backend.Data.Models.GitRepository
        {
            GitRepositoryId = Guid.NewGuid(),
            Name = "network-configs",
            Url = "https://git.example.test/net/configs.git",
            IsActive = true,
        };
        db.GitRepositories.Add(repo);
        await db.SaveChangesAsync();

        var byName = await handler.ExecuteAsync(
            Request("""{"operation":"status","repository":"network-configs"}"""), default);

        Assert.True(byName.Success);
        Assert.Equal(repo.GitRepositoryId, git.LastRepoId);

        // The uuid form still works, through either key.
        var byIdKey = await handler.ExecuteAsync(
            Request($$"""{"operation":"status","repository_id":"{{repo.GitRepositoryId}}"}"""), default);
        Assert.True(byIdKey.Success);
        Assert.Equal(repo.GitRepositoryId, git.LastRepoId);

        var byNameKeyHoldingAnId = await handler.ExecuteAsync(
            Request($$"""{"operation":"status","repository":"{{repo.GitRepositoryId}}"}"""), default);
        Assert.True(byNameKeyHoldingAnId.Success);
        Assert.Equal(repo.GitRepositoryId, git.LastRepoId);

        db.Dispose();
    }

    [Fact]
    public async Task A_repository_name_that_is_not_registered_is_not_found()
    {
        var (handler, git, db) = NewWithDb();

        var unknown = await handler.ExecuteAsync(
            Request("""{"operation":"status","repository":"ghost"}"""), default);
        var neither = await handler.ExecuteAsync(Request("""{"operation":"status"}"""), default);

        Assert.False(unknown.Success);
        Assert.Equal("not_found", unknown.ErrorCode);
        Assert.False(neither.Success);
        Assert.Equal("bad_input", neither.ErrorCode);
        Assert.Null(git.LastRepoId);

        db.Dispose();
    }
}
