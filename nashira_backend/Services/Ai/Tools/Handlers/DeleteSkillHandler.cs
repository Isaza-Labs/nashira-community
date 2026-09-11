using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Identity;
using SkillEntity = nashira_backend.Data.Models.AiPromptSkill;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes a prompt skill (resolved by id or name) and hot-reloads the agent
// prompt. Write → single_confirm.
public sealed class DeleteSkillHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "skill_id":{"type":"string","description":"Prompt skill id (or identify by name)"},
          "name":{"type":"string","description":"Identify the skill by name if skill_id is omitted"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISkillPromptLoader _loader;

    public DeleteSkillHandler(AppDbContext db, ICurrentUser user, ISkillPromptLoader loader)
    {
        _db = db;
        _user = user;
        _loader = loader;
    }

    public string Name => "delete_skill";
    public string Description => "Removes a prompt skill, identified by skill_id or name, and hot-reloads the agent prompt.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        SkillEntity? row = null;
        if (Str(args, "skill_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            row = await _db.AiPromptSkills.FirstOrDefaultAsync(
                s => s.AiPromptSkillId == id && s.IsActive, ct);
        else if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            row = await _db.AiPromptSkills.FirstOrDefaultAsync(
                s => s.Name == name && s.IsActive, ct);

        if (row is null) return Err("skill not found (pass a valid skill_id or name)");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _loader.Invalidate();
        return JsonSerializer.SerializeToElement(new { ai_prompt_skill_id = row.AiPromptSkillId, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
