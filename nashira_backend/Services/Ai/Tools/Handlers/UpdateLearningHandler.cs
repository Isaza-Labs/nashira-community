using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates a tenant's agent learning, resolved by learning_id. Only provided fields
// change. Seeded system knowledge (IsSystem) is read-only and cannot be
// updated. Write → single_confirm.
public sealed class UpdateLearningHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "learning_id":{"type":"string","description":"AgentLearning id (uuid) to update"},
          "error_pattern":{"type":"string","description":"Regex or substring that identifies the tool error"},
          "error_category":{"type":"string"},
          "service_type":{"type":"string"},
          "tool_name":{"type":"string"},
          "fix_strategy":{"type":"string","enum":["parameter_adjust","escalate"]},
          "fix_params":{"type":"object","description":"Replace the stored fix parameters"},
          "is_active":{"type":"boolean"}
        },"required":["learning_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateLearningHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "update_learning";
    public string Description =>
        "Updates an agent learning identified by learning_id. Only provided fields change. System-wide " +
        "knowledge is read-only and cannot be updated.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "learning_id")?.Trim() is not { Length: > 0 } lid || !Guid.TryParse(lid, out var id))
            return Err("learning_id is required (a valid uuid)");

        var row = await _db.AgentLearnings.FirstOrDefaultAsync(
            l => l.AgentLearningId == id && l.IsActive, ct);
        if (row is null) return Err("learning not found");
        if (row.IsSystem) return Err("system knowledge is read-only");

        if (Str(args, "error_pattern")?.Trim() is { Length: > 0 } ep) row.ErrorPattern = ep;
        if (Str(args, "error_category") is { } ec) row.ErrorCategory = ec;
        if (Str(args, "service_type") is { } st) row.ServiceType = st;
        if (Str(args, "tool_name") is { } tn) row.ToolName = tn;
        if (Str(args, "fix_strategy") is { } fs) row.FixStrategy = CreateLearningHandler.NormalizeStrategy(fs);
        if (args.TryGetProperty("fix_params", out var fp) && fp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            row.FixParamsJson = fp.GetRawText();
        if (args.TryGetProperty("is_active", out var ia) && ia.ValueKind is JsonValueKind.True or JsonValueKind.False)
            row.IsActive = ia.GetBoolean();
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { agent_learning_id = row.AgentLearningId, error_pattern = row.ErrorPattern, updated = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
