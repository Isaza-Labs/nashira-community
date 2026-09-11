using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Inventory;

public class CreateInventorySource
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("token_secret_ref")] public string? TokenSecretRef { get; set; }
    [JsonPropertyName("site_filter")] public string? SiteFilter { get; set; }
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; }
}

public class UpdateInventorySource
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("token_secret_ref")] public string? TokenSecretRef { get; set; }
    [JsonPropertyName("site_filter")] public string? SiteFilter { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }
}

// Never exposes the token reference's resolved value.
public class InventorySourceResponse
{
    [JsonPropertyName("inventory_source_id")] public Guid InventorySourceId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("token_secret_ref")] public string? TokenSecretRef { get; set; }
    [JsonPropertyName("site_filter")] public string? SiteFilter { get; set; }
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; }
    [JsonPropertyName("last_synced_at")] public DateTime? LastSyncedAt { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class NetBoxSyncResult
{
    [JsonPropertyName("created")] public int Created { get; set; }
    [JsonPropertyName("updated")] public int Updated { get; set; }
    [JsonPropertyName("unchanged")] public int Unchanged { get; set; }
    // Records the sync could not turn into a row of their own: no usable name, a
    // device_name another row already holds, or a name a *different* device from
    // this source already carries. Reported instead of aborting the sync — see
    // NetBoxSyncService.
    [JsonPropertyName("skipped")] public int Skipped { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("dry_run")] public bool DryRun { get; set; }
}
