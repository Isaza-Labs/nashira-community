using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Credential;

public class CreateCredential
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "ssh";
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("private_key")] public string? PrivateKey { get; set; }
    [JsonPropertyName("key_passphrase")] public string? KeyPassphrase { get; set; }
    [JsonPropertyName("auth_method")] public string? AuthMethod { get; set; }
    // token / api_key: the secret value (bearer token, PAT, or API key).
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("api_key_header")] public string? ApiKeyHeader { get; set; }
    // oauth2 client-credentials.
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("client_secret")] public string? ClientSecret { get; set; }
    [JsonPropertyName("token_url")] public string? TokenUrl { get; set; }
    [JsonPropertyName("scopes")] public string? Scopes { get; set; }
}

public class UpdateCredential
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("private_key")] public string? PrivateKey { get; set; }
    [JsonPropertyName("key_passphrase")] public string? KeyPassphrase { get; set; }
    [JsonPropertyName("auth_method")] public string? AuthMethod { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("api_key_header")] public string? ApiKeyHeader { get; set; }
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("client_secret")] public string? ClientSecret { get; set; }
    [JsonPropertyName("token_url")] public string? TokenUrl { get; set; }
    [JsonPropertyName("scopes")] public string? Scopes { get; set; }
}

// Never includes the secret bytes — only has_* flags plus the non-secret OAuth2
// coordinates (client id / token URL / scopes are public per RFC 6749).
public class CredentialResponse
{
    [JsonPropertyName("credential_id")] public Guid CredentialId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("auth_method")] public string AuthMethod { get; set; } = "password";
    [JsonPropertyName("has_password")] public bool HasPassword { get; set; }
    [JsonPropertyName("has_private_key")] public bool HasPrivateKey { get; set; }
    [JsonPropertyName("has_token")] public bool HasToken { get; set; }
    [JsonPropertyName("has_client_secret")] public bool HasClientSecret { get; set; }
    [JsonPropertyName("api_key_header")] public string? ApiKeyHeader { get; set; }
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("token_url")] public string? TokenUrl { get; set; }
    [JsonPropertyName("scopes")] public string? Scopes { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}
