using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using ProfileEntity = nashira_backend.Data.Models.Profile;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes an assistant profile (resolved by id or name) and detaches it from any user. Write → single_confirm.
public sealed class DeleteProfileHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "profile_id":{"type":"string","description":"Profile id (or identify by name)"},
          "name":{"type":"string","description":"Identify the profile by name if profile_id is omitted"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DeleteProfileHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "delete_profile";
    public string Description =>
        "Removes an assistant profile, identified by profile_id or name, and detaches it from any user still using it.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        ProfileEntity? row = null;
        if (Str(args, "profile_id")?.Trim() is { Length: > 0 } pid && Guid.TryParse(pid, out var id))
            row = await _db.Profiles.FirstOrDefaultAsync(
                p => p.ProfileId == id && p.IsActive, ct);
        else if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            row = await _db.Profiles.FirstOrDefaultAsync(
                p => p.Name == name && p.IsActive, ct);

        if (row is null) return Err("profile not found (pass a valid profile_id or name)");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        // Detach the profile from any user still pointing at it.
        var assigned = await _db.Users
            .Where(u => u.ProfileId == row.ProfileId).ToListAsync(ct);
        foreach (var u in assigned) { u.ProfileId = null; u.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { profile_id = row.ProfileId, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
