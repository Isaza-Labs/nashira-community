using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Export;
using nashira_backend.Exceptions;
using nashira_backend.Services.Export;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Lists and downloads generated export artifacts. Creation happens through the
// export_table agent tool / ExportService; this controller only lists + streams.
[ApiController]
[Route("api/export")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class ExportController : ControllerBase
{
    private readonly IExportService _export;

    public ExportController(IExportService export) => _export = export;

    [HttpGet]
    public Task<ListResponse<ExportArtifactResponse>> List(int limit = 50, int offset = 0, CancellationToken ct = default)
        => _export.ListAsync(limit, offset, ct);

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var artifact = await _export.GetForDownloadAsync(id, ct)
            ?? throw new NotFoundException("export not found");
        return File(artifact.Content, artifact.ContentType, artifact.FileName);
    }
}
