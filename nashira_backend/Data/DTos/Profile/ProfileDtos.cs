using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Profile;

public class CreateProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("skills")] public List<string>? Skills { get; set; }
    [JsonPropertyName("response_style")] public string? ResponseStyle { get; set; }
    [JsonPropertyName("display_order")] public int? DisplayOrder { get; set; }
}

public class UpdateProfile
{
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("skills")] public List<string>? Skills { get; set; }
    [JsonPropertyName("response_style")] public string? ResponseStyle { get; set; }
    [JsonPropertyName("display_order")] public int? DisplayOrder { get; set; }
}

public class ProfileResponse
{
    [JsonPropertyName("profile_id")] public Guid ProfileId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("skills")] public List<string> Skills { get; set; } = [];
    [JsonPropertyName("response_style")] public string? ResponseStyle { get; set; }
    [JsonPropertyName("display_order")] public int DisplayOrder { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class AssignProfileRequest
{
    [JsonPropertyName("profile_id")] public Guid? ProfileId { get; set; }
    [JsonPropertyName("custom_profile_text")] public string? CustomProfileText { get; set; }
}

public class UserProfileResponse
{
    [JsonPropertyName("profile_id")] public Guid? ProfileId { get; set; }
    [JsonPropertyName("custom_profile_text")] public string? CustomProfileText { get; set; }
}
