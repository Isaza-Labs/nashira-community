using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists the folders of a channel's mailbox. Read → autonomous.
public sealed class ListEmailFoldersHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "channel":{"type":"string","description":"Email channel slug or id; omitted, the oldest enabled channel with IMAP configured is used"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IEmailChannelMailbox _mailbox;

    public ListEmailFoldersHandler(AppDbContext db, IEmailChannelMailbox mailbox)
    {
        _db = db;
        _mailbox = mailbox;
    }

    public string Name => "list_email_folders";
    public string Description =>
        "Lists the mailbox folders of an email channel (IMAP) with message and unread counts. " +
        "Use the folder names with list_emails, move_email and the other mailbox tools.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var (channel, error) = await MailboxToolSupport.ResolveChannelAsync(_db, args, ct);
        if (channel is null) return MailboxToolSupport.Err(error!);

        try
        {
            var folders = await _mailbox.ListFoldersAsync(channel, ct);
            return JsonSerializer.SerializeToElement(new
            {
                channel = channel.Slug,
                count = folders.Count,
                folders = folders.Select(f => new { name = f.Name, messages = f.Count, unread = f.Unread }),
            });
        }
        catch (DomainException ex)
        {
            return MailboxToolSupport.Err(ex.Message);
        }
    }
}
