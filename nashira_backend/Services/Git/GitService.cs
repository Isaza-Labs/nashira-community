using System.Collections.Concurrent;
using System.Text;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Security;
using nashira_backend.Services.Identity;
using GitRepoEntity = nashira_backend.Data.Models.GitRepository;
// LibGit2Sharp also exports NotFoundException — bind the bare name to ours.
using NotFoundException = nashira_backend.Exceptions.NotFoundException;
using nashira_backend.Services.Settings;

namespace nashira_backend.Services.Git;

// HTTPS+token implementation, adapted from flow-weaver. SSH-key transport is
// deferred: every clone/fetch/push uses the PAT stored in the linked Credential
// row (auth_method "token"; legacy rows kept the PAT in EncryptedPassword).
//
// Concurrency: a per-repository SemaphoreSlim serializes index mutations within
// the process. Multiple processes would still race the same checkout — production
// must pin Git work to a single replica until a queued worker exists.
public sealed class GitService : IGitService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretProtector _crypto;
    private readonly ILogger<GitService> _logger;
    private readonly string _gitRoot;
    private readonly long _maxFileBytes;
    private readonly string _defaultAuthorName;
    private readonly string _defaultAuthorEmail;

    public GitService(
        AppDbContext db,
        ICurrentUser user,
        ISecretProtector crypto,
        IConfiguration config,
        AppSettingsProvider settings,
        IHostEnvironment env,
        ILogger<GitService> logger)
    {
        _db = db;
        _user = user;
        _crypto = crypto;
        _logger = logger;
        var configured = config["Git:Root"] ?? "./data/git";
        _gitRoot = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        _maxFileBytes = settings.GetLong("Git:MaxFileBytes", 5 * 1024 * 1024);
        _defaultAuthorName = config["Git:DefaultAuthorName"] ?? "nashira";
        _defaultAuthorEmail = config["Git:DefaultAuthorEmail"] ?? "nashira@localhost";
    }

    // ─── CRUD ───────────────────────────────────────────────────────

    public async Task<ListResponse<GitRepositoryResponse>> ListAsync(int limit, int offset, CancellationToken ct)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.GitRepositories.AsNoTracking().Where(r => r.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new ListResponse<GitRepositoryResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        };
    }

    public async Task<GitRepositoryResponse> GetAsync(Guid id, CancellationToken ct) =>
        ToResponse(await Require(id, ct));

    public async Task<GitRepositoryResponse> CreateAsync(CreateGitRepository dto, CancellationToken ct)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        var url = (dto.Url ?? string.Empty).Trim();
        if (name.Length == 0) throw new ValidationException("name is required");
        if (url.Length == 0) throw new ValidationException("url is required");
        if (!IsAllowedUrl(url)) throw new ValidationException("url must be https:// (ssh and git:// are not supported)");

        if (await _db.GitRepositories.AnyAsync(r => r.Name == name && r.IsActive, ct))
            throw new ConflictException("a repository with that name already exists", "git_repo_name_taken");

        if (dto.AuthCredentialId is { } credId)
        {
            var exists = await _db.Credentials.AnyAsync(
                c => c.CredentialId == credId && c.IsActive, ct);
            if (!exists) throw new ValidationException("auth_credential_id not found");
        }

        var now = DateTime.UtcNow;
        var row = new GitRepoEntity
        {
            GitRepositoryId = Guid.NewGuid(),
            Name = name,
            Url = url,
            DefaultBranch = string.IsNullOrWhiteSpace(dto.DefaultBranch) ? "main" : dto.DefaultBranch.Trim(),
            AuthCredentialId = dto.AuthCredentialId,
            Description = dto.Description,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        row.LocalPath = ComputeLocalPath(row);
        _db.GitRepositories.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("git.repo.created repo_id={RepoId} name={Name} url={Url}",
            row.GitRepositoryId, row.Name, row.Url);
        return ToResponse(row);
    }

    public async Task<GitRepositoryResponse> UpdateAsync(Guid id, UpdateGitRepository dto, CancellationToken ct)
    {
        var row = await Require(id, ct);
        if (dto.Name is { } n && n.Trim().Length > 0) row.Name = n.Trim();
        if (dto.Url is { } u && u.Trim().Length > 0)
        {
            if (!IsAllowedUrl(u)) throw new ValidationException("url must be https://");
            row.Url = u.Trim();
        }
        if (dto.DefaultBranch is { } b && b.Trim().Length > 0) row.DefaultBranch = b.Trim();
        if (dto.AuthCredentialId.HasValue) row.AuthCredentialId = dto.AuthCredentialId.Value;
        if (dto.Description is not null) row.Description = dto.Description;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    public async Task<GitRepositoryResponse> DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await Require(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Free the per-repo semaphore so it doesn't accumulate for the process
        // lifetime. The on-disk checkout is deliberately left in place so an
        // accidental deregistration is cheap to restore.
        if (Locks.TryRemove(id, out var sem))
        {
            try { sem.Dispose(); } catch { /* concurrent waiters; harmless */ }
        }
        return ToResponse(row);
    }

    // ─── Operations ─────────────────────────────────────────────────

    public Task<GitOpResult> PullAsync(Guid id, string? branch, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            var targetBranch = string.IsNullOrWhiteSpace(branch) ? row.DefaultBranch : branch!;

            using var repo = new Repository(repoPath);
            var fetchOpts = new FetchOptions { CredentialsProvider = auth };
            Commands.Fetch(repo, "origin", Array.Empty<string>(), fetchOpts, "fetch from nashira");
            CheckoutOrCreate(repo, targetBranch);

            var signature = BuildSignature(null, null);
            var mergeResult = Commands.Pull(repo, signature, new PullOptions
            {
                FetchOptions = fetchOpts,
                MergeOptions = new MergeOptions { FastForwardStrategy = FastForwardStrategy.Default },
            });

            row.LastFetchedAt = DateTime.UtcNow;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new GitOpResult
            {
                Ok = mergeResult.Status != MergeStatus.Conflicts,
                Message = mergeResult.Status.ToString(),
                CommitSha = mergeResult.Commit?.Sha,
                Branch = targetBranch,
            };
        });

    public Task<GitOpResult> PushAsync(Guid id, string? branch, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            var targetBranch = string.IsNullOrWhiteSpace(branch)
                ? (repo.Head?.FriendlyName ?? row.DefaultBranch)
                : branch!;
            var localBranch = repo.Branches[targetBranch];
            if (localBranch is null)
                return new GitOpResult { Ok = false, Message = $"branch '{targetBranch}' not found locally", Branch = targetBranch };
            try
            {
                var published = PushBranch(repo, localBranch, new PushOptions { CredentialsProvider = auth });
                return new GitOpResult
                {
                    Ok = true,
                    Message = published ? "pushed and set upstream" : "pushed",
                    Branch = targetBranch,
                    CommitSha = localBranch.Tip.Sha,
                };
            }
            catch (LibGit2SharpException ex)
            {
                _logger.LogWarning(ex, "git.push.failed repo_id={RepoId} branch={Branch}", row.GitRepositoryId, targetBranch);
                return new GitOpResult { Ok = false, Message = $"push failed: {ex.Message}", Branch = targetBranch };
            }
        });

    public Task<GitBranchesResponse> ListBranchesAsync(Guid id, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            TryFetch(repoPath, auth); // best-effort so remote branches show up
            using var repo = new Repository(repoPath);

            var current = repo.Head?.FriendlyName ?? row.DefaultBranch;
            var local = repo.Branches.Where(b => !b.IsRemote).Select(b => b.FriendlyName);
            var remote = repo.Branches
                .Where(b => b.IsRemote && !b.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal))
                .Select(b => b.FriendlyName.StartsWith("origin/", StringComparison.Ordinal)
                    ? b.FriendlyName["origin/".Length..]
                    : b.FriendlyName);
            var all = local.Concat(remote)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return new GitBranchesResponse { Current = current, Branches = all };
        });

    public Task<GitOpResult> CheckoutAsync(Guid id, string branch, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            if (string.IsNullOrWhiteSpace(branch))
                throw new ValidationException("branch is required");
            await EnsureClonedAsync(row, repoPath, auth, ct);
            TryFetch(repoPath, auth);
            using var repo = new Repository(repoPath);
            CheckoutOrCreate(repo, branch);
            return new GitOpResult { Ok = true, Message = "checked out", Branch = branch };
        });

    public Task<GitStatusResponse> StatusAsync(Guid id, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);

            // Ignored files are deliberately excluded: they are noise for an agent
            // deciding whether there is anything worth committing.
            var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeIgnored = false,
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
            });

            var result = new GitStatusResponse
            {
                Branch = repo.Head?.FriendlyName ?? row.DefaultBranch,
                Staged = status.Where(e => e.State.HasFlag(FileStatus.NewInIndex)
                                        || e.State.HasFlag(FileStatus.ModifiedInIndex)
                                        || e.State.HasFlag(FileStatus.DeletedFromIndex)
                                        || e.State.HasFlag(FileStatus.RenamedInIndex))
                                .Select(e => e.FilePath).OrderBy(p => p).ToList(),
                Modified = status.Where(e => e.State.HasFlag(FileStatus.ModifiedInWorkdir))
                                 .Select(e => e.FilePath).OrderBy(p => p).ToList(),
                Untracked = status.Where(e => e.State.HasFlag(FileStatus.NewInWorkdir))
                                  .Select(e => e.FilePath).OrderBy(p => p).ToList(),
                Missing = status.Where(e => e.State.HasFlag(FileStatus.DeletedFromWorkdir))
                                .Select(e => e.FilePath).OrderBy(p => p).ToList(),
            };
            result.Clean = result.Staged.Count == 0 && result.Modified.Count == 0
                        && result.Untracked.Count == 0 && result.Missing.Count == 0;

            var tracking = repo.Head?.TrackingDetails;
            result.Ahead = tracking?.AheadBy;
            result.Behind = tracking?.BehindBy;

            return result;
        });

    public Task<GitListFilesResponse> ListFilesAsync(Guid id, string? path, string? @ref, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            var refName = string.IsNullOrWhiteSpace(@ref) ? (repo.Head?.FriendlyName ?? row.DefaultBranch) : @ref!;
            var commit = ResolveCommit(repo, refName) ?? repo.Head?.Tip;
            if (commit is null)
                return new GitListFilesResponse { Ref = refName, Path = path ?? string.Empty };

            var rel = NormalizePath(path);
            var tree = commit.Tree;
            if (!string.IsNullOrEmpty(rel))
            {
                var node = tree[rel];
                if (node is null || node.TargetType != TreeEntryTargetType.Tree)
                    return new GitListFilesResponse { Ref = refName, Path = rel };
                tree = (Tree)node.Target;
            }

            var entries = tree
                .Where(t => t.TargetType is TreeEntryTargetType.Blob or TreeEntryTargetType.Tree)
                .Select(t => new GitFileEntry
                {
                    Path = string.IsNullOrEmpty(rel) ? t.Name : $"{rel}/{t.Name}",
                    Type = t.TargetType == TreeEntryTargetType.Tree ? "tree" : "blob",
                    Size = t.TargetType == TreeEntryTargetType.Blob ? ((Blob)t.Target).Size : 0,
                })
                .OrderBy(e => e.Type != "tree") // dirs first
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .ToList();

            return new GitListFilesResponse { Ref = refName, Path = rel, Entries = entries };
        });

    public Task<GitReadFileResponse> ReadFileAsync(Guid id, string path, string? @ref, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            var rel = NormalizePath(path);
            if (string.IsNullOrEmpty(rel))
                throw new ValidationException("path is required");

            var refName = string.IsNullOrWhiteSpace(@ref) ? (repo.Head?.FriendlyName ?? row.DefaultBranch) : @ref!;
            var commit = ResolveCommit(repo, refName) ?? repo.Head?.Tip
                ?? throw new ValidationException($"ref '{refName}' not found");
            var entry = commit[rel] ?? throw new NotFoundException($"path '{rel}' not in tree");
            if (entry.TargetType != TreeEntryTargetType.Blob)
                throw new ValidationException($"path '{rel}' is not a file");

            var blob = (Blob)entry.Target;
            if (blob.Size > _maxFileBytes)
                throw new ValidationException($"file exceeds max size ({_maxFileBytes} bytes)");
            var isBinary = blob.IsBinary;
            string content;
            if (isBinary)
            {
                await using var stream = blob.GetContentStream();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                content = Convert.ToBase64String(ms.ToArray());
            }
            else
            {
                content = blob.GetContentText();
            }
            return new GitReadFileResponse
            {
                Path = rel,
                Ref = refName,
                Content = content,
                Size = blob.Size,
                IsBinary = isBinary,
            };
        });

    public Task<GitOpResult> WriteFileAsync(Guid id, GitWriteFileRequest req, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            var rel = NormalizePath(req.Path);
            if (string.IsNullOrEmpty(rel))
                return new GitOpResult { Ok = false, Message = "path is required" };
            if (string.IsNullOrWhiteSpace(req.CommitMessage))
                return new GitOpResult { Ok = false, Message = "commit_message is required" };
            var bytes = Encoding.UTF8.GetByteCount(req.Content ?? string.Empty);
            if (bytes > _maxFileBytes)
                return new GitOpResult { Ok = false, Message = $"content exceeds max size ({_maxFileBytes} bytes)" };

            await EnsureClonedAsync(row, repoPath, auth, ct);
            string commitSha;
            string? branchName;
            using (var repo = new Repository(repoPath))
            {
                if (!string.IsNullOrWhiteSpace(req.Branch))
                    CheckoutOrCreate(repo, req.Branch!);

                var absolute = Path.GetFullPath(Path.Combine(repoPath, rel));
                if (!absolute.StartsWith(Path.GetFullPath(repoPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && absolute != Path.GetFullPath(repoPath))
                    return new GitOpResult { Ok = false, Message = "path escapes repository root" };
                var dir = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(absolute, req.Content ?? string.Empty, Encoding.UTF8, ct);

                Commands.Stage(repo, rel);
                var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true });
                if (!status.IsDirty)
                    return new GitOpResult { Ok = true, Message = "no changes", Branch = repo.Head?.FriendlyName };

                var sig = BuildSignature(req.AuthorName, req.AuthorEmail);
                var commit = repo.Commit(req.CommitMessage, sig, sig);
                commitSha = commit.Sha;
                branchName = repo.Head?.FriendlyName;
            }

            var pushMsg = req.Push ? PushSuffix(PushCurrentBranch(repoPath, auth)) : null;
            return new GitOpResult { Ok = true, Message = $"committed{pushMsg}", CommitSha = commitSha, Branch = branchName };
        });

    public Task<GitOpResult> CommitAsync(Guid id, GitCommitRequest req, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            if (string.IsNullOrWhiteSpace(req.CommitMessage))
                return new GitOpResult { Ok = false, Message = "commit_message is required" };
            await EnsureClonedAsync(row, repoPath, auth, ct);

            string commitSha;
            string? branchName;
            using (var repo = new Repository(repoPath))
            {
                if (req.Paths is { Count: > 0 })
                    foreach (var p in req.Paths)
                    {
                        var rel = NormalizePath(p);
                        if (!string.IsNullOrEmpty(rel)) Commands.Stage(repo, rel);
                    }
                else
                    Commands.Stage(repo, "*");

                var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true });
                if (!status.IsDirty)
                    return new GitOpResult { Ok = true, Message = "no changes", Branch = repo.Head?.FriendlyName };

                var sig = BuildSignature(req.AuthorName, req.AuthorEmail);
                var commit = repo.Commit(req.CommitMessage, sig, sig);
                commitSha = commit.Sha;
                branchName = repo.Head?.FriendlyName;
            }

            var pushMsg = req.Push ? PushSuffix(PushCurrentBranch(repoPath, auth)) : null;
            return new GitOpResult { Ok = true, Message = $"committed{pushMsg}", CommitSha = commitSha, Branch = branchName };
        });

    public Task<GitDiffResponse> DiffAsync(Guid id, string? @from, string? to, string? path, CancellationToken ct) =>
        WithRepoAsync(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            Patch patch;
            string fromRef, toRef;
            if (string.IsNullOrWhiteSpace(@from) && string.IsNullOrWhiteSpace(to))
            {
                fromRef = "HEAD";
                toRef = "WORKING";
                var paths = string.IsNullOrEmpty(path) ? null : new[] { NormalizePath(path) };
                patch = repo.Diff.Compare<Patch>(repo.Head?.Tip?.Tree, DiffTargets.WorkingDirectory, paths);
            }
            else
            {
                fromRef = string.IsNullOrWhiteSpace(@from) ? "HEAD" : @from!;
                toRef = string.IsNullOrWhiteSpace(to) ? (repo.Head?.FriendlyName ?? row.DefaultBranch) : to!;
                var fromCommit = ResolveCommit(repo, fromRef) ?? throw new ValidationException($"ref '{fromRef}' not found");
                var toCommit = ResolveCommit(repo, toRef) ?? throw new ValidationException($"ref '{toRef}' not found");
                var paths = string.IsNullOrEmpty(path) ? null : new[] { NormalizePath(path) };
                patch = repo.Diff.Compare<Patch>(fromCommit.Tree, toCommit.Tree, paths);
            }
            return await Task.FromResult(new GitDiffResponse
            {
                From = fromRef,
                To = toRef,
                Path = path,
                Patch = patch.Content ?? string.Empty,
            });
        });

    // ─── helpers ────────────────────────────────────────────────────

    private Task<GitRepoEntity?> Find(Guid id, CancellationToken ct) =>
        _db.GitRepositories.FirstOrDefaultAsync(
            r => r.GitRepositoryId == id && r.IsActive, ct);

    private async Task<GitRepoEntity> Require(Guid id, CancellationToken ct) =>
        await Find(id, ct) ?? throw new NotFoundException("git repository not found");

    private async Task<T> WithRepoAsync<T>(
        Guid id, CancellationToken ct, Func<GitRepoEntity, string, CredentialsHandler, Task<T>> body)
    {
        var row = await Require(id, ct);

        var localPath = ComputeLocalPath(row);
        if (row.LocalPath != localPath)
        {
            row.LocalPath = localPath;
            await _db.SaveChangesAsync(ct);
        }

        var auth = await BuildAuthHandlerAsync(row, ct);
        var sem = Locks.GetOrAdd(row.GitRepositoryId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            return await body(row, localPath, auth);
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogWarning(ex, "git.op.failed repo_id={RepoId}", row.GitRepositoryId);
            throw new ValidationException($"git operation failed: {ex.Message}");
        }
        finally
        {
            sem.Release();
        }
    }

    // HTTPS+token: returns a CredentialsHandler backed by the linked Credential's
    // decrypted PAT, or unauthenticated credentials for public clone.
    private async Task<CredentialsHandler> BuildAuthHandlerAsync(GitRepoEntity row, CancellationToken ct)
    {
        if (row.AuthCredentialId is not { } credId)
            return (_, _, _) => new DefaultCredentials();

        var cred = await _db.Credentials.AsNoTracking().FirstOrDefaultAsync(
            c => c.CredentialId == credId && c.IsActive, ct);
        if (cred is null)
            return (_, _, _) => new DefaultCredentials();

        // PATs live in EncryptedToken since auth_method 'token' became first-class;
        // fall back to EncryptedPassword for rows created before the migration.
        var token = _crypto.Decrypt(cred.EncryptedToken)
            ?? _crypto.Decrypt(cred.EncryptedPassword)
            ?? string.Empty;
        var username = string.IsNullOrWhiteSpace(cred.Username) ? "git" : cred.Username!;
        return (_, _, _) => new UsernamePasswordCredentials { Username = username, Password = token };
    }

    private async Task EnsureClonedAsync(GitRepoEntity row, string repoPath, CredentialsHandler auth, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(repoPath, ".git"))) return;

        _logger.LogInformation("git.clone.start repo_id={RepoId} url={Url} path={Path}",
            row.GitRepositoryId, row.Url, repoPath);
        Directory.CreateDirectory(repoPath);
        await Task.Run(() => Repository.Clone(row.Url, repoPath, new CloneOptions
        {
            BranchName = row.DefaultBranch,
            FetchOptions = { CredentialsProvider = auth },
        }), ct);
        row.LastFetchedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static void CheckoutOrCreate(Repository repo, string branchName)
    {
        var local = repo.Branches[branchName];
        if (local is not null)
        {
            Commands.Checkout(repo, local);
            return;
        }
        var remote = repo.Branches[$"origin/{branchName}"];
        if (remote is not null)
        {
            var tracking = repo.CreateBranch(branchName, remote.Tip);
            repo.Branches.Update(tracking,
                b => b.Remote = "origin",
                b => b.UpstreamBranch = $"refs/heads/{branchName}");
            Commands.Checkout(repo, tracking);
            return;
        }
        var fresh = repo.CreateBranch(branchName);
        Commands.Checkout(repo, fresh);
    }

    private static Commit? ResolveCommit(Repository repo, string refName)
    {
        var local = repo.Branches[refName];
        if (local is not null) return local.Tip;
        var remote = repo.Branches[$"origin/{refName}"];
        if (remote is not null) return remote.Tip;
        return repo.Lookup<Commit>(refName); // sha or tag
    }

    private Signature BuildSignature(string? name, string? email)
    {
        var who = string.IsNullOrWhiteSpace(name)
            ? (string.IsNullOrWhiteSpace(_user.Username) ? _defaultAuthorName : _user.Username!)
            : name!;
        var mail = string.IsNullOrWhiteSpace(email) ? _defaultAuthorEmail : email!;
        return new Signature(who, mail, DateTimeOffset.UtcNow);
    }

    private string ComputeLocalPath(GitRepoEntity row) =>
        Path.Combine(_gitRoot, row.GitRepositoryId.ToString("N"));

    // Best-effort fetch for branch listing / checkout. Failures are logged, never thrown.
    /// <summary>
    /// Pushes a local branch, publishing it and recording its upstream the first
    /// time. Returns true when the branch had no upstream and now has one.
    /// </summary>
    /// <remarks>
    /// <c>Network.Push(Branch, …)</c> requires the branch to ALREADY track a
    /// remote one and otherwise throws
    /// <c>"The branch 'x' that you are trying to push does not track an upstream
    /// branch."</c> — so a branch created here could be checked out, written to
    /// and committed, and then never published. Every write to <c>main</c>
    /// worked, which is what made it look like pushing worked at all.
    ///
    /// Pushing by refspec is what <c>git push -u origin &lt;branch&gt;</c> does:
    /// send <c>refs/heads/x:refs/heads/x</c>, then record the upstream so the
    /// next push, the ahead/behind counts and a pull all resolve against it.
    /// </remarks>
    internal static bool PushBranch(Repository repo, Branch localBranch, PushOptions options)
    {
        if (localBranch.IsTracking)
        {
            repo.Network.Push(localBranch, options);
            return false;
        }

        var remote = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault()
            ?? throw new ValidationException(
                "this repository has no remote configured, so a branch cannot be published");

        repo.Network.Push(remote, $"{localBranch.CanonicalName}:{localBranch.CanonicalName}", options);

        // Recorded only after the push succeeds. Writing the upstream first would
        // leave a branch claiming to track something the remote does not have.
        repo.Branches.Update(localBranch,
            b => b.Remote = remote.Name,
            b => b.UpstreamBranch = localBranch.CanonicalName);
        return true;
    }

    private void TryFetch(string repoPath, CredentialsHandler auth)
    {
        try
        {
            using var repo = new Repository(repoPath);
            Commands.Fetch(repo, "origin", Array.Empty<string>(),
                new FetchOptions { CredentialsProvider = auth }, null);
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogDebug(ex, "git.fetch.skipped");
        }
    }

    private GitOpResult PushCurrentBranch(string repoPath, CredentialsHandler auth)
    {
        using var repo = new Repository(repoPath);
        var branchName = repo.Head?.FriendlyName ?? string.Empty;
        if (string.IsNullOrEmpty(branchName))
            return new GitOpResult { Ok = false, Message = "no current branch" };
        try
        {
            var localBranch = repo.Branches[branchName] ?? repo.Head;
            // Same rule as PushAsync: a branch created here has no upstream until
            // its first push, and this is the path a write-and-push takes — which
            // is where the failure was actually met.
            var published = PushBranch(repo, localBranch, new PushOptions { CredentialsProvider = auth });
            return new GitOpResult { Ok = true, Message = published ? "pushed and set upstream" : "pushed" };
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogWarning(ex, "git.push.failed path={Path} branch={Branch}", repoPath, branchName);
            return new GitOpResult { Ok = false, Message = ex.Message };
        }
    }

    private static string PushSuffix(GitOpResult push) => push.Ok ? " · pushed" : $" · push failed: {push.Message}";

    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Replace('\\', '/').Trim('/');
        // Reject absolute and traversal fragments outright.
        if (p.Split('/').Any(seg => seg is ".." or "."))
            return string.Empty;
        return p;
    }

    // HTTPS only in v1 — http:// would send the PAT in cleartext; ssh/git:// deferred.
    internal static bool IsAllowedUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static GitRepositoryResponse ToResponse(GitRepoEntity r) => new()
    {
        GitRepositoryId = r.GitRepositoryId,
        Name = r.Name,
        Url = r.Url,
        DefaultBranch = r.DefaultBranch,
        AuthCredentialId = r.AuthCredentialId,
        Description = r.Description,
        LocalPath = r.LocalPath,
        LastFetchedAt = r.LastFetchedAt,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };
}
