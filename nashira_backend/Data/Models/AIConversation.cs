namespace nashira_backend.Data.Models;

// A chat thread. Messages are stored as a JSON array of {role, content} turns
// (user/assistant); tool round-trips happen within a turn and are not persisted
// to history. Tenant-scoped; owned by a user.
public class AIConversation : BaseModel
{
    public Guid AIConversationId { get; set; }
    public Guid UserId { get; set; }
    public string? Title { get; set; }
    public string MessagesJson { get; set; } = "[]";
    public string Status { get; set; } = "active";
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }

    // Which provider and model answered this thread. Written on every turn, so a
    // follow-up sent without an explicit pick keeps talking to the same brain
    // instead of silently falling back to the tenant default halfway through a
    // conversation. Null on rows written before the chat could choose; those
    // resolve to the default exactly as they did then.
    public Guid? AIProviderId { get; set; }
    public string? Model { get; set; }

    // Tools the user has confirmed inside this conversation, as a JSON array of names.
    //
    // Approval used to last exactly one request, so a conversation spent entirely in
    // NetBox asked for the same permission on every single message — which trains people
    // to approve without reading, the opposite of what a confirmation is for. Consent
    // now lasts as long as the conversation it was given in, and no longer: a new chat
    // starts from nothing.
    public string? ApprovedToolsJson { get; set; }

    // Integrations whose skills have been loaded into this conversation, as a JSON
    // array of integration ids. A scoped skill enters the prompt when its integration
    // is in play and stays for the rest of the conversation — the same shape as
    // approvals: the set only grows, and a new chat starts from nothing.
    public string? LoadedSkillsJson { get; set; }

    // Where this conversation came from: null/"web" for the browser chat, or the
    // provider name of the messaging channel that opened it. Set together with the
    // two fields below, which are what let a Slack thread keep its history across
    // turns instead of starting a new conversation on every message.
    public string? Source { get; set; }

    public Guid? MessagingChannelId { get; set; }

    // The provider's own thread identity (Slack "channel:thread_ts", Telegram
    // chat_id, Teams "serviceUrl::conversationId").
    public string? ExternalThreadId { get; set; }
}
