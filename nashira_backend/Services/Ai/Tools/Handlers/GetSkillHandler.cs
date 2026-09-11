using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using SkillEntity = nashira_backend.Data.Models.AiPromptSkill;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Details of one prompt skill (resolved by id or unique name). Read → autonomous.
public sealed class GetSkillHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "skill_id":{"type":"string","description":"Prompt skill id (or identify by name)"},
          "name":{"type":"string","description":"Skill name if no id"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public GetSkillHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "get_skill";
    public string Description =>
        "Returns a prompt skill's details (name, content, priority, timestamps), resolved by skill_id or name.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("skill not found (pass a valid skill_id or name)");

        return JsonSerializer.SerializeToElement(new
        {
            ai_prompt_skill_id = row.AiPromptSkillId,
            name = row.Name,
            content = row.Content,
            priority = row.Priority,
            created_at = row.CreatedAt,
            updated_at = row.UpdatedAt,
        });
    }

    private async Task<SkillEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "skill_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.AiPromptSkills.AsNoTracking().FirstOrDefaultAsync(
                s => s.AiPromptSkillId == id && s.IsActive, ct);
        if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            return await _db.AiPromptSkills.AsNoTracking().FirstOrDefaultAsync(
                s => s.Name == name && s.IsActive, ct);
        return null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
