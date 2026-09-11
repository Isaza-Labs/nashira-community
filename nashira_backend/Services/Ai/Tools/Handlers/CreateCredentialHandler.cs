using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Security;
using nashira_backend.Services.Identity;
using CredentialEntity = nashira_backend.Data.Models.Credential;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Creates an SSH/API credential (admin). Secret material is encrypted at rest and NEVER
// returned. Write → single_confirm (admin-gated via "dangerous"). Mirrors CredentialController.Post.
public sealed class CreateCredentialHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string"},
          "type":{"type":"string","description":"Free-form context tag (default ssh; e.g. git_token, netbox)"},
          "username":{"type":"string"},
          "auth_method":{"type":"string","enum":["password","key","token","api_key","oauth2"],"description":"Default password"},
          "password":{"type":"string"},
          "private_key":{"type":"string"},
          "key_passphrase":{"type":"string"},
          "token":{"type":"string","description":"Bearer token / PAT (token) or the key value (api_key)"},
          "api_key_header":{"type":"string","description":"api_key only: header the key is sent in (e.g. X-API-Key)"},
          "client_id":{"type":"string","description":"oauth2 only"},
          "client_secret":{"type":"string","description":"oauth2 only"},
          "token_url":{"type":"string","description":"oauth2 only: token endpoint URL"},
          "scopes":{"type":"string","description":"oauth2 only: space-separated scopes"}
        },"required":["name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ISecretProtector _crypto;
    private readonly ICurrentUser _user;

    public CreateCredentialHandler(AppDbContext db, ISecretProtector crypto, ICurrentUser user)
    {
        _db = db;
        _crypto = crypto;
        _user = user;
    }

    public string Name => "create_credential";
    public string Description =>
        "Creates a credential (admin). auth_method: password, key (SSH), token (bearer/PAT), api_key, " +
        "or oauth2 (client credentials). Secret fields are encrypted and never returned.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("name is required");

        if (await _db.Credentials.AnyAsync(c => c.Name == name && c.IsActive, ct))
            return Err("a credential with this name already exists");

        var now = DateTime.UtcNow;
        var row = new CredentialEntity
        {
            CredentialId = Guid.NewGuid(),
            Name = name,
            Type = Str(args, "type")?.Trim() is { Length: > 0 } t ? t : "ssh",
            Username = Str(args, "username"),
            AuthMethod = CredentialRules.Normalize(Str(args, "auth_method")),
            EncryptedPassword = _crypto.Encrypt(Str(args, "password")),
            EncryptedPrivateKey = _crypto.Encrypt(Str(args, "private_key")),
            EncryptedKeyPassphrase = _crypto.Encrypt(Str(args, "key_passphrase")),
            EncryptedToken = _crypto.Encrypt(Str(args, "token")),
            ApiKeyHeader = Trimmed(Str(args, "api_key_header")),
            ClientId = Trimmed(Str(args, "client_id")),
            EncryptedClientSecret = _crypto.Encrypt(Str(args, "client_secret")),
            TokenUrl = Trimmed(Str(args, "token_url")),
            Scopes = Trimmed(Str(args, "scopes")),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (CredentialRules.MissingMaterial(row) is { } missing) return Err(missing);

        _db.Credentials.Add(row);
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            credential_id = row.CredentialId,
            name = row.Name,
            auth_method = row.AuthMethod,
            has_password = row.EncryptedPassword is { Length: > 0 },
            has_private_key = row.EncryptedPrivateKey is { Length: > 0 },
            has_token = row.EncryptedToken is { Length: > 0 },
            has_client_secret = row.EncryptedClientSecret is { Length: > 0 },
        });
    }

    private static string? Trimmed(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
