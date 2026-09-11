using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Permission;

public class NavigationVisibilityItem
{
    [JsonPropertyName("page_key")] public string PageKey { get; set; } = string.Empty;
    [JsonPropertyName("visible")] public bool Visible { get; set; }

    // Write-only. Removes the override so the next broader scope decides.
    [JsonPropertyName("inherit")] public bool? Inherit { get; set; }
}

public class UpdateNavigationVisibilityRequest
{
    [JsonPropertyName("permissions")]
    public List<NavigationVisibilityItem> Permissions { get; set; } = [];
}
