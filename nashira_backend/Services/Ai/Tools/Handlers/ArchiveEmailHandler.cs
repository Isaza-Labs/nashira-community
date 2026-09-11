using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Archives messages: moves them to the server's archive folder (special-use if
// advertised, an existing "Archive" otherwise, created if the account has
// neither). Write / single_confirm.
public sealed class ArchiveEmailHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "channel":{"type":"string","description":"Email channel slug or id; omitted, the oldest enabled channel with IMAP configured is used"},
          "folder":{"type":"string","description":"Source folder (default INBOX)"},
          "uids":{"type":"array","items":{"type":"integer"},"description":"Message uid(s) from list_emails"}
        },"required":["uids"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IEmailChannelMailbox _mailbox;

    public ArchiveEmailHandler(AppDbContext db, IEmailChannelMailbox mailbox)
    {
        _db = db;
        _mailbox = mailbox;
    }

    public string Name => "archive_email";
    public string Description =>
        "Archives email messages (moves them to the account's archive folder, creating one when the " +
        "account has none) by the uids list_emails returned.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var (uids, uidError) = MailboxToolSupport.ReadUids(args);
        if (uidError is not null) return MailboxToolSupport.Err(uidError);

        var (channel, error) = await MailboxToolSupport.ResolveChannelAsync(_db, args, ct);
        if (channel is null) return MailboxToolSupport.Err(error!);

        try
        {
            var (moved, archiveFolder) = await _mailbox.ArchiveAsync(
                channel, MailboxToolSupport.Str(args, "folder"), uids, ct);
            return JsonSerializer.SerializeToElement(new
            {
                channel = channel.Slug,
                archived = moved,
                requested = uids.Count,
                archive_folder = archiveFolder,
            });
        }
        catch (DomainException ex)
        {
            return MailboxToolSupport.Err(ex.Message);
        }
    }
}
