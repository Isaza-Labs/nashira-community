using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Reads a user's per-domain tool permissions. Admin-only; read → autonomous.
// Mirrors PermissionsController.GetForUser.
public sealed class GetUserPermissionsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "user_id":{"type":"string","description":"User id from list_users"}
        },"required":["user_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public GetUserPermissionsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "get_user_permissions";
    public string Description => "Returns a user's per-domain tool permissions (read/write/execute). Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "user_id")?.Trim() is not { Length: > 0 } sid || !Guid.TryParse(sid, out var id))
            return Err("user_id (a uuid from list_users) is required");

        var exists = await _db.Users.AnyAsync(u => u.UserId == id && u.IsActive, ct);
        if (!exists) return Err("user not found");

        var perms = await _db.UserToolPermissions.AsNoTracking()
            .Where(p => p.UserId == id)
            .Select(p => new
            {
                tool_domain = p.ToolDomain,
                can_read = p.CanRead,
                can_write = p.CanWrite,
                can_execute = p.CanExecute,
            })
            .ToListAsync(ct);
        return JsonSerializer.SerializeToElement(new { user_id = id, permissions = perms });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
