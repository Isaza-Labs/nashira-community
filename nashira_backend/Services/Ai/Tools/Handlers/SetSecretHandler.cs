using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using SecretEntity = nashira_backend.Data.Models.Secret;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Stores (upserts) an encrypted secret value under a name. The value is encrypted at
// rest and never returned. Admin-only; write → single_confirm. Mirrors
// SecretsController.Create/Update, including the name grammar — a secret the agent
// creates has to be referenceable by the same ${secret:secret:<name>:value} marker as
// one an admin typed.
public sealed class SetSecretHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"Lookup key, e.g. netbox-token: 2-64 chars, lowercase letters/digits/hyphens/underscores"},
          "value":{"type":"string","description":"The secret value to store (encrypted; never echoed)"},
          "description":{"type":"string","description":"What this credential authorizes"}
        },"required":["name","value"],"additionalProperties":false}
        """).RootElement.Clone();

    private static readonly Regex NameRegex = new(
        "^[a-z0-9](?:[a-z0-9_-]{1,62}[a-z0-9])?$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ICurrentUser _user;

    public SetSecretHandler(AppDbContext db, ISecretProtector protector, ICurrentUser user)
    {
        _db = db;
        _protector = protector;
        _user = user;
    }

    public string Name => "set_secret";
    public string Description =>
        "Stores an encrypted secret value under a name (creating it if new, rotating it if it " +
        "exists). The value is encrypted at rest and never returned. Admin only. Templates " +
        "reference it as ${secret:secret:<name>:value}.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name")?.Trim();
        var value = Str(args, "value");
        if (string.IsNullOrWhiteSpace(name) || !NameRegex.IsMatch(name))
            return Err("name must be 2–64 chars, lowercase letters/digits/hyphens/underscores");
        if (string.IsNullOrEmpty(value)) return Err("value is required");

        var now = DateTime.UtcNow;
        // Not scoped to IsActive: a soft-deleted row still owns its name, and reviving it
        // in place is the only way to keep the id every audit entry already points at.
        var secret = await _db.Secrets.FirstOrDefaultAsync(s => s.Name == name, ct);
        var created = secret is null;
        if (secret is null)
        {
            secret = new SecretEntity
            {
                SecretId = Guid.NewGuid(),
                Name = name,
                CreatedBy = _user.IsAuthenticated ? _user.Username : null,
                CreatedAt = now,
            };
            _db.Secrets.Add(secret);
        }

        secret.IsActive = true;
        secret.EncryptedValue = _protector.Encrypt(value) ?? Array.Empty<byte>();
        if (Str(args, "description") is { } desc) secret.Description = desc.Trim();
        secret.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            secret_id = secret.SecretId,
            name,
            created,
            has_value = true,
            reference = $"${{secret:secret:{name}:value}}",
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
