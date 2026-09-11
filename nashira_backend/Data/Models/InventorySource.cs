namespace nashira_backend.Data.Models;

// An external inventory source (NetBox in v1) that syncs devices into the local
// Device table. The API token is a ${secret:secret:<name>:value} reference resolved at
// sync time (or a literal). Tenant-scoped.
public class InventorySource : BaseModel
{
    public const string KindNetBox = "netbox";

    public Guid InventorySourceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = KindNetBox;
    public string BaseUrl { get; set; } = string.Empty;
    public string? TokenSecretRef { get; set; }
    public string? SiteFilter { get; set; }
    public bool AllowPrivateNetwork { get; set; }
    public DateTime? LastSyncedAt { get; set; }
}
