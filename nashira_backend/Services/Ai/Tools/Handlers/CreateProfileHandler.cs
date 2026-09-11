using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using ProfileEntity = nashira_backend.Data.Models.Profile;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Creates an assistant profile (agent persona). Name is unique within the company. Write → single_confirm.
public sealed class CreateProfileHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"Unique profile name within the company"},
          "display_name":{"type":"string","description":"Human-friendly name (defaults to name)"},
          "description":{"type":"string"},
          "skills":{"type":"array","items":{"type":"string"},"description":"Described skills the persona has"},
          "response_style":{"type":"string","description":"How the agent should phrase responses under this persona"},
          "display_order":{"type":"integer","description":"Sort order (default 0)"}
        },"required":["name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateProfileHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "create_profile";
    public string Description =>
        "Creates an assistant profile (agent persona). name must be unique within the company; " +
        "display_name defaults to name. Returns the new profile id.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("name is required");
        if (await _db.Profiles.AnyAsync(p => p.Name == name && p.IsActive, ct))
            return Err("a profile with this name already exists");

        var displayName = Str(args, "display_name");
        var now = DateTime.UtcNow;
        var row = new ProfileEntity
        {
            ProfileId = Guid.NewGuid(),
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
            Description = Str(args, "description"),
            Skills = ReadSkills(args) ?? [],
            ResponseStyle = Str(args, "response_style"),
            DisplayOrder = args.TryGetProperty("display_order", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var dv) ? dv : 0,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Profiles.Add(row);
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { profile_id = row.ProfileId, name = row.Name });
    }

    // Reads the "skills" array (shared with update_profile). Returns null when absent.
    internal static List<string>? ReadSkills(JsonElement args)
    {
        if (!args.TryGetProperty("skills", out var s) || s.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var el in s.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { } v) list.Add(v);
        return list;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
