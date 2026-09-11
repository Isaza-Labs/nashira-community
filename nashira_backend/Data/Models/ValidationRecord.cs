namespace nashira_backend.Data.Models;

// History of template (skill/spec) validations run by the loader. Tenant-scoped.
public class ValidationRecord : BaseModel
{
    public const string KindSkill = "skill";
    public const string KindSpec = "spec";

    public Guid ValidationRecordId { get; set; }
    public string Kind { get; set; } = string.Empty;        // skill | spec
    public string TargetName { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? IssuesJson { get; set; }                 // serialized TemplateIssue[]
    public Guid? UserId { get; set; }
}
