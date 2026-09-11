using System.Text.Json;

namespace nashira_backend.Services.Ai.Providers;

// Provider-agnostic message/tool/result shapes. Each concrete provider maps
// these to/from its own wire format.

public sealed class LlmMessage
{
    public required string Role { get; init; } // "system" | "user" | "assistant" | "tool"
    public string? Content { get; init; }
    public List<ToolCallResult>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
}

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement ParametersSchema { get; init; }
}

public sealed class ToolCallResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
    // Opaque provider token that has to travel back with the call when the history
    // is replayed. Only Gemini mints one (a "thoughtSignature": an encrypted handle
    // on the reasoning behind the call, which its 2.5+ models reject the next turn
    // without); the other providers leave it null. Never parsed, only echoed.
    public string? ThoughtSignature { get; init; }
}

public sealed class ChatResult
{
    public required string Content { get; init; }
    public List<ToolCallResult>? ToolCalls { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public string? StopReason { get; init; }
}

public sealed class ChatStreamEvent
{
    public required string Type { get; init; } // "text_delta" | "tool_call" | "done" | "error"
    public string? TextDelta { get; init; }
    public ToolCallResult? ToolCall { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public string? Error { get; init; }
    // Why the model stopped, carried on "done" events when the provider said
    // ("stop", "tool_calls", "length", ...). Null when the stream ended without
    // saying. "length" is the one the runner acts on: it means the answer is not
    // finished, and nothing in the text itself shows that.
    public string? StopReason { get; init; }
}
