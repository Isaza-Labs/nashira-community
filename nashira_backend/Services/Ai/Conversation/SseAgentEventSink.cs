using System.Text;
using System.Text.Json;
using nashira_backend.Services.Ai.Tools;

namespace nashira_backend.Services.Ai.Conversation;

// Writes each agent event as a Server-Sent Events frame: `data: {json}\n\n`.
// Every frame carries a `type` field the frontend switches on.
public sealed class SseAgentEventSink : IAgentEventSink
{
    private readonly Stream _out;

    public SseAgentEventSink(Stream output) => _out = output;

    private async Task WriteAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await _out.WriteAsync(bytes, ct);
        await _out.FlushAsync(ct);
    }

    public Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct)
        => WriteAsync(new { type = "conversation", conversation_id = conversationId, is_new = isNew }, ct);

    public Task ModelAsync(Guid providerId, string providerName, string model, CancellationToken ct)
        => WriteAsync(new { type = "model", ai_provider_id = providerId, provider = providerName, model }, ct);

    public Task TextAsync(string delta, CancellationToken ct)
        => WriteAsync(new { type = "token", delta }, ct);

    public Task ToolStartAsync(string name, JsonElement args, CancellationToken ct)
        => WriteAsync(new { type = "tool_start", name, args }, ct);

    public Task ToolResultAsync(string name, ToolCallOutput output, CancellationToken ct)
        => WriteAsync(new { type = "tool_result", name, success = output.Success, result = output.Result }, ct);

    public Task ConfirmationRequiredAsync(string name, JsonElement args, string tier, CancellationToken ct)
        => WriteAsync(new { type = "confirmation_required", name, args, tier }, ct);

    public Task DoneAsync(int tokensIn, int tokensOut, int iterations, CancellationToken ct)
        => WriteAsync(new { type = "done", tokens_in = tokensIn, tokens_out = tokensOut, iterations }, ct);

    public Task ErrorAsync(string message, string? code, CancellationToken ct)
        => WriteAsync(new { type = "error", message, code }, ct);
}
