using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Reads the tamper-evident audit trail. Admin-only (gated as "dangerous"); read → autonomous.
public sealed class ListAuditEventsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "entity_type":{"type":"string","description":"Filter by entity type, e.g. Device, User"},
          "action":{"type":"string","description":"Filter by action, e.g. create, update, delete"},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":25}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListAuditEventsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_audit_events";
    public string Description =>
        "Lists recent audit-trail events (most recent first), optionally filtered by entity_type or " +
        "action. Returns sequence, time, user, entity, action, and hash. Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var entityType = Str(args, "entity_type");
        var action = Str(args, "action");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 100) : 25;

        var q = _db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);

        var rows = await q.OrderByDescending(a => a.Sequence).Take(limit)
            .Select(a => new
            {
                audit_event_id = a.AuditEventId,
                sequence = a.Sequence,
                at = a.At,
                user_id = a.UserId,
                entity_type = a.EntityType,
                entity_id = a.EntityId,
                action = a.Action,
            })
            .ToListAsync(ct);
        return JsonSerializer.SerializeToElement(new { events = rows, count = rows.Count });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
