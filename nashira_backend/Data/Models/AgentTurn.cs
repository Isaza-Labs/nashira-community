namespace nashira_backend.Data.Models;

// One agent turn, recorded for forensics: what the user asked, which model answered,
// every tool it called with the arguments and what came back, and how the turn ended.
//
// This is the layer the audit trail could not provide. AuditEvent records *mutations* —
// it is the tamper-evident record of what changed — so a turn that read twenty things
// and changed nothing leaves no trace there at all, and the tool results never do. When
// an operator asks "what did the agent actually do when it said NetBox was unreachable",
// this is the row that answers.
//
// Deliberately NOT a BaseModel: like AuditEvent, a turn is append-only — never updated,
// never soft-deleted — so it carries no IsActive/UpdatedAt.
//
// It is telemetry, not the conversation: deleting a conversation does not delete its
// turns, which is the point of keeping them separately.
public class AgentTurn
{
    public const string StatusCompleted = "completed";
    public const string StatusAwaitingConfirmation = "awaiting_confirmation";
    public const string StatusError = "error";
    public const string StatusTimeout = "timeout";
    // The model stopped because it ran out of output tokens, not because it was
    // finished. The text is persisted as it stands, with a notice appended; the
    // status is what lets /admin/sessions tell "answered" from "ran out of room".
    public const string StatusTruncated = "truncated";

    public Guid AgentTurnId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? UserId { get; set; }

    public string Model { get; set; } = string.Empty;

    // What the user sent and what the agent finally answered. Both stored capped:
    // an attachment inlined into the message can be 128 KB on its own.
    public string UserMessage { get; set; } = string.Empty;
    public string? AssistantText { get; set; }

    // [{ name, arguments, ok, result, elapsed_ms }] — the exact payloads, with
    // secret-bearing argument values redacted (see ToolTelemetry).
    public string ToolCallsJson { get; set; } = "[]";
    public int ToolCallCount { get; set; }

    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public int Iterations { get; set; }

    public string Status { get; set; } = StatusCompleted;
    public string? Error { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public int ElapsedMs { get; set; }
}
