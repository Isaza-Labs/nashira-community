using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Security;
using nashira_backend.Services.Identity;
using CredentialEntity = nashira_backend.Data.Models.Credential;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates a credential (resolved by id or name). Admin. Secrets encrypted, never returned.
// Write → single_confirm. Mirrors CredentialController.Update.
public sealed class UpdateCredentialHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "credential_id":{"type":"string","description":"Credential id (or identify by name)"},
          "name":{"type":"string","description":"Identify by name if no id"},
          "new_name":{"type":"string"},
          "type":{"type":"string"},
          "username":{"type":"string"},
          "auth_method":{"type":"string","enum":["password","key","token","api_key","oauth2"]},
          "password":{"type":"string"},
          "private_key":{"type":"string"},
          "key_passphrase":{"type":"string"},
          "token":{"type":"string","description":"Bearer token / PAT (token) or the key value (api_key)"},
          "api_key_header":{"type":"string","description":"api_key only: header the key is sent in"},
          "client_id":{"type":"string","description":"oauth2 only"},
          "client_secret":{"type":"string","description":"oauth2 only"},
          "token_url":{"type":"string","description":"oauth2 only: token endpoint URL"},
          "scopes":{"type":"string","description":"oauth2 only: space-separated scopes"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ISecretProtector _crypto;
    private readonly ICurrentUser _user;

    public UpdateCredentialHandler(AppDbContext db, ISecretProtector crypto, ICurrentUser user)
    {
        _db = db;
        _crypto = crypto;
        _user = user;
    }

    public string Name => "update_credential";
    public string Description =>
        "Updates a credential identified by credential_id or name (admin). Only provided fields change; " +
        "use new_name to rename. Secret fields are encrypted and never returned.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("credential not found (pass a valid credential_id or name)");

        if (Str(args, "new_name")?.Trim() is { Length: > 0 } nn) row.Name = nn;
        if (Str(args, "type")?.Trim() is { Length: > 0 } tp) row.Type = tp;
        if (Str(args, "username") is { } un) row.Username = un;
        if (args.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String) row.EncryptedPassword = _crypto.Encrypt(pw.GetString());
        if (args.TryGetProperty("private_key", out var pk) && pk.ValueKind == JsonValueKind.String) row.EncryptedPrivateKey = _crypto.Encrypt(pk.GetString());
        if (args.TryGetProperty("key_passphrase", out var kp) && kp.ValueKind == JsonValueKind.String) row.EncryptedKeyPassphrase = _crypto.Encrypt(kp.GetString());
        if (args.TryGetProperty("token", out var tk) && tk.ValueKind == JsonValueKind.String) row.EncryptedToken = _crypto.Encrypt(tk.GetString());
        if (Str(args, "api_key_header") is { } akh) row.ApiKeyHeader = Trimmed(akh);
        if (Str(args, "client_id") is { } ci) row.ClientId = Trimmed(ci);
        if (args.TryGetProperty("client_secret", out var cs) && cs.ValueKind == JsonValueKind.String) row.EncryptedClientSecret = _crypto.Encrypt(cs.GetString());
        if (Str(args, "token_url") is { } tu) row.TokenUrl = Trimmed(tu);
        if (Str(args, "scopes") is { } sc) row.Scopes = Trimmed(sc);
        if (Str(args, "auth_method") is { } am) row.AuthMethod = CredentialRules.Normalize(am);

        // Final-state validation: the stored material must satisfy whatever
        // auth_method the row ends up with (updates may switch methods).
        if (CredentialRules.MissingMaterial(row) is { } missing) return Err(missing);

        row.UpdatedAt = DateTime.UtcNow;
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
            updated = true,
        });
    }

    private async Task<CredentialEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "credential_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.Credentials.FirstOrDefaultAsync(
                c => c.CredentialId == id && c.IsActive, ct);
        if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            return await _db.Credentials.FirstOrDefaultAsync(
                c => c.Name == name && c.IsActive, ct);
        return null;
    }

    private static string? Trimmed(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
