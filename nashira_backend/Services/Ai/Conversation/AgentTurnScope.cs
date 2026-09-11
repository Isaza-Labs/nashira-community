namespace nashira_backend.Services.Ai.Conversation;

// Scoped ambient for one chat turn: which AIConversation the tools being dispatched
// right now belong to. AgentConversationRunner stamps it before the first tool call;
// handlers that persist an artifact born in the chat (create_workflow today) read it
// so the row can point back at the conversation that produced it.
//
// Null on every non-chat path — the API and UI create workflows with no conversation,
// and that is a meaningful "authored by a human directly", not a missing value.
public sealed class AgentTurnScope
{
    public Guid? ConversationId { get; set; }

    // Files attached to the turn being processed, decoded once by the runner so tool
    // handlers (parse_file) can read them by filename without a round-trip. The
    // runner also persists them per conversation (conversation_attachments) at the
    // end of the turn, which is what lets parse_file re-read a file in LATER turns —
    // this dictionary only covers the current one.
    private readonly Dictionary<string, byte[]> _attachments = new(StringComparer.OrdinalIgnoreCase);

    public void AddAttachment(string filename, byte[] bytes) => _attachments[filename] = bytes;

    // Integrations whose skills are loaded in this conversation: what came in from the
    // conversation row plus whatever this turn loaded (a mention, an API call, the
    // load_skill tool). The runner persists the union at the end of the turn.
    private readonly HashSet<Guid> _loadedSkills = new();

    public IReadOnlyCollection<Guid> LoadedSkillIntegrationIds => _loadedSkills;

    // True when this call is what loaded it — false when it was already in.
    public bool MarkSkillLoaded(Guid integrationId) => _loadedSkills.Add(integrationId);

    public bool IsSkillLoaded(Guid integrationId) => _loadedSkills.Contains(integrationId);

    public bool TryGetAttachment(string filename, out byte[] bytes) =>
        _attachments.TryGetValue(filename, out bytes!);

    public IReadOnlyCollection<string> AttachmentNames => _attachments.Keys;

    public IReadOnlyDictionary<string, byte[]> Attachments => _attachments;
}
