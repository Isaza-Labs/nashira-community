using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Device;

public class CreateDevice
{
    [JsonPropertyName("device_name")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("ip_address")] public string IpAddress { get; set; } = string.Empty;
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("os_version")] public string? OsVersion { get; set; }
    [JsonPropertyName("site")] public string? Site { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("credential_id")] public Guid? CredentialId { get; set; }
    [JsonPropertyName("source_id")] public Guid? SourceId { get; set; }
    [JsonPropertyName("external_id")] public string? ExternalId { get; set; }
    [JsonPropertyName("properties")] public JsonElement? Properties { get; set; }
    [JsonPropertyName("allow_draft")] public bool? AllowDraft { get; set; }
    [JsonPropertyName("allow_qa")] public bool? AllowQa { get; set; }
    [JsonPropertyName("allow_production")] public bool? AllowProduction { get; set; }
    [JsonPropertyName("expected_ssh_host_key_fingerprint")] public string? ExpectedSshHostKeyFingerprint { get; set; }
}

public class UpdateDevice
{
    [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
    [JsonPropertyName("ip_address")] public string? IpAddress { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("os_version")] public string? OsVersion { get; set; }
    [JsonPropertyName("site")] public string? Site { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("credential_id")] public Guid? CredentialId { get; set; }
    [JsonPropertyName("source_id")] public Guid? SourceId { get; set; }
    [JsonPropertyName("external_id")] public string? ExternalId { get; set; }
    [JsonPropertyName("properties")] public JsonElement? Properties { get; set; }
    [JsonPropertyName("allow_draft")] public bool? AllowDraft { get; set; }
    [JsonPropertyName("allow_qa")] public bool? AllowQa { get; set; }
    [JsonPropertyName("allow_production")] public bool? AllowProduction { get; set; }
    [JsonPropertyName("expected_ssh_host_key_fingerprint")] public string? ExpectedSshHostKeyFingerprint { get; set; }
}

// Aggregate view for dashboards: the true inventory size and status mix computed
// over every active row. A paged list cannot answer either — it only sees one page.
public class DeviceStatsResponse
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("by_status")] public Dictionary<string, int> ByStatus { get; set; } = new();
}

public class DeviceResponse
{
    [JsonPropertyName("device_id")] public Guid DeviceId { get; set; }
    [JsonPropertyName("device_name")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("ip_address")] public string IpAddress { get; set; } = string.Empty;
    [JsonPropertyName("platform")] public string Platform { get; set; } = string.Empty;
    [JsonPropertyName("vendor")] public string Vendor { get; set; } = string.Empty;
    [JsonPropertyName("os_version")] public string OsVersion { get; set; } = string.Empty;
    [JsonPropertyName("site")] public string Site { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("credential_id")] public Guid? CredentialId { get; set; }
    [JsonPropertyName("source_id")] public Guid? SourceId { get; set; }
    [JsonPropertyName("external_id")] public string? ExternalId { get; set; }
    [JsonPropertyName("last_sync_at")] public DateTime? LastSyncAt { get; set; }
    [JsonPropertyName("properties")] public JsonElement Properties { get; set; }
    [JsonPropertyName("allow_draft")] public bool AllowDraft { get; set; }
    [JsonPropertyName("allow_qa")] public bool AllowQa { get; set; }
    [JsonPropertyName("allow_production")] public bool AllowProduction { get; set; }
    [JsonPropertyName("has_host_key_fingerprint")] public bool HasHostKeyFingerprint { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}
