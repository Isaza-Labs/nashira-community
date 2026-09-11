namespace nashira_backend.Data.Models;

// Tenant-scoped non-secret configuration (base URLs, feature toggles, ...).
// Plaintext; secrets live in the Secret entity. Unique per
// (Provider, SettingKey).
public class SystemSetting : BaseModel
{
    public Guid SystemSettingId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string SettingKey { get; set; } = string.Empty;
    public string? SettingValue { get; set; }
    public bool IsEditable { get; set; } = true;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public string? InputType { get; set; }
}
