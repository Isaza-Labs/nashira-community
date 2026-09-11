using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists the tenant's prompt skills (system-prompt building blocks), ordered like the
// admin API (priority, then name). Read → autonomous.
public sealed class ListSkillsHandler : IToolHandler
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListSkillsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_skills";
    public string Description =>
        "Lists tenant-authored prompt skills (id, name, priority, content) merged into the agent system " +
        "prompt, ordered by priority then name. Use the id with get_skill / update_skill / delete_skill.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rows = await _db.AiPromptSkills.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Priority).ThenBy(s => s.Name)
            .Select(s => new
            {
                ai_prompt_skill_id = s.AiPromptSkillId,
                name = s.Name,
                priority = s.Priority,
                content = s.Content,
                created_at = s.CreatedAt,
                updated_at = s.UpdatedAt,
            })
            .ToListAsync(ct);
        return JsonSerializer.SerializeToElement(new { skills = rows, count = rows.Count });
    }
}
