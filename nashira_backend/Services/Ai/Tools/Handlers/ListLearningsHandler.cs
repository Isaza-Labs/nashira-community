using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists agent self-correction learnings for the tenant plus the read-only system-wide
// knowledge (IsSystem). Read → autonomous.
public sealed class ListLearningsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "category":{"type":"string","description":"Filter by category: knowledge or learning"},
          "tool":{"type":"string","description":"Filter by tool name (exact match)"},
          "limit":{"type":"integer","minimum":1,"maximum":200,"default":50},
          "offset":{"type":"integer","minimum":0,"default":0}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListLearningsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_learnings";
    public string Description =>
        "Lists agent self-correction learnings (curated 'knowledge' and discovered 'learning'), most " +
        "confident first, including read-only system-wide knowledge. Optionally filter by category or " +
        "tool. Use the agent_learning_id with update_learning / delete_learning.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var category = Str(args, "category");
        var tool = Str(args, "tool");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? lv : 50;
        var offset = args.TryGetProperty("offset", out var o) && o.TryGetInt32(out var ov) ? ov : 0;
        (limit, offset) = Pagination.Clamp(limit, offset);

        var q = _db.AgentLearnings.AsNoTracking()
            .Where(l => l.IsActive);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(l => l.Category == category);
        if (!string.IsNullOrWhiteSpace(tool)) q = q.Where(l => l.ToolName == tool);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(l => l.Confidence).Skip(offset).Take(limit).ToListAsync(ct);
        var learnings = rows.Select(l => new
        {
            agent_learning_id = l.AgentLearningId,
            error_pattern = l.ErrorPattern,
            error_category = l.ErrorCategory,
            service_type = l.ServiceType,
            tool_name = l.ToolName,
            fix_strategy = l.FixStrategy,
            fix_params = ParseJson(l.FixParamsJson),
            category = l.Category,
            confidence = l.Confidence,
            success_count = l.SuccessCount,
            failure_count = l.FailureCount,
            is_system = l.IsSystem,
            is_active = l.IsActive,
            created_at = l.CreatedAt,
            updated_at = l.UpdatedAt,
        }).ToList();
        return JsonSerializer.SerializeToElement(new { learnings, total, count = learnings.Count, limit, offset });
    }

    private static JsonElement? ParseJson(string? json)
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
}
