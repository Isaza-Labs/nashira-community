namespace nashira_backend.Data.Models;

// Stored credential. Secret fields are encrypted at rest (Data Protection) and
// never leave the service layer in plaintext except when a handler injects them
// (e.g. the SSH runner, the git PAT). Tenant-scoped.
//
// auth_method decides which material the credential carries:
//   password → Username + EncryptedPassword                (SSH login / basic auth)
//   key      → EncryptedPrivateKey (+ passphrase)          (SSH)
//   token    → EncryptedToken                              (bearer / PAT)
//   api_key  → EncryptedToken + ApiKeyHeader               (named header key)
//   oauth2   → ClientId + EncryptedClientSecret + TokenUrl (client-credentials grant)
public class Credential : BaseModel
{
    public const string AuthMethodPassword = "password";
    public const string AuthMethodKey = "key";
    public const string AuthMethodToken = "token";
    public const string AuthMethodApiKey = "api_key";
    public const string AuthMethodOAuth2 = "oauth2";

    public Guid CredentialId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // free-form context tag, e.g. "ssh", "git_token", "netbox"
    public string? Username { get; set; }

    public string AuthMethod { get; set; } = AuthMethodPassword;

    public byte[]? EncryptedPassword { get; set; }
    public byte[]? EncryptedPrivateKey { get; set; }
    public byte[]? EncryptedKeyPassphrase { get; set; }

    // token / api_key: the secret value (bearer token, PAT, or API key).
    public byte[]? EncryptedToken { get; set; }

    // api_key: header the key is sent in (e.g. "X-API-Key"); null → Authorization.
    public string? ApiKeyHeader { get; set; }

    // oauth2 client-credentials. ClientId/TokenUrl/Scopes are not secret (RFC 6749);
    // only the client secret is.
    public string? ClientId { get; set; }
    public byte[]? EncryptedClientSecret { get; set; }
    public string? TokenUrl { get; set; }
    public string? Scopes { get; set; } // space-separated, as in the OAuth2 wire format
}

// Shared auth-method rules — the controller and the agent tools validate identically.
public static class CredentialRules
{
    public static readonly string[] AuthMethods =
    [
        Credential.AuthMethodPassword, Credential.AuthMethodKey, Credential.AuthMethodToken,
        Credential.AuthMethodApiKey, Credential.AuthMethodOAuth2,
    ];

    // Unknown/legacy values fall back to password — matches the pre-refactor coercion,
    // so existing rows and callers keep behaving the same.
    public static string Normalize(string? raw)
    {
        var v = (raw ?? Credential.AuthMethodPassword).Trim().ToLowerInvariant();
        return AuthMethods.Contains(v) ? v : Credential.AuthMethodPassword;
    }

    // Validates the credential's FINAL state (create and update share this).
    // Returns null when complete, else a message naming what is missing.
    // password stays lenient (pre-refactor behaviour: material may be set later);
    // the newer methods are strict — an incomplete token/oauth2 credential is unusable.
    public static string? MissingMaterial(Credential c) => c.AuthMethod switch
    {
        Credential.AuthMethodKey when IsEmpty(c.EncryptedPrivateKey) =>
            "auth_method 'key' requires private_key",
        Credential.AuthMethodToken when IsEmpty(c.EncryptedToken) =>
            "auth_method 'token' requires token",
        Credential.AuthMethodApiKey when IsEmpty(c.EncryptedToken) =>
            "auth_method 'api_key' requires token (the key value)",
        Credential.AuthMethodOAuth2 when string.IsNullOrWhiteSpace(c.ClientId) =>
            "auth_method 'oauth2' requires client_id",
        Credential.AuthMethodOAuth2 when IsEmpty(c.EncryptedClientSecret) =>
            "auth_method 'oauth2' requires client_secret",
        Credential.AuthMethodOAuth2 when string.IsNullOrWhiteSpace(c.TokenUrl) =>
            "auth_method 'oauth2' requires token_url",
        _ => null,
    };

    private static bool IsEmpty(byte[]? b) => b is null || b.Length == 0;
}
