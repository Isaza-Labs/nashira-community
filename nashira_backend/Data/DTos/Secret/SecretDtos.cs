using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Secret;

// Secret metadata safe to expose through the API. The encrypted payload never
// leaves the backend — admins rotate by writing a new value, and reads only ever
// return whether a value is set.
public class SecretResponse
{
    [JsonPropertyName("secret_id")] public Guid SecretId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    // True whenever the row carries ciphertext — drives "not set yet" UI states
    // without revealing anything about the value itself.
    [JsonPropertyName("has_value")] public bool HasValue { get; set; }

    [JsonPropertyName("created_by")] public string? CreatedBy { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class CreateSecretRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
}

public class UpdateSecretRequest
{
    [JsonPropertyName("description")] public string? Description { get; set; }

    // Null → leave the ciphertext untouched (allows editing the description
    // only). Empty string is rejected: deleting the secret is how you remove a
    // value, and a silent wipe here is indistinguishable from a UI bug.
    [JsonPropertyName("value")] public string? Value { get; set; }
}
