using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Sets or clears the seen flag on messages. Mutates server state visible in the
// user's mail client → write / single_confirm.
public sealed class MarkEmailHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "channel":{"type":"string","description":"Email channel slug or id; omitted, the oldest enabled channel with IMAP configured is used"},
          "folder":{"type":"string","description":"Mailbox folder (default INBOX)"},
          "uids":{"type":"array","items":{"type":"integer"},"description":"Message uid(s) from list_emails"},
          "seen":{"type":"boolean","default":true,"description":"true = mark read, false = mark unread"}
        },"required":["uids"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IEmailChannelMailbox _mailbox;

    public MarkEmailHandler(AppDbContext db, IEmailChannelMailbox mailbox)
    {
        _db = db;
        _mailbox = mailbox;
    }

    public string Name => "mark_email";
    public string Description =>
        "Marks email messages as read (seen=true, the default) or unread (seen=false) by the uids " +
        "list_emails returned.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var (uids, uidError) = MailboxToolSupport.ReadUids(args);
        if (uidError is not null) return MailboxToolSupport.Err(uidError);

        var (channel, error) = await MailboxToolSupport.ResolveChannelAsync(_db, args, ct);
        if (channel is null) return MailboxToolSupport.Err(error!);

        var seen = MailboxToolSupport.Bool(args, "seen", true);
        try
        {
            var affected = await _mailbox.MarkAsync(
                channel, MailboxToolSupport.Str(args, "folder"), uids, seen, ct);
            return JsonSerializer.SerializeToElement(new
            {
                channel = channel.Slug,
                marked = affected,
                requested = uids.Count,
                seen,
            });
        }
        catch (DomainException ex)
        {
            return MailboxToolSupport.Err(ex.Message);
        }
    }
}
