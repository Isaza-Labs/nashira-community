using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Security;
using nashira_backend.Services.Identity;
using AIProviderEntity = nashira_backend.Data.Models.AIProvider;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates an LLM provider (resolved by id or name). Admin. api_key encrypted, never returned.
// Write → single_confirm. Mirrors AIProviderController.Update.
public sealed class UpdateAiProviderHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "ai_provider_id":{"type":"string","description":"Provider id (or identify by name)"},
          "name":{"type":"string","description":"Identify by name if no id"},
          "new_name":{"type":"string"},
          "base_url":{"type":"string"},
          "default_model":{"type":"string"},
          "api_key":{"type":"string","description":"Replaces the stored key; empty string clears it"},
          "enabled":{"type":"boolean"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ISecretProtector _crypto;
    private readonly ICurrentUser _user;

    public UpdateAiProviderHandler(AppDbContext db, ISecretProtector crypto, ICurrentUser user)
    {
        _db = db;
        _crypto = crypto;
        _user = user;
    }

    public string Name => "update_ai_provider";
    public string Description =>
        "Updates an LLM provider identified by ai_provider_id or name (admin). Only provided fields " +
        "change; use new_name to rename. The api_key is encrypted and never returned.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("provider not found (pass a valid ai_provider_id or name)");

        if (Str(args, "new_name")?.Trim() is { Length: > 0 } nn) row.Name = nn;
        if (args.TryGetProperty("base_url", out var bu) && bu.ValueKind == JsonValueKind.String) row.BaseURL = bu.GetString();
        if (Str(args, "default_model")?.Trim() is { Length: > 0 } dm) row.DefaultModel = dm;
        if (args.TryGetProperty("api_key", out var ak) && ak.ValueKind == JsonValueKind.String) row.EncryptedApiKey = _crypto.Encrypt(ak.GetString());
        if (args.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False) row.Enabled = en.GetBoolean();
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            ai_provider_id = row.AIProviderId,
            name = row.Name,
            has_api_key = row.EncryptedApiKey is { Length: > 0 },
            enabled = row.Enabled,
            updated = true,
        });
    }

    private async Task<AIProviderEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "ai_provider_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.AIProviders.FirstOrDefaultAsync(
                p => p.AIProviderId == id && p.IsActive, ct);
        if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            return await _db.AIProviders.FirstOrDefaultAsync(
                p => p.Name == name && p.IsActive, ct);
        return null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
