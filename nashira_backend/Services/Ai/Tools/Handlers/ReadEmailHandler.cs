using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Fetches one full message by uid. Read → autonomous.
//
// The folder is opened read-only, so reading here does NOT mark the message as
// seen on the server — that is mark_email's job, on purpose: an agent skimming
// twenty messages to answer one question must not silently clear the user's
// unread state.
public sealed class ReadEmailHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "channel":{"type":"string","description":"Email channel slug or id; omitted, the oldest enabled channel with IMAP configured is used"},
          "folder":{"type":"string","description":"Mailbox folder (default INBOX)"},
          "uid":{"type":"integer","description":"Message uid from list_emails"}
        },"required":["uid"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IEmailChannelMailbox _mailbox;

    public ReadEmailHandler(AppDbContext db, IEmailChannelMailbox mailbox)
    {
        _db = db;
        _mailbox = mailbox;
    }

    public string Name => "read_email";
    public string Description =>
        "Reads one email message in full (headers, text/html body, attachment names) by the uid " +
        "list_emails returned. Reading does not mark the message as seen — use mark_email for that.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("uid", out var uidEl) || uidEl.ValueKind != JsonValueKind.Number
            || !uidEl.TryGetUInt32(out var uid))
            return MailboxToolSupport.Err("`uid` (a positive integer from list_emails) is required");

        var (channel, error) = await MailboxToolSupport.ResolveChannelAsync(_db, args, ct);
        if (channel is null) return MailboxToolSupport.Err(error!);

        try
        {
            var message = await _mailbox.GetAsync(channel, MailboxToolSupport.Str(args, "folder"), uid, ct);
            if (message is null)
                return MailboxToolSupport.Err(
                    $"message uid {uid} not found — it may have been moved or deleted; list_emails again");

            return JsonSerializer.SerializeToElement(new
            {
                channel = channel.Slug,
                uid = message.Uid,
                folder = message.Folder,
                from = message.From,
                to = message.To,
                cc = message.Cc,
                reply_to = message.ReplyTo,
                subject = message.Subject,
                date = message.Date,
                seen = message.Seen,
                text_body = message.TextBody,
                html_body = message.HtmlBody,
                body_truncated = message.BodyTruncated,
                attachments = message.Attachments.Select(a => new
                {
                    file_name = a.FileName,
                    content_type = a.ContentType,
                    size_bytes = a.Size,
                }),
            });
        }
        catch (DomainException ex)
        {
            return MailboxToolSupport.Err(ex.Message);
        }
    }
}
