using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Deletes messages: to the server's Trash by default (recoverable), permanently
// (flag + expunge) on request. Expunge cannot be undone by anyone, which is why
// this tool — unlike its mailbox siblings — sits at elevated_confirm.
public sealed class DeleteEmailHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "channel":{"type":"string","description":"Email channel slug or id; omitted, the oldest enabled channel with IMAP configured is used"},
          "folder":{"type":"string","description":"Mailbox folder (default INBOX)"},
          "uids":{"type":"array","items":{"type":"integer"},"description":"Message uid(s) from list_emails"},
          "permanent":{"type":"boolean","default":false,"description":"true = expunge immediately instead of moving to Trash; unrecoverable"}
        },"required":["uids"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IEmailChannelMailbox _mailbox;

    public DeleteEmailHandler(AppDbContext db, IEmailChannelMailbox mailbox)
    {
        _db = db;
        _mailbox = mailbox;
    }

    public string Name => "delete_email";
    public string Description =>
        "Deletes email messages by the uids list_emails returned. By default they go to the server's " +
        "Trash folder and can be recovered there; permanent=true expunges them irreversibly (also the " +
        "behaviour when the account has no Trash folder).";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var (uids, uidError) = MailboxToolSupport.ReadUids(args);
        if (uidError is not null) return MailboxToolSupport.Err(uidError);

        var (channel, error) = await MailboxToolSupport.ResolveChannelAsync(_db, args, ct);
        if (channel is null) return MailboxToolSupport.Err(error!);

        var permanent = MailboxToolSupport.Bool(args, "permanent", false);
        try
        {
            var (affected, outcome) = await _mailbox.DeleteAsync(
                channel, MailboxToolSupport.Str(args, "folder"), uids, permanent, ct);
            return JsonSerializer.SerializeToElement(new
            {
                channel = channel.Slug,
                deleted = affected,
                requested = uids.Count,
                outcome,
            });
        }
        catch (DomainException ex)
        {
            return MailboxToolSupport.Err(ex.Message);
        }
    }
}
