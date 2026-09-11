using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;
using SpecEntity = nashira_backend.Data.Models.AiApiSpec;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes an OpenAPI spec (resolved by id or api name), then reloads the index.
// Admin-only; write → single_confirm.
public sealed class DeleteSpecHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "spec_id":{"type":"string","description":"Spec id (ai_api_spec_id) — or identify by api name"},
          "api":{"type":"string","description":"Spec api name if spec_id is omitted"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IApiSpecIndex _index;

    public DeleteSpecHandler(AppDbContext db, ICurrentUser user, IApiSpecIndex index)
    {
        _db = db;
        _user = user;
        _index = index;
    }

    public string Name => "delete_spec";
    public string Description => "Removes an OpenAPI spec, identified by spec_id or api name. Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        SpecEntity? row = null;
        if (Str(args, "spec_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            row = await _db.AiApiSpecs.FirstOrDefaultAsync(
                s => s.AiApiSpecId == id && s.IsActive, ct);
        else if (Str(args, "api")?.Trim().ToLowerInvariant() is { Length: > 0 } api)
            row = await _db.AiApiSpecs.FirstOrDefaultAsync(
                s => s.Api == api && s.IsActive, ct);

        if (row is null) return Err("api spec not found (pass a valid spec_id or api)");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _index.ReloadAsync(ct);
        return JsonSerializer.SerializeToElement(new { ai_api_spec_id = row.AiApiSpecId, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
