using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using ProfileEntity = nashira_backend.Data.Models.Profile;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates an assistant profile (resolved by id or name). Only provided fields change; name is immutable. Write → single_confirm.
public sealed class UpdateProfileHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "profile_id":{"type":"string","description":"Profile id (or identify by name)"},
          "name":{"type":"string","description":"Identify the profile by name if profile_id is omitted"},
          "display_name":{"type":"string"},
          "description":{"type":"string"},
          "skills":{"type":"array","items":{"type":"string"}},
          "response_style":{"type":"string"},
          "display_order":{"type":"integer"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateProfileHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "update_profile";
    public string Description =>
        "Updates an assistant profile identified by profile_id or name. Only the fields you provide change. " +
        "The profile name itself cannot be changed.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("profile not found (pass a valid profile_id or name)");

        if (args.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String) row.DisplayName = dn.GetString()!;
        if (args.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String) row.Description = desc.GetString();
        if (CreateProfileHandler.ReadSkills(args) is { } skills) row.Skills = skills;
        if (args.TryGetProperty("response_style", out var rs) && rs.ValueKind == JsonValueKind.String) row.ResponseStyle = rs.GetString();
        if (args.TryGetProperty("display_order", out var order) && order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out var ov))
            row.DisplayOrder = ov;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { profile_id = row.ProfileId, name = row.Name, updated = true });
    }

    private async Task<ProfileEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "profile_id")?.Trim() is { Length: > 0 } pid && Guid.TryParse(pid, out var id))
            return await _db.Profiles.FirstOrDefaultAsync(
                p => p.ProfileId == id && p.IsActive, ct);
        if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            return await _db.Profiles.FirstOrDefaultAsync(
                p => p.Name == name && p.IsActive, ct);
        return null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
