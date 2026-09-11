using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Full detail (incl. before/after) of one audit event. Admin-only; read → autonomous.
public sealed class GetAuditEventHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "audit_event_id":{"type":"string","description":"Event id from list_audit_events"}
        },"required":["audit_event_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public GetAuditEventHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "get_audit_event";
    public string Description =>
        "Returns the full detail of one audit event, including the before/after state. Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "audit_event_id")?.Trim() is not { Length: > 0 } sid || !Guid.TryParse(sid, out var id))
            return Err("audit_event_id (a uuid) is required");

        var e = await _db.AuditEvents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AuditEventId == id, ct);
        if (e is null) return Err("audit event not found");

        return JsonSerializer.SerializeToElement(new
        {
            audit_event_id = e.AuditEventId,
            sequence = e.Sequence,
            at = e.At,
            user_id = e.UserId,
            entity_type = e.EntityType,
            entity_id = e.EntityId,
            action = e.Action,
            ip = e.Ip,
            hash = e.Hash,
            prev_hash = e.PrevHash,
            before = Parse(e.BeforeJson),
            after = Parse(e.AfterJson),
        });
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
