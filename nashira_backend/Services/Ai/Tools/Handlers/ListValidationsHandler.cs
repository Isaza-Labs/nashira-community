using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Recent template-validation records. Admin-only; read → autonomous.
public sealed class ListValidationsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "kind":{"type":"string","enum":["skill","spec"]},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":25}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListValidationsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_validations";
    public string Description => "Lists recent template-validation records (kind, target, ok, time). Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var kind = Str(args, "kind");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 100) : 25;

        var q = _db.ValidationRecords.AsNoTracking().Where(v => v.IsActive);
        if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(v => v.Kind == kind);

        var rows = await q.OrderByDescending(v => v.CreatedAt).Take(limit)
            .Select(v => new
            {
                validation_record_id = v.ValidationRecordId,
                kind = v.Kind,
                target_name = v.TargetName,
                ok = v.Ok,
                at = v.CreatedAt,
            })
            .ToListAsync(ct);
        return JsonSerializer.SerializeToElement(new { validations = rows, count = rows.Count });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
