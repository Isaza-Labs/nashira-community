using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Moves messages to another folder. Reversible by moving back, but it reshapes
// the user's mailbox → write / single_confirm.
public sealed class MoveEmailHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "channel":{"type":"string","description":"Email channel slug or id; omitted, the oldest enabled channel with IMAP configured is used"},
          "folder":{"type":"string","description":"Source folder (default INBOX)"},
          "uids":{"type":"array","items":{"type":"integer"},"description":"Message uid(s) from list_emails"},
          "target_folder":{"type":"string","description":"Destination folder; must exist (see list_email_folders)"}
        },"required":["uids","target_folder"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IEmailChannelMailbox _mailbox;

    public MoveEmailHandler(AppDbContext db, IEmailChannelMailbox mailbox)
    {
        _db = db;
        _mailbox = mailbox;
    }

    public string Name => "move_email";
    public string Description =>
        "Moves email messages to another existing folder by the uids list_emails returned. For the " +
        "archive folder specifically, prefer archive_email, which finds or creates it.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var (uids, uidError) = MailboxToolSupport.ReadUids(args);
        if (uidError is not null) return MailboxToolSupport.Err(uidError);

        var target = MailboxToolSupport.Str(args, "target_folder");
        if (string.IsNullOrWhiteSpace(target))
            return MailboxToolSupport.Err("`target_folder` is required");

        var (channel, error) = await MailboxToolSupport.ResolveChannelAsync(_db, args, ct);
        if (channel is null) return MailboxToolSupport.Err(error!);

        try
        {
            var moved = await _mailbox.MoveAsync(
                channel, MailboxToolSupport.Str(args, "folder"), uids, target!, ct);
            return JsonSerializer.SerializeToElement(new
            {
                channel = channel.Slug,
                moved,
                requested = uids.Count,
                target_folder = target,
            });
        }
        catch (DomainException ex)
        {
            return MailboxToolSupport.Err(ex.Message);
        }
    }
}
