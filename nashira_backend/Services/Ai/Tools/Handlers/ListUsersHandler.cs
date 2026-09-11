using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists users in the caller's company (metadata only — no password material). Admin-only;
// read → autonomous.
public sealed class ListUsersHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "limit":{"type":"integer","minimum":1,"maximum":200,"default":50}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListUsersHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_users";
    public string Description => "Lists users in your company (id, username, email, role, active). Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 200) : 50;
        var rows = await _db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.Username).Take(limit)
            .Select(u => new
            {
                user_id = u.UserId,
                username = u.Username,
                email = u.Email,
                role = u.Role,
                is_active = u.IsActive,
            })
            .ToListAsync(ct);
        return JsonSerializer.SerializeToElement(new { users = rows, count = rows.Count });
    }
}
