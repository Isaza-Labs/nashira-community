using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Permission;

public class PermissionItem
{
    [JsonPropertyName("tool_domain")] public string ToolDomain { get; set; } = string.Empty;
    [JsonPropertyName("can_read")] public bool CanRead { get; set; }
    [JsonPropertyName("can_write")] public bool CanWrite { get; set; }
    [JsonPropertyName("can_execute")] public bool CanExecute { get; set; }

    // Removes the row instead of writing one, returning the target to "the role
    // decides". A row is a restriction, so absence and all-false mean opposite things —
    // all-false denies everything on that target, and there has to be a way to say
    // "never mind" that is not indistinguishable from "deny everything".
    [JsonPropertyName("inherit")] public bool? Inherit { get; set; }
}

public class UpdatePermissionsRequest
{
    [JsonPropertyName("permissions")] public List<PermissionItem> Permissions { get; set; } = [];
}

// One grantable target. `key` is what goes into a PermissionItem.tool_domain; `kind`
// lets the UI group capability domains apart from the concrete systems, which is the
// difference between a list of seven words and a list of everything registered.
public class PermissionResourceItem
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
}
