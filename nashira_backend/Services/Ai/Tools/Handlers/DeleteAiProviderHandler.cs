using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using AIProviderEntity = nashira_backend.Data.Models.AIProvider;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes an LLM provider (resolved by id or name). Admin. Write → single_confirm.
public sealed class DeleteAiProviderHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "ai_provider_id":{"type":"string","description":"Provider id (or identify by name)"},
          "name":{"type":"string","description":"Identify by name if no id"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DeleteAiProviderHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "delete_ai_provider";
    public string Description => "Removes an LLM provider, identified by ai_provider_id or name (admin).";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        AIProviderEntity? row = null;
        if (Str(args, "ai_provider_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            row = await _db.AIProviders.FirstOrDefaultAsync(
                p => p.AIProviderId == id && p.IsActive, ct);
        else if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            row = await _db.AIProviders.FirstOrDefaultAsync(
                p => p.Name == name && p.IsActive, ct);

        if (row is null) return Err("provider not found (pass a valid ai_provider_id or name)");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { ai_provider_id = row.AIProviderId, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
