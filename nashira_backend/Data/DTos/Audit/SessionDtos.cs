using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Audit;

// One conversation as an administrator sees it: whose it is, how much of it there is,
// and what it cost. The transcript is not here — it is a click away, because a list of
// sessions is scanned and a transcript is read.
public class SessionSummary
{
    [JsonPropertyName("conversation_id")] public Guid ConversationId { get; set; }
    [JsonPropertyName("user_id")] public Guid? UserId { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("message_count")] public int MessageCount { get; set; }
    [JsonPropertyName("turn_count")] public int TurnCount { get; set; }
    [JsonPropertyName("tool_call_count")] public int ToolCallCount { get; set; }
    [JsonPropertyName("failed_turn_count")] public int FailedTurnCount { get; set; }
    [JsonPropertyName("tokens_in")] public int TokensIn { get; set; }
    [JsonPropertyName("tokens_out")] public int TokensOut { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class SessionMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

// One recorded turn: the prompt, the answer, and every tool call in between with the
// arguments that went out and what came back. Secret-bearing argument values are
// replaced before the row is written, never here.
public class SessionTurn
{
    [JsonPropertyName("agent_turn_id")] public Guid AgentTurnId { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("user_message")] public string UserMessage { get; set; } = string.Empty;
    [JsonPropertyName("assistant_text")] public string? AssistantText { get; set; }
    [JsonPropertyName("tool_calls")] public JsonElement ToolCalls { get; set; }
    [JsonPropertyName("tool_call_count")] public int ToolCallCount { get; set; }
    [JsonPropertyName("tokens_in")] public int TokensIn { get; set; }
    [JsonPropertyName("tokens_out")] public int TokensOut { get; set; }
    [JsonPropertyName("iterations")] public int Iterations { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("started_at")] public DateTime StartedAt { get; set; }
    [JsonPropertyName("elapsed_ms")] public int ElapsedMs { get; set; }
}

public class SessionDetail : SessionSummary
{
    [JsonPropertyName("messages")] public List<SessionMessage> Messages { get; set; } = [];
    [JsonPropertyName("turns")] public List<SessionTurn> Turns { get; set; } = [];
}
