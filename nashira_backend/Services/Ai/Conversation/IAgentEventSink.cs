using System.Text.Json;
using nashira_backend.Services.Ai.Tools;

namespace nashira_backend.Services.Ai.Conversation;

// Transport-agnostic sink for the agent loop's events. The web chat wraps an
// SseAgentEventSink; other channels can provide their own.
public interface IAgentEventSink
{
    Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct);
    // Which provider and model are about to answer. Emitted after resolution, so
    // it cannot ride along on the conversation frame: that one goes out first,
    // because the client needs the id before anything can fail. A turn that picks
    // up the conversation's stored choice reports it here too, which is how the
    // browser's selector stays truthful without asking.
    Task ModelAsync(Guid providerId, string providerName, string model, CancellationToken ct);
    Task TextAsync(string delta, CancellationToken ct);
    Task ToolStartAsync(string name, JsonElement args, CancellationToken ct);
    Task ToolResultAsync(string name, ToolCallOutput output, CancellationToken ct);
    // The agent wants to run a tool that requires the user's confirmation; the turn
    // pauses here until the user re-sends with the tool approved.
    Task ConfirmationRequiredAsync(string name, JsonElement args, string tier, CancellationToken ct);
    Task DoneAsync(int tokensIn, int tokensOut, int iterations, CancellationToken ct);
    Task ErrorAsync(string message, string? code, CancellationToken ct);
}
