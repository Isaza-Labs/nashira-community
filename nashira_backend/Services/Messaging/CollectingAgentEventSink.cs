using System.Text;
using System.Text.Json;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Tools;

namespace nashira_backend.Services.Messaging;

// Headless sink for an agent turn that has no HTTP response to stream into.
//
// The web chat wraps SseAgentEventSink and writes each delta to the wire as it
// arrives. A chat platform wants one finished message instead, so this collects
// the text deltas and hands back a single string once the turn ends.
//
// Confirmation is refused rather than deferred: a tool that needs the user to
// approve it has nowhere to ask on this transport — there is no dialog in a Slack
// thread — and silently proceeding would be exactly the escalation the whole
// permission model exists to prevent. The turn instead ends with a message telling
// the user to run that action from the web app.
public sealed class CollectingAgentEventSink : IAgentEventSink
{
    private readonly StringBuilder _text = new();

    public Guid ConversationId { get; private set; }
    public bool IsNewConversation { get; private set; }
    public string? Error { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ConfirmationRequiredTool { get; private set; }
    public int TokensIn { get; private set; }
    public int TokensOut { get; private set; }
    public int Iterations { get; private set; }

    public string FinalText => _text.ToString().Trim();

    public Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct)
    {
        ConversationId = conversationId;
        IsNewConversation = isNew;
        return Task.CompletedTask;
    }

    // Which model answered is a control for the web chat's selector. A Slack or
    // Telegram thread has no selector, and the choice is already on the
    // conversation row and in the turn history.
    public Task ModelAsync(Guid providerId, string providerName, string model, CancellationToken ct)
        => Task.CompletedTask;

    public Task TextAsync(string delta, CancellationToken ct)
    {
        _text.Append(delta);
        return Task.CompletedTask;
    }

    // Tool activity is not narrated back to the chat platform. A user in Slack
    // asked a question; a running commentary of every tool call is noise, and the
    // full record already lives in the turn history that /admin/sessions shows.
    public Task ToolStartAsync(string name, JsonElement args, CancellationToken ct) => Task.CompletedTask;

    public Task ToolResultAsync(string name, ToolCallOutput output, CancellationToken ct) => Task.CompletedTask;

    public Task ConfirmationRequiredAsync(string name, JsonElement args, string tier, CancellationToken ct)
    {
        ConfirmationRequiredTool = name;
        return Task.CompletedTask;
    }

    public Task DoneAsync(int tokensIn, int tokensOut, int iterations, CancellationToken ct)
    {
        TokensIn = tokensIn;
        TokensOut = tokensOut;
        Iterations = iterations;
        return Task.CompletedTask;
    }

    public Task ErrorAsync(string message, string? code, CancellationToken ct)
    {
        Error = message;
        ErrorCode = code;
        return Task.CompletedTask;
    }

    // What the external user should actually see. Order matters: a confirmation
    // request is not an error, but it does mean the turn produced no answer, and
    // saying so beats returning whatever partial text preceded it.
    public string ReplyText()
    {
        if (ConfirmationRequiredTool is not null)
            return $"That action ({ConfirmationRequiredTool}) needs confirmation, which cannot be given from "
                 + "this chat. Please run it from the Nashira web app.";

        if (!string.IsNullOrWhiteSpace(FinalText)) return FinalText;

        return Error is not null
            ? "Sorry — something went wrong handling your request. Please try again."
            : "(no response)";
    }
}
