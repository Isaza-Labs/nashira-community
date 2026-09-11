using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Services.Git;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Git repository management + working-copy operations. Read = viewer; repository
// registration (create/update/delete) = admin; git operations (pull/push/checkout/
// write/commit) = operator+. The service throws DomainException on error.
[ApiController]
[Route("api/git")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class GitController : ControllerBase
{
    private readonly IGitService _git;

    public GitController(IGitService git) => _git = git;

    [HttpGet("repositories")]
    public Task<ListResponse<GitRepositoryResponse>> List(int limit = 50, int offset = 0, CancellationToken ct = default)
        => _git.ListAsync(limit, offset, ct);

    [HttpGet("repositories/{id:guid}")]
    public Task<GitRepositoryResponse> Get(Guid id, CancellationToken ct) => _git.GetAsync(id, ct);

    [HttpPost("repositories")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<GitRepositoryResponse>> Create([FromBody] CreateGitRepository dto, CancellationToken ct)
    {
        var res = await _git.CreateAsync(dto, ct);
        return CreatedAtAction(nameof(Get), new { id = res.GitRepositoryId }, res);
    }

    [HttpPut("repositories/{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public Task<GitRepositoryResponse> Update(Guid id, [FromBody] UpdateGitRepository dto, CancellationToken ct)
        => _git.UpdateAsync(id, dto, ct);

    [HttpDelete("repositories/{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public Task<GitRepositoryResponse> Delete(Guid id, CancellationToken ct) => _git.DeleteAsync(id, ct);

    [HttpPost("repositories/{id:guid}/pull")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public Task<GitOpResult> Pull(Guid id, [FromQuery] string? branch, CancellationToken ct) => _git.PullAsync(id, branch, ct);

    [HttpPost("repositories/{id:guid}/push")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public Task<GitOpResult> Push(Guid id, [FromQuery] string? branch, CancellationToken ct) => _git.PushAsync(id, branch, ct);

    [HttpGet("repositories/{id:guid}/branches")]
    public Task<GitBranchesResponse> Branches(Guid id, CancellationToken ct) => _git.ListBranchesAsync(id, ct);

    [HttpPost("repositories/{id:guid}/checkout")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public Task<GitOpResult> Checkout(Guid id, [FromQuery] string branch, CancellationToken ct) => _git.CheckoutAsync(id, branch, ct);

    [HttpGet("repositories/{id:guid}/files")]
    public Task<GitListFilesResponse> Files(Guid id, [FromQuery] string? path, [FromQuery] string? @ref, CancellationToken ct)
        => _git.ListFilesAsync(id, path, @ref, ct);

    [HttpGet("repositories/{id:guid}/file")]
    public Task<GitReadFileResponse> File(Guid id, [FromQuery] string path, [FromQuery] string? @ref, CancellationToken ct)
        => _git.ReadFileAsync(id, path, @ref, ct);

    [HttpPut("repositories/{id:guid}/file")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public Task<GitOpResult> WriteFile(Guid id, [FromBody] GitWriteFileRequest req, CancellationToken ct)
        => _git.WriteFileAsync(id, req, ct);

    [HttpPost("repositories/{id:guid}/commit")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public Task<GitOpResult> Commit(Guid id, [FromBody] GitCommitRequest req, CancellationToken ct)
        => _git.CommitAsync(id, req, ct);

    [HttpGet("repositories/{id:guid}/diff")]
    public Task<GitDiffResponse> Diff(Guid id, [FromQuery] string? @from, [FromQuery] string? to, [FromQuery] string? path, CancellationToken ct)
        => _git.DiffAsync(id, @from, to, path, ct);

    [HttpGet("repositories/{id:guid}/status")]
    public Task<GitStatusResponse> Status(Guid id, CancellationToken ct)
        => _git.StatusAsync(id, ct);
}
