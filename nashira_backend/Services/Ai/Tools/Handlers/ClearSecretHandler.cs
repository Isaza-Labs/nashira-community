using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Deletes a secret by name. Admin-only; write → single_confirm. Mirrors
// SecretsController.Delete: the row is soft-deleted rather than erased, so the audit
// entries that name its id stay resolvable and the name stays reserved.
public sealed class ClearSecretHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"The secret's lookup name"}
        },"required":["name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ClearSecretHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "clear_secret";
    public string Description =>
        "Deletes a stored secret by name. Every ${secret:secret:<name>:value} reference to it " +
        "stops resolving. Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("name is required");

        var secret = await _db.Secrets.FirstOrDefaultAsync(s => s.Name == name && s.IsActive, ct);
        if (secret is null) return Err("secret not found");

        secret.IsActive = false;
        secret.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            secret_id = secret.SecretId,
            name,
            deleted = true,
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
