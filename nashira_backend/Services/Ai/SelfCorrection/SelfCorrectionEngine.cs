using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace nashira_backend.Services.Ai.SelfCorrection;

// Correction suggested for a failed tool call.
public sealed record Correction(
    string Strategy, JsonElement? CorrectedArgs, string Source,
    Guid? LearningId, double Confidence, string? Message, string ErrorCategory, string? FixParamsJson);

public sealed record CorrectionAttempt(
    string ToolName, JsonElement OriginalArgs, string ErrorMessage, Correction Correction, bool Success);

// The three-layer self-correction loop (ported from nashira_agent self_correction.py):
//   1. a known fix from the learnings store (parameter_adjust or escalate hint),
//   2. a generic static fix (coerce numeric fields the error names to strings),
//   3. an escalate hint by error category.
// Scoped per chat turn; caps corrections per tool so a loop can't thrash.
public sealed class SelfCorrectionEngine
{
    public const string StrategyParameterAdjust = "parameter_adjust";
    public const string StrategyEscalate = "escalate";
    private const int MaxAttemptsPerTool = 3;

    private readonly ILearningsStore _store;
    private readonly ILogger<SelfCorrectionEngine> _logger;
    private readonly Dictionary<string, int> _attempts = new();

    public SelfCorrectionEngine(ILearningsStore store, ILogger<SelfCorrectionEngine> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<Correction?> SuggestCorrectionAsync(string toolName, JsonElement args, string errorMessage, CancellationToken ct)
    {
        if (_attempts.GetValueOrDefault(toolName) >= MaxAttemptsPerTool) return null;

        var category = ErrorClassifier.Classify(errorMessage);
        var serviceType = ReadServiceType(args);

        // 1. Known fix from the learnings store.
        var learning = await _store.FindBestFixAsync(toolName, serviceType, errorMessage, ct);
        if (learning is not null)
        {
            if (learning.FixStrategy == StrategyEscalate)
                return Bump(toolName, new Correction(StrategyEscalate, null, "learnings_store",
                    learning.AgentLearningId, learning.Confidence, ReadMessage(learning.FixParamsJson) ?? DefaultHint(category), category, null));

            var merged = MergeParams(args, learning.FixParamsJson);
            if (merged is { } m)
                return Bump(toolName, new Correction(StrategyParameterAdjust, m, "learnings_store",
                    learning.AgentLearningId, learning.Confidence, null, category, learning.FixParamsJson));
        }

        // 2. Generic static fix: coerce numeric/bool fields the error names to strings.
        var (coerced, delta) = TryCoerceStringFields(args, errorMessage, category);
        if (coerced is { } c)
            return Bump(toolName, new Correction(StrategyParameterAdjust, c, "static_rules", null, 0, null, category, delta));

        // 3. Escalate hint by category.
        var hint = DefaultHint(category);
        if (hint is not null)
            return Bump(toolName, new Correction(StrategyEscalate, null, "static_rules", null, 0, hint, category, null));

        return null;
    }

    public async Task RecordOutcomeAsync(CorrectionAttempt attempt, CancellationToken ct)
    {
        try
        {
            if (attempt.Correction.LearningId is { } id)
            {
                await _store.RecordOutcomeAsync(id, attempt.Success, ct);
            }
            else if (attempt.Success
                && attempt.Correction.Source == "static_rules"
                && attempt.Correction.Strategy == StrategyParameterAdjust
                && attempt.Correction.FixParamsJson is not null)
            {
                await _store.RecordDiscoveredAsync(
                    attempt.ErrorMessage, attempt.Correction.ErrorCategory, string.Empty,
                    attempt.ToolName, StrategyParameterAdjust, attempt.Correction.FixParamsJson, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "selfcorrect.record_outcome.failed tool={Tool}", attempt.ToolName);
        }
    }

    private Correction Bump(string toolName, Correction correction)
    {
        _attempts[toolName] = _attempts.GetValueOrDefault(toolName) + 1;
        return correction;
    }

    private static string? DefaultHint(string category) => category switch
    {
        ErrorClassifier.Duplicate => "The resource already exists — use the existing one instead of creating a duplicate.",
        ErrorClassifier.Timeout => "The operation timed out — retry later or check connectivity/reachability.",
        ErrorClassifier.Auth => "Authentication failed — check the credentials and permissions.",
        ErrorClassifier.Connection => "Could not connect — check service availability and network reachability.",
        ErrorClassifier.NotFound => "The target was not found — verify the id/name exists before retrying.",
        _ => null,
    };

    private static string ReadServiceType(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (var key in (string[])["service_type", "type", "platform"])
            if (args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string? ReadMessage(string? fixParamsJson)
    {
        if (string.IsNullOrEmpty(fixParamsJson)) return null;
        try
        {
            if (JsonNode.Parse(fixParamsJson) is JsonObject o)
                return (o["message"] ?? o["_note"])?.GetValue<string>();
        }
        catch (JsonException) { }
        return null;
    }

    // Merge a learning's param delta into the original args. Returns null when the
    // delta has no real params (only a _note/message), so we don't retry pointlessly.
    private static JsonElement? MergeParams(JsonElement args, string? fixParamsJson)
    {
        if (string.IsNullOrEmpty(fixParamsJson)) return null;
        JsonObject? fix;
        try { fix = JsonNode.Parse(fixParamsJson) as JsonObject; }
        catch (JsonException) { return null; }
        if (fix is null) return null;

        var realKeys = fix.Where(kv => kv.Key is not ("_note" or "message")).ToList();
        if (realKeys.Count == 0) return null;

        var obj = ToObject(args);
        foreach (var kv in realKeys) obj[kv.Key] = kv.Value?.DeepClone();
        return JsonSerializer.SerializeToElement(obj);
    }

    private static (JsonElement? corrected, string? delta) TryCoerceStringFields(JsonElement args, string errorMessage, string category)
    {
        if (category is not (ErrorClassifier.Validation or ErrorClassifier.FieldError)) return (null, null);
        if (args.ValueKind != JsonValueKind.Object) return (null, null);

        var fields = Regex.Matches(errorMessage, "'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (fields.Count == 0) return (null, null);

        var obj = ToObject(args);
        var delta = new JsonObject();
        var changed = false;
        foreach (var field in fields)
        {
            if (!args.TryGetProperty(field, out var pv)) continue;
            var coerced = pv.ValueKind switch
            {
                JsonValueKind.Number => pv.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (coerced is null) continue;
            obj[field] = JsonValue.Create(coerced);
            delta[field] = JsonValue.Create(coerced);
            changed = true;
        }
        return changed ? (JsonSerializer.SerializeToElement(obj), delta.ToJsonString()) : (null, null);
    }

    private static JsonObject ToObject(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && JsonNode.Parse(args.GetRawText()) is JsonObject o) return o;
        return new JsonObject();
    }
}
