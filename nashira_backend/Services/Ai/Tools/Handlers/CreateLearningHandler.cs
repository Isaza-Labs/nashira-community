using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using LearningEntity = nashira_backend.Data.Models.AgentLearning;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Curates an agent self-correction learning (category = knowledge) for the tenant.
// error_pattern is required; fix_params holds the param delta / escalate hint.
// Write → single_confirm.
public sealed class CreateLearningHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "error_pattern":{"type":"string","description":"Regex or substring that identifies the tool error"},
          "error_category":{"type":"string","description":"Error category label (default unknown)"},
          "service_type":{"type":"string","description":"Service type this applies to (empty = any)"},
          "tool_name":{"type":"string","description":"Tool this applies to (empty = any)"},
          "fix_strategy":{"type":"string","enum":["parameter_adjust","escalate"],"description":"parameter_adjust (default) merges a param delta; escalate hands off"},
          "fix_params":{"type":"object","description":"Param delta to merge (parameter_adjust) or an escalate hint object"}
        },"required":["error_pattern"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateLearningHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "create_learning";
    public string Description =>
        "Curates an agent self-correction learning (a fix for a recurring tool error). error_pattern is " +
        "required (regex or substring); fix_strategy is parameter_adjust or escalate. Returns the new id.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var errorPattern = Str(args, "error_pattern")?.Trim();
        if (string.IsNullOrWhiteSpace(errorPattern)) return Err("error_pattern is required");
        var strategy = NormalizeStrategy(Str(args, "fix_strategy"));

        var now = DateTime.UtcNow;
        var row = new LearningEntity
        {
            AgentLearningId = Guid.NewGuid(),
            ErrorPattern = errorPattern,
            ErrorCategory = Str(args, "error_category") ?? "unknown",
            ServiceType = Str(args, "service_type") ?? string.Empty,
            ToolName = Str(args, "tool_name") ?? string.Empty,
            FixStrategy = strategy,
            FixParamsJson = RawJson(args, "fix_params"),
            Category = LearningEntity.CategoryKnowledge,
            Confidence = 0.8,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AgentLearnings.Add(row);
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { agent_learning_id = row.AgentLearningId, error_pattern = row.ErrorPattern });
    }

    // Shared with update_learning. Mirrors LearningController.NormalizeStrategy.
    internal static string NormalizeStrategy(string? raw)
    {
        var v = (raw ?? LearningEntity.StrategyParameterAdjust).Trim().ToLowerInvariant();
        return v == LearningEntity.StrategyEscalate ? LearningEntity.StrategyEscalate : LearningEntity.StrategyParameterAdjust;
    }

    // Raw JSON text of a provided object arg — mirrors dto.FixParams?.GetRawText().
    internal static string? RawJson(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v.GetRawText() : null;

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
