namespace nashira_backend.Data.Models;

// A named group of devices, resolved from explicit members plus optional filter
// rules (site / role / vendor / platform / status).
//
// The pool's own allow trio is applied ON TOP of each member's, not instead of it:
// a run must be permitted by the pool AND by the device. A pool open to production
// therefore still only reaches the members that individually allow production —
// which is what makes a pool safe to widen without auditing every member.
public class DevicePool : BaseModel
{
    public Guid DevicePoolId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }

    // JSON: { "site": "...", "role": "...", "vendor": "...", "platform": "...",
    //         "status": "..." }. Matching is exact and case-insensitive; every
    //         present key must match (AND), because a pool that widened as rules
    //         were added would be the opposite of what an operator expects.
    public string? FilterRulesJson { get; set; }

    // Device ids always in the pool regardless of the rules. JSON array.
    public string StaticMembersJson { get; set; } = "[]";

    public bool AllowDraft { get; set; } = true;
    public bool AllowQa { get; set; }
    public bool AllowProduction { get; set; } = true;

    public Guid? CreatedBy { get; set; }
}
