using System.Text.Json;
using System.Text.Json.Serialization;
using nashira_backend.Services.Security;

namespace nashira_backend.Services.Mcp;

// The secret half of an McpServer's configuration. Serialized to JSON and
// encrypted into McpServer.AuthConfigEncrypted; never leaves the backend.
public sealed class McpAuthConfig
{
    [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("api_key_header")] public string? ApiKeyHeader { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }

    // Free-form secret headers for servers whose scheme is none of the above.
    [JsonPropertyName("secret_headers")] public Dictionary<string, string>? SecretHeaders { get; set; }

    // oauth_client_credentials configuration.
    [JsonPropertyName("token_url")] public string? TokenUrl { get; set; }
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("client_secret")] public string? ClientSecret { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }

    // oauth_authorization_code configuration (ported from FlowWeaver's flow).
    // TokenUrl above doubles as FW's token_endpoint; both endpoints may also be
    // discovered (RFC 8414 / RFC 9728) instead of typed.
    [JsonPropertyName("authorization_endpoint")] public string? AuthorizationEndpoint { get; set; }
    [JsonPropertyName("redirect_uri")] public string? RedirectUri { get; set; }

    // Obtained at runtime by the grant. Persisted because an access token that is
    // still valid should survive a restart, and because a refresh token cannot be
    // re-derived from anything the admin typed.
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_at")] public DateTime? ExpiresAt { get; set; }

    // Transient PKCE verifier + CSRF nonce held between oauth/start and
    // oauth/callback (cleared once tokens are obtained).
    [JsonPropertyName("code_verifier")] public string? CodeVerifier { get; set; }
    [JsonPropertyName("state_nonce")] public string? StateNonce { get; set; }

    public bool HasSecret =>
        !string.IsNullOrEmpty(ApiKey) || !string.IsNullOrEmpty(Token)
        || !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(ClientSecret)
        || (SecretHeaders is { Count: > 0 });
}

// Bridges a stored Credential into an McpAuthConfig, the same split Integration uses:
// auth_type describes the SHAPE, the credential supplies the MATERIAL.
//
// The field names diverge from Integration's on purpose — MCP calls the key `api_key`
// with `api_key_header`, Integration calls the same thing `token` with `header` — so
// this mapping cannot be shared with IntegrationCredentialAuth.
public static class McpCredentialAuth
{
    public static string AuthTypeFor(string? credentialAuthMethod) =>
        (credentialAuthMethod ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Data.Models.Credential.AuthMethodPassword => Data.Models.McpServer.AuthBasic,
            Data.Models.Credential.AuthMethodToken => Data.Models.McpServer.AuthBearer,
            Data.Models.Credential.AuthMethodApiKey => Data.Models.McpServer.AuthApiKey,
            Data.Models.Credential.AuthMethodOAuth2 => Data.Models.McpServer.AuthOAuthClientCredentials,
            // An SSH private key cannot travel in an HTTP header.
            _ => Data.Models.McpServer.AuthNone,
        };

    public static bool IsHttpUsable(string? credentialAuthMethod) =>
        AuthTypeFor(credentialAuthMethod) != Data.Models.McpServer.AuthNone;

    // Fills only what the stored config left empty, so the runtime-owned OAuth tokens
    // already persisted on the server survive a credential being attached.
    public static McpAuthConfig Merge(
        McpAuthConfig stored, Data.Models.Credential credential, ISecretProtector protector)
    {
        switch ((credential.AuthMethod ?? string.Empty).Trim().ToLowerInvariant())
        {
            case Data.Models.Credential.AuthMethodPassword:
                stored.Username = Fallback(stored.Username, credential.Username);
                stored.Password = Fallback(stored.Password, protector.Decrypt(credential.EncryptedPassword));
                break;

            case Data.Models.Credential.AuthMethodToken:
                stored.Token = Fallback(stored.Token, protector.Decrypt(credential.EncryptedToken));
                break;

            case Data.Models.Credential.AuthMethodApiKey:
                stored.ApiKey = Fallback(stored.ApiKey, protector.Decrypt(credential.EncryptedToken));
                stored.ApiKeyHeader = Fallback(stored.ApiKeyHeader, credential.ApiKeyHeader);
                break;

            case Data.Models.Credential.AuthMethodOAuth2:
                stored.ClientId = Fallback(stored.ClientId, credential.ClientId);
                stored.ClientSecret = Fallback(stored.ClientSecret, protector.Decrypt(credential.EncryptedClientSecret));
                stored.TokenUrl = Fallback(stored.TokenUrl, credential.TokenUrl);
                stored.Scope = Fallback(stored.Scope, credential.Scopes);
                break;
        }

        return stored;
    }

    private static string? Fallback(string? preferred, string? candidate) =>
        string.IsNullOrWhiteSpace(preferred) ? candidate : preferred;
}

// Encrypt/decrypt McpAuthConfig against ISecretProtector (Data Protection).
public static class McpAuthConfigCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[]? Encrypt(McpAuthConfig? config, ISecretProtector protector) =>
        config is null ? null : protector.Encrypt(JsonSerializer.Serialize(config, Options));

    // Returns an empty config rather than null on absent or unreadable material, so
    // callers apply "no auth" instead of dereferencing. An undecryptable blob means
    // the Data Protection keyring changed, which the caller sees as a 401 upstream —
    // noisy, but not a crash.
    public static McpAuthConfig Decrypt(byte[]? ciphertext, ISecretProtector protector)
    {
        if (ciphertext is null || ciphertext.Length == 0) return new McpAuthConfig();
        try
        {
            var json = protector.Decrypt(ciphertext);
            if (string.IsNullOrWhiteSpace(json)) return new McpAuthConfig();
            return JsonSerializer.Deserialize<McpAuthConfig>(json) ?? new McpAuthConfig();
        }
        catch (Exception)
        {
            return new McpAuthConfig();
        }
    }
}
