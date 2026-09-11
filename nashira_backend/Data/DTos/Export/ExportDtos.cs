using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Export;

// Export metadata (never the bytes — those are fetched via the download endpoint).
public class ExportArtifactResponse
{
    [JsonPropertyName("export_artifact_id")] public Guid ExportArtifactId { get; set; }
    [JsonPropertyName("file_name")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = string.Empty;
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}
