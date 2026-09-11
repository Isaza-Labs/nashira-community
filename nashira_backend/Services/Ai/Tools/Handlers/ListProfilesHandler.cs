using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists assistant profiles (agent personas) so the agent can reference one by id or name. Read → autonomous.
public sealed class ListProfilesHandler : IToolHandler
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListProfilesHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_profiles";
    public string Description =>
        "Lists assistant profiles (agent personas: name, display name, described skills, response style). " +
        "Use the id or name with update_profile / delete_profile / assign_profile.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var profiles = await _db.Profiles.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name)
            .ToListAsync(ct);
        var rows = profiles.Select(p => new
        {
            profile_id = p.ProfileId,
            name = p.Name,
            display_name = p.DisplayName,
            description = p.Description,
            skills = p.Skills,
            response_style = p.ResponseStyle,
            display_order = p.DisplayOrder,
            created_at = p.CreatedAt,
            updated_at = p.UpdatedAt,
        }).ToList();
        return JsonSerializer.SerializeToElement(new { profiles = rows, count = rows.Count });
    }
}
