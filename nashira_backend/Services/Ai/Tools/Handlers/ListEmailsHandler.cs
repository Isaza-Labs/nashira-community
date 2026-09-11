using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists mailbox messages over IMAP on an email channel. Read → autonomous.
//
// This is the entry point of the mailbox tool family: everything else
// (read_email, archive_email, delete_email, …) operates on the uids this
// returns, so the summary carries exactly what a triage decision needs and no
// body text — read_email is the tool that pays for a body.
public sealed class ListEmailsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "channel":{"type":"string","description":"Email channel slug or id; omitted, the oldest enabled channel with IMAP configured is used"},
          "folder":{"type":"string","description":"Mailbox folder (default INBOX); see list_email_folders"},
          "unread_only":{"type":"boolean","default":false},
          "from_contains":{"type":"string","description":"Only messages whose From contains this text"},
          "subject_contains":{"type":"string","description":"Only messages whose Subject contains this text"},
          "text_contains":{"type":"string","description":"Only messages whose body contains this text (server-side search)"},
          "since_days":{"type":"integer","minimum":1,"description":"Only messages delivered in the last N days"},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":20}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IEmailChannelMailbox _mailbox;

    public ListEmailsHandler(AppDbContext db, IEmailChannelMailbox mailbox)
    {
        _db = db;
        _mailbox = mailbox;
    }

    public string Name => "list_emails";
    public string Description =>
        "Lists messages in a mailbox folder of an email channel (IMAP), newest first, with optional " +
        "unread/from/subject/text/date filters. Returns per message the uid used by read_email, " +
        "mark_email, move_email, archive_email and delete_email. Needs a channel with IMAP configured.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var (channel, error) = await MailboxToolSupport.ResolveChannelAsync(_db, args, ct);
        if (channel is null) return MailboxToolSupport.Err(error!);

        var sinceDays = MailboxToolSupport.Int(args, "since_days", 0);
        var query = new EmailMailboxQuery
        {
            Folder = MailboxToolSupport.Str(args, "folder"),
            UnreadOnly = MailboxToolSupport.Bool(args, "unread_only", false),
            FromContains = MailboxToolSupport.Str(args, "from_contains"),
            SubjectContains = MailboxToolSupport.Str(args, "subject_contains"),
            TextContains = MailboxToolSupport.Str(args, "text_contains"),
            Since = sinceDays > 0 ? DateTimeOffset.UtcNow.AddDays(-sinceDays) : null,
            Limit = Math.Clamp(MailboxToolSupport.Int(args, "limit", 20), 1, 100),
        };

        try
        {
            var messages = await _mailbox.ListAsync(channel, query, ct);
            return JsonSerializer.SerializeToElement(new
            {
                channel = channel.Slug,
                folder = query.Folder ?? "INBOX",
                count = messages.Count,
                messages = messages.Select(MailboxToolSupport.Project).ToList(),
            });
        }
        catch (DomainException ex)
        {
            return MailboxToolSupport.Err(ex.Message);
        }
    }
}
