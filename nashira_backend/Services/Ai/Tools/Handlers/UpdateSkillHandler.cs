using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Identity;
using SkillEntity = nashira_backend.Data.Models.AiPromptSkill;
using ValidationRecordEntity = nashira_backend.Data.Models.ValidationRecord;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates a prompt skill (resolved by id or name). Only provided fields change; changed
// content is security-validated + recorded, and a successful write hot-reloads the agent
// prompt. Write → single_confirm.
public sealed class UpdateSkillHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "skill_id":{"type":"string","description":"Prompt skill id (or identify by name)"},
          "name":{"type":"string","description":"Identify the skill by name if skill_id is omitted"},
          "new_name":{"type":"string","description":"Rename the skill"},
          "content":{"type":"string","description":"Replacement skill body (re-validated before save)"},
          "priority":{"type":"integer","description":"Load order; lower loads earlier"},
          "is_active":{"type":"boolean"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ITemplateSecurityValidator _validator;
    private readonly IValidationRecorder _recorder;
    private readonly ISkillPromptLoader _loader;

    public UpdateSkillHandler(
        AppDbContext db, ICurrentUser user, ITemplateSecurityValidator validator,
        IValidationRecorder recorder, ISkillPromptLoader loader)
    {
        _db = db;
        _user = user;
        _validator = validator;
        _recorder = recorder;
        _loader = loader;
    }

    public string Name => "update_skill";
    public string Description =>
        "Updates a prompt skill identified by skill_id or name. Only provided fields change; use new_name to " +
        "rename. Changed content is re-validated; a successful write hot-reloads the agent prompt.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("skill not found (pass a valid skill_id or name)");

        if (Str(args, "new_name")?.Trim() is { Length: > 0 } nn) row.Name = nn;
        if (args.TryGetProperty("content", out var cv) && cv.ValueKind == JsonValueKind.String)
        {
            var content = cv.GetString() ?? string.Empty;
            var result = _validator.ValidateSkill(row.Name, content);
            await _recorder.RecordAsync(ValidationRecordEntity.KindSkill, row.Name, result, ct);
            if (!result.Ok)
            {
                var errors = string.Join("; ", result.Issues
                    .Where(i => i.Severity == TemplateSecurityValidator.Error).Select(i => i.Message));
                return Err($"skill failed security validation: {errors}");
            }
            row.Content = content;
        }
        if (Int(args, "priority") is { } p) row.Priority = p;
        if (args.TryGetProperty("is_active", out var ia) && ia.ValueKind is JsonValueKind.True or JsonValueKind.False)
            row.IsActive = ia.GetBoolean();
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        _loader.Invalidate();
        return JsonSerializer.SerializeToElement(new { ai_prompt_skill_id = row.AiPromptSkillId, name = row.Name, updated = true });
    }

    private async Task<SkillEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "skill_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.AiPromptSkills.FirstOrDefaultAsync(
                s => s.AiPromptSkillId == id && s.IsActive, ct);
        if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            return await _db.AiPromptSkills.FirstOrDefaultAsync(
                s => s.Name == name && s.IsActive, ct);
        return null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
