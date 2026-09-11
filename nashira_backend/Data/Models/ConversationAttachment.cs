namespace nashira_backend.Data.Models;

// A file attached to a chat conversation, kept so parse_file can re-read it in any
// later turn without the user re-uploading it. Content is the raw bytes as sent;
// one row per (conversation, filename) — re-attaching the same name overwrites,
// which is also how "here is the corrected version" should behave. Rows are
// hard-deleted with their conversation: file content has no business outliving
// the thread it was shared in.
public class ConversationAttachment : BaseModel
{
    // Matches parse_file's read cap — storing more than the reader will accept
    // only wastes rows.
    public const int MaxBytes = 5 * 1024 * 1024;

    // Per-conversation cap; the oldest-touched row is evicted past it, so a long
    // thread cannot grow the table unbounded.
    public const int MaxPerConversation = 20;

    public Guid ConversationAttachmentId { get; set; }
    public Guid ConversationId { get; set; }
    public string Filename { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public int SizeBytes { get; set; }
}
