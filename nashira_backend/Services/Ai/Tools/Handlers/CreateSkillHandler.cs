using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Identity;
using SkillEntity = nashira_backend.Data.Models.AiPromptSkill;
using ValidationRecordEntity = nashira_backend.Data.Models.ValidationRecord;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Creates a tenant prompt skill. Content is security-validated + recorded before save,
// name must be unique, and a successful write hot-reloads the agent prompt. Write → single_confirm.
public sealed class CreateSkillHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string"},
          "content":{"type":"string","description":"Skill body (markdown) merged into the system prompt"},
          "priority":{"type":"integer","description":"Load order; lower loads earlier (default 100)"}
        },"required":["name","content"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ITemplateSecurityValidator _validator;
    private readonly IValidationRecorder _recorder;
    private readonly ISkillPromptLoader _loader;

    public CreateSkillHandler(
        AppDbContext db, ICurrentUser user, ITemplateSecurityValidator validator,
        IValidationRecorder recorder, ISkillPromptLoader loader)
    {
        _db = db;
        _user = user;
        _validator = validator;
        _recorder = recorder;
        _loader = loader;
    }

    public string Name => "create_skill";
    public string Description =>
        "Creates a tenant prompt skill (system-prompt building block). Content is security-validated before " +
        "save and the name must be unique; a successful write hot-reloads the agent prompt. Returns the new id.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("name is required");
        var content = Str(args, "content") ?? string.Empty;

        if (await ValidateAndRecordAsync(name, content, ct) is { } validationError) return validationError;

        if (await _db.AiPromptSkills.AnyAsync(s => s.Name == name && s.IsActive, ct))
            return Err("a skill with this name already exists");

        var now = DateTime.UtcNow;
        var row = new SkillEntity
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = name,
            Content = content,
            Priority = Int(args, "priority") ?? 100,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AiPromptSkills.Add(row);
        await _db.SaveChangesAsync(ct);
        _loader.Invalidate();
        return JsonSerializer.SerializeToElement(new { ai_prompt_skill_id = row.AiPromptSkillId, name = row.Name });
    }

    // Validate the content, persist the validation record, and reject on error-severity issues.
    // Shared shape with update_skill. Returns an Err element to surface, or null when ok.
    private async Task<JsonElement?> ValidateAndRecordAsync(string name, string content, CancellationToken ct)
    {
        var result = _validator.ValidateSkill(name, content);
        await _recorder.RecordAsync(ValidationRecordEntity.KindSkill, name, result, ct);
        if (result.Ok) return null;
        var errors = string.Join("; ", result.Issues
            .Where(i => i.Severity == TemplateSecurityValidator.Error).Select(i => i.Message));
        return Err($"skill failed security validation: {errors}");
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
