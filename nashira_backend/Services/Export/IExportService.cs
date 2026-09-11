using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Export;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Export;

// Builds downloadable artifacts and stores them. Two shapes, deliberately separate:
// CreateAsync takes rows and produces a table (csv/xlsx/pdf/html/markdown);
// CreateDocumentAsync takes markdown and produces a written report (pdf/html/markdown).
// Tenant-scoped; throws DomainException on bad input.
public interface IExportService
{
    Task<ExportArtifactResponse> CreateAsync(
        string format,
        string? fileName,
        IReadOnlyList<string>? columns,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        CancellationToken ct);

    // `content` is markdown: headings, paragraphs, lists, GFM tables, fenced code.
    Task<ExportArtifactResponse> CreateDocumentAsync(
        string format,
        string? fileName,
        string? title,
        string content,
        CancellationToken ct);

    Task<ListResponse<ExportArtifactResponse>> ListAsync(int limit, int offset, CancellationToken ct);

    // Returns the full row (including bytes) for the download endpoint, or null.
    Task<ExportArtifact?> GetForDownloadAsync(Guid id, CancellationToken ct);
}
