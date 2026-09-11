using System.Text.Json;

namespace nashira_backend.Data.Models;

// Network device inventory row. `Platform` is the Netmiko device_type
// (cisco_ios, juniper_junos, nokia_srl, ...) the SSH runner uses. Tenant-scoped.
public class Device : BaseModel
{
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    public Guid? CredentialId { get; set; }

    // Provenance. Set on rows an InventorySource sync produced: SourceId is the
    // InventorySource row, ExternalId the device's id in that system (NetBox's
    // numeric pk). The pair is uniquely indexed (filtered on both non-null) so a
    // re-sync updates the row it created instead of duplicating it — a rename in
    // NetBox no longer forks the inventory. Both null on manually-created rows.
    public Guid? SourceId { get; set; }
    public string? ExternalId { get; set; }

    // When the last successful sync touched this row. Null on manually-created
    // devices — they are never "stale", so a nullable is the honest encoding
    // (flow-weaver's non-nullable DateTime writes 0001-01-01 for those).
    public DateTime? LastSyncAt { get; set; }

    // Free-form per-device attributes (jsonb): custom fields carried over from the
    // inventory source, site-specific tags, whatever the deployment needs without a
    // schema change. A default(JsonElement) is Undefined and Npgsql refuses to
    // write it, so seed a valid empty object (same pattern as AIProvider.Config).
    private static readonly JsonElement EmptyProperties = JsonDocument.Parse("{}").RootElement;
    public JsonElement Properties { get; set; } = EmptyProperties;

    // Which workflow environments may dispatch to this device. A run resolves its
    // targets against the environment of the workflow being run and refuses every
    // device that doesn't allow it (see DeviceEnvironmentPolicy). The three are
    // independent: any combination is valid, including all three (reachable from
    // anywhere) and none (parked — no run can target it, reported rather than
    // silently skipped).
    //
    // The defaults (draft + production on, qa off) are what the migration backfills
    // onto existing rows, so an inventory that predates the trio keeps behaving
    // exactly as it did before.
    public bool AllowDraft { get; set; } = true;
    public bool AllowQa { get; set; }
    public bool AllowProduction { get; set; } = true;

    // SSH host-key pinning ("SHA256:<base64>"). When set, the SSH runner refuses
    // to connect on mismatch (MITM defense); null = soft-TOFU on first connect.
    public string? ExpectedSshHostKeyFingerprint { get; set; }
}
