using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Python;
using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Security;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Controllers;

// Python environments built with uv (see PythonEnvironment for why they exist).
//
// Reads are Operator: whoever can edit a device or a snippet has to be able to pick an
// environment for it. Writes are Admin: defining an environment ends with
// `uv pip install` running third-party packaging code on the server, and the installed
// packages then run wherever SSH and snippets run — the same reasoning that makes
// approving a pip module Admin-only.
//
// Every response masks credentials in index URLs, and every write audits itself with
// the masked form ([SkipAudit] turns off the generic filter, which would otherwise
// record the same row a second time under a generic action name).
[ApiController]
[Route("api/python-environments")]
[Authorize(Policy = "Operator")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class PythonEnvironmentController : ControllerBase
{
    private const string AuditEntity = "python_environment";

    private readonly PythonEnvironmentService _service;
    private readonly IAuditLogger _audit;

    public PythonEnvironmentController(PythonEnvironmentService service, IAuditLogger audit)
    {
        _service = service;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<PythonEnvironmentResponse>>> Get(CancellationToken ct)
    {
        var rows = await _service.ListAsync(ct);
        var included = await _service.IncludedSpecsAsync(rows.Select(r => r.PythonEnvironmentId).ToList(), ct);
        return new OkObjectResult(new ListResponse<PythonEnvironmentResponse>
        {
            Items = rows.Select(r => PythonEnvironmentResponse.From(r, included.GetValueOrDefault(r.PythonEnvironmentId)))
                .ToList(),
            Total = rows.Count,
            Limit = rows.Count,
            Offset = 0,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PythonEnvironmentResponse>> GetById(Guid id, CancellationToken ct) =>
        await ResponseAsync(await _service.GetAsync(id, ct), ct);

    [HttpPost]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit]
    public async Task<ActionResult<PythonEnvironmentResponse>> Post(
        [FromBody] WritePythonEnvironment dto, CancellationToken ct)
    {
        var row = await _service.CreateAsync(dto.ToDraft(), ct);
        var response = await ResponseAsync(row, ct);
        await _audit.LogAsync(AuditEntity, row.PythonEnvironmentId, "create", after: AuditView(response), ct: ct);
        return new CreatedAtActionResult(nameof(GetById), "PythonEnvironment", new { id = row.PythonEnvironmentId }, response);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit]
    public async Task<ActionResult<PythonEnvironmentResponse>> Update(
        Guid id, [FromBody] WritePythonEnvironment dto, CancellationToken ct)
    {
        var before = await ResponseAsync(await _service.GetAsync(id, ct), ct);
        var row = await _service.UpdateAsync(id, dto.ToDraft(), ct);
        var response = await ResponseAsync(row, ct);
        await _audit.LogAsync(AuditEntity, id, "update",
            before: AuditView(before), after: AuditView(response), ct: ct);
        return response;
    }

    // Queue the environment for another build — after a failure, or to pick up newer
    // releases within the same version ranges.
    [HttpPost("{id:guid}/rebuild")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit]
    public async Task<ActionResult<PythonEnvironmentResponse>> Rebuild(Guid id, CancellationToken ct)
    {
        var row = await _service.RebuildAsync(id, ct);
        var response = await ResponseAsync(row, ct);
        await _audit.LogAsync(AuditEntity, id, "rebuild", after: AuditView(response), ct: ct);
        return response;
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit]
    public async Task<ActionResult<PythonEnvironmentResponse>> Delete(Guid id, CancellationToken ct)
    {
        // Read before the delete drops the environment's allowlist links, so the audit
        // entry still says what it included.
        var before = await ResponseAsync(await _service.GetAsync(id, ct), ct);
        var row = await _service.DeleteAsync(id, ct);
        var response = PythonEnvironmentResponse.From(row);
        response.IncludedPackages = before.IncludedPackages;
        await _audit.LogAsync(AuditEntity, id, "delete", before: AuditView(response), ct: ct);
        return response;
    }

    private async Task<PythonEnvironmentResponse> ResponseAsync(PythonEnvironment row, CancellationToken ct) =>
        PythonEnvironmentResponse.From(row, await _service.IncludedSpecsAsync(row.PythonEnvironmentId, ct));

    // What an incident review needs: the definition, already masked. The freeze output
    // is left out — it is long, derivable from the definition and the build time, and
    // would make every audit row of this entity mostly package listings.
    private static object AuditView(PythonEnvironmentResponse r) => new
    {
        r.Name,
        r.PythonVersion,
        r.Requirements,
        r.IncludedPackages,
        r.IsSshFallback,
        r.Status,
        r.Description,
    };
}
