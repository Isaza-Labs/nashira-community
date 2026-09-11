using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using CredentialEntity = nashira_backend.Data.Models.Credential;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes a credential (resolved by id or name). Admin. Write → single_confirm.
public sealed class DeleteCredentialHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "credential_id":{"type":"string","description":"Credential id (or identify by name)"},
          "name":{"type":"string","description":"Identify by name if no id"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DeleteCredentialHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "delete_credential";
    public string Description => "Removes a credential, identified by credential_id or name (admin).";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        CredentialEntity? row = null;
        if (Str(args, "credential_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            row = await _db.Credentials.FirstOrDefaultAsync(
                c => c.CredentialId == id && c.IsActive, ct);
        else if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            row = await _db.Credentials.FirstOrDefaultAsync(
                c => c.Name == name && c.IsActive, ct);

        if (row is null) return Err("credential not found (pass a valid credential_id or name)");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { credential_id = row.CredentialId, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
