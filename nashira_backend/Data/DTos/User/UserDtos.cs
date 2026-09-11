using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.User;

public class CreateUser
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string? Role { get; set; }
}

public class UpdateUser
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("is_active")] public bool? IsActive { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
}

public class UserResponse
{
    [JsonPropertyName("user_id")] public Guid UserId { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("profile_id")] public Guid? ProfileId { get; set; }
    [JsonPropertyName("password_changed_at")] public DateTime PasswordChangedAt { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }

    // Set only on create: whether the credentials email reached the relay, and
    // the warning for the admin when it did not. Absent on every other endpoint.
    [JsonPropertyName("credentials_email_sent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CredentialsEmailSent { get; set; }

    [JsonPropertyName("warning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; set; }
}
