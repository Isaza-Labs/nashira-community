namespace nashira_backend.Data.Models;

// A generated export file (CSV/XLSX) held for download. Bytes live in the row for
// simplicity; move to blob storage if exports grow large. Tenant-scoped.
public class ExportArtifact : BaseModel
{
    public Guid ExportArtifactId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[] Content { get; set; } = [];
    public Guid? CreatedByUserId { get; set; }
}
