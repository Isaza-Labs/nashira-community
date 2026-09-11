namespace nashira_backend.Data.Models;

// A tenant-authored system-prompt skill (.md content), merged into the agent prompt
// by SkillPromptLoader after the built-in file skills, ordered by Priority then Name.
public class AiPromptSkill : BaseModel
{
    public Guid AiPromptSkillId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;   // lower loads earlier

    // UserId of the admin who created/last edited this row. Null on rows written
    // before the column existed (or by an unauthenticated bootstrap path).
    public Guid? CreatedBy { get; set; }

    // Optional link to an integration. When set, the agent treats this skill as
    // scoped to that integration — the operations it describes are expected to
    // target that integration's base URL and credentials. Null = global skill.
    //
    // Deliberately an unconstrained Guid?, not an FK: Nashira has no Integration
    // table yet (specs carry their own BaseUrl/AuthType/AuthConfig instead). The
    // column + index exist so scoping works the day that table lands, without a
    // second migration over a table the prompt loader reads on every turn.
    public Guid? IntegrationId { get; set; }
}
