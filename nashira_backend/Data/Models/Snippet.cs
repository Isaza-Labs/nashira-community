namespace nashira_backend.Data.Models;

// A reusable building block a workflow node invokes. `Type` selects the
// ISnippetHandler that runs it; `Code` carries the body for the handlers that
// need one (transform today).
//
// This is the row `workflow.v1`'s `snippet_id` has always pointed at. Until it
// existed, a conformant workflow could be stored and validated but not executed —
// ToolNodeExecutor read a `config_overrides.tool` binding instead, which is not in
// the schema and is not what another workflow.v1 implementation would send.
public class Snippet : BaseModel
{
    // Handler types. Free-form on the column so adding one does not need a
    // migration, but these are the ones with a handler registered today.
    public const string TypePing = "ping";
    public const string TypeRestCall = "rest_call";
    public const string TypeTransform = "transform";
    public const string TypeSsh = "ssh";
    public const string TypeIntegrationAction = "integration_action";
    public const string TypeMcpCall = "mcp_call";
    public const string TypePythonSnippet = "python_snippet";
    public const string TypeGit = "git";
    public const string TypeReport = "report";
    public const string TypeEmailSend = "email_send";
    public const string TypeEmailMailbox = "email_mailbox";
    public const string TypeSlackMessage = "slack_message";
    public const string TypeAnsiblePlaybook = "ansible_playbook";
    // Registered as stubs (mirroring FlowWeaver): the type exists so a workflow
    // referencing it fails with an actionable message instead of unknown_handler.
    public const string TypeNetconf = "netconf";
    public const string TypeSnmpV3 = "snmp_v3";

    // How a step maps onto the run's target devices.
    public const string TargetOnce = "once";
    public const string TargetPerDevice = "per_device";

    public Guid SnippetId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Stable cross-instance identity — see Integration.Slug. A workflow exported
    // from another instance names its snippets by slug, so a rename here must not
    // break a bundle already shared.
    public string Slug { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }

    // JSON Schema of the step's input and output. Advisory today: they document
    // the contract for the builder and the agent. Enforcing input against the
    // schema is a separate decision from having one.
    public string? InputSchemaJson { get; set; }
    public string? OutputSchemaJson { get; set; }

    // Body for handlers that carry logic (transform's JMESPath-ish expression).
    // Null for the handlers whose behaviour is fully described by their input.
    public string? Code { get; set; }
    public string? ScriptLanguage { get; set; }

    public string TargetMode { get; set; } = TargetOnce;
    public int TimeoutSeconds { get; set; } = 60;

    // JSON: { "max_attempts": 3, "delay_seconds": 2, "backoff": "exponential" }.
    // Null means one attempt, which is what every step did before this existed.
    //
    // Only a failure the handler marked retryable is retried. Re-running a step
    // that failed because its input was wrong just fails three times more slowly,
    // and re-running a non-idempotent step that already had an effect is worse
    // than failing — see RetryPolicy.
    public string? RetryPolicyJson { get; set; }

    // Explicit override of the handler's idempotency tier, as a string so a new
    // tier does not require a migration.
    //
    // Honoured except against a NonReversible handler, which is absolute — see
    // Idempotency.Effective. The override exists because the handler often cannot
    // tell: `rest_call` and `integration_action` default to RequiresCompensation
    // since a GET and a DELETE reach the same code, and only the author knows
    // which one their snippet wraps.
    public string? Idempotency { get; set; }

    // Admin-reviewed. Unverified snippets still run — this marks provenance for
    // the reviewer looking at a promotion, not an execution gate.
    /// <summary>
    /// Whether a step running this snippet changes anything. Null when the author has not
    /// said, which is only legal for types whose handler CAN say.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="Idempotency"/>, and a different question: that one says
    /// whether the action could be undone, this one whether anything was done. The handler
    /// answers where it can see the action; where the AUTHOR supplies it — a python script, a
    /// playbook — only the author can, and this is where they say so. For types whose action
    /// lives on the NODE (ssh commands, an mcp tool) the node's `config_overrides.changes`
    /// says it instead, and wins over this.
    ///
    /// Nullable and never backfilled: existing rows were written when the executor inferred
    /// the answer from the idempotency tier, and inventing a value for them would turn an
    /// inference into a record of a measurement nobody took.
    /// </remarks>
    public bool? ChangesState { get; set; }

    public bool Verified { get; set; }

    // Mermaid source describing what this step does. The point is that someone
    // opening a workflow six months later understands each node without reading
    // its payload.
    public string? LogicDiagramMermaid { get; set; }

    // python_snippet only: run with the network-capable modules on offer.
    //
    // Opt-in per snippet rather than a global setting, because an author who needs
    // `requests` for one step should not thereby hand every other snippet a socket.
    // It gates which modules the allowlist offers — it is NOT an OS-level network
    // jail; see PythonSandbox for what is and is not enforced.
    public bool NetworkEnabled { get; set; }

    public Guid? CreatedBy { get; set; }
}
