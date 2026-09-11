using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Git;

namespace nashira_backend.Services.Git;

// Tenant-scoped façade over a local working copy of a registered Git repository.
// The service owns the on-disk layout (one folder per repo under
// {Git:Root}/{GitRepositoryId}) and serializes calls per repository so
// two simultaneous writes don't trash the index. Throws DomainException on error;
// git-level failures (merge conflict, push rejected) come back as GitOpResult.Ok=false.
// Authorization is enforced at the controller/tool layer — the service only filters
// by tenant. HTTPS+token transport only (SSH-key is deferred).
public interface IGitService
{
    Task<ListResponse<GitRepositoryResponse>> ListAsync(int limit, int offset, CancellationToken ct);
    Task<GitRepositoryResponse> GetAsync(Guid id, CancellationToken ct);
    Task<GitRepositoryResponse> CreateAsync(CreateGitRepository dto, CancellationToken ct);
    Task<GitRepositoryResponse> UpdateAsync(Guid id, UpdateGitRepository dto, CancellationToken ct);
    Task<GitRepositoryResponse> DeleteAsync(Guid id, CancellationToken ct);

    Task<GitOpResult> PullAsync(Guid id, string? branch, CancellationToken ct);
    Task<GitOpResult> PushAsync(Guid id, string? branch, CancellationToken ct);
    Task<GitBranchesResponse> ListBranchesAsync(Guid id, CancellationToken ct);
    Task<GitOpResult> CheckoutAsync(Guid id, string branch, CancellationToken ct);
    Task<GitStatusResponse> StatusAsync(Guid id, CancellationToken ct);

    Task<GitListFilesResponse> ListFilesAsync(Guid id, string? path, string? @ref, CancellationToken ct);
    Task<GitReadFileResponse> ReadFileAsync(Guid id, string path, string? @ref, CancellationToken ct);
    Task<GitOpResult> WriteFileAsync(Guid id, GitWriteFileRequest req, CancellationToken ct);
    Task<GitOpResult> CommitAsync(Guid id, GitCommitRequest req, CancellationToken ct);
    Task<GitDiffResponse> DiffAsync(Guid id, string? @from, string? to, string? path, CancellationToken ct);
}
