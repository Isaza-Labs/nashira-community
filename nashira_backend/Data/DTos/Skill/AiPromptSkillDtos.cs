using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Skill;

public class CreateAiPromptSkill
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("priority")] public int? Priority { get; set; }
    [JsonPropertyName("integration_id")] public Guid? IntegrationId { get; set; }
}

public class UpdateAiPromptSkill
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("priority")] public int? Priority { get; set; }
    [JsonPropertyName("integration_id")] public Guid? IntegrationId { get; set; }
    // Explicit unlink — `integration_id: null` is indistinguishable from "absent"
    // on a PATCH-shaped DTO, so clearing the scope needs its own flag.
    [JsonPropertyName("clear_integration")] public bool? ClearIntegration { get; set; }
    [JsonPropertyName("is_active")] public bool? IsActive { get; set; }
}

// One file in a bulk import. `name` is the filename as the browser reported it; the
// extension is stripped server-side so `netbox-troubleshooting.md` becomes a skill
// named "netbox-troubleshooting" whichever way it was exported.
public class ImportSkillFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("priority")] public int? Priority { get; set; }
}

public class ImportSkillsRequest
{
    [JsonPropertyName("files")] public List<ImportSkillFile> Files { get; set; } = [];

    // Whether a name that already exists is updated or refused. Defaults to refusing:
    // an import that silently overwrites hand-tuned prompts is a bad surprise, and the
    // per-file result says exactly which ones would need it.
    [JsonPropertyName("overwrite")] public bool? Overwrite { get; set; }

    // Applied to every file that does not carry its own.
    [JsonPropertyName("priority")] public int? Priority { get; set; }
    [JsonPropertyName("integration_id")] public Guid? IntegrationId { get; set; }
}

// Per file, because a bulk import that reports one aggregate number leaves the caller
// re-uploading everything to find out which one failed.
public class ImportSkillResult
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty; // created | updated | skipped | failed
    [JsonPropertyName("ai_prompt_skill_id")] public Guid? AiPromptSkillId { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public class ImportSkillsResponse
{
    [JsonPropertyName("created")] public int Created { get; set; }
    [JsonPropertyName("updated")] public int Updated { get; set; }
    [JsonPropertyName("skipped")] public int Skipped { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("results")] public List<ImportSkillResult> Results { get; set; } = [];
}

// A built-in file skill (Skills/*.md). Read-only: shipped with the backend and
// prepended to every tenant's system prompt — surfaced so admins can see the
// full prompt composition, not just their uploaded rows.
public class BuiltinSkillResponse
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

public class UpdateBuiltinSkill
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

public class AiPromptSkillResponse
{
    [JsonPropertyName("ai_prompt_skill_id")] public Guid AiPromptSkillId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("integration_id")] public Guid? IntegrationId { get; set; }
    [JsonPropertyName("created_by")] public Guid? CreatedBy { get; set; }
    [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}
