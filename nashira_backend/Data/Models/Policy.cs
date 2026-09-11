namespace nashira_backend.Data.Models;

// A corporate guardrail, evaluated before a workflow runs or is promoted.
//
// `RuleJson` is a document rather than typed columns so the shape can grow without
// a migration. Today:
//
//   {
//     "action": "deny",
//     "reason": "core routers need a change window",
//     "when": {
//       "environment": ["production", "qa"],   // optional; any if absent
//       "device_role": ["core"],                // matches Device.Role
//       "device_pool": ["core-routers"],        // matches DevicePool.Name
//       "snippet_type": ["ssh"],                // matches Snippet.Type
//       "description_contains": ["bgp"]         // substring of the workflow description
//     }
//   }
//
// Deny-only, and deliberately so. Default-allow with opt-in denies is the model
// people already hold for firewalls, and it has the property that a policy nobody
// wrote cannot accidentally block work. A mixed allow/deny engine would need
// precedence rules, and precedence is where guardrails start being misread.
//
// Every clause inside `when` must match (AND), and a clause matches if ANY of its
// values does (OR). So adding a clause always narrows a rule — a policy cannot
// become broader by being made more specific.
// A second shape, evaluated at promotion rather than at run time:
//
//   {
//     "action": "gate",
//     "reason": "production needs a week of clean qa runs",
//     "on": "promote",
//     "from": "qa",
//     "to": "production",
//     "require": [
//       { "type": "successful_runs", "min": 3, "within_days": 14, "scope": "this_workflow" },
//       { "type": "last_successful_run_within", "days": 7 }
//     ]
//   }
//
// `within_days` on `successful_runs` is optional; without it the count has no
// horizon and is satisfied by runs from a year ago, which is not what anyone means
// by "three clean runs".
//
// A deny asks "is this forbidden?"; a gate asks "has this earned it yet?" — the
// difference is that a gate is satisfiable by doing the work, so it is a
// precondition rather than a prohibition. Every requirement must hold, and the
// refusal names all the unmet ones at once so an operator does not discover them
// one promotion at a time.
public class Policy : BaseModel
{
    public const string ActionDeny = "deny";
    public const string ActionGate = "gate";

    public static readonly string[] Actions = [ActionDeny, ActionGate];

    public Guid PolicyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string RuleJson { get; set; } = "{}";

    // Admin on/off switch, distinct from IsActive (soft delete): a guardrail can be
    // suspended for an incident without losing its definition.
    public bool Enabled { get; set; } = true;

    public Guid? CreatedBy { get; set; }
}
