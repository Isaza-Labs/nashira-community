using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using nashira_backend.Exceptions;
using nashira_backend.Services.Security;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Services.Email;

public sealed record EmailFolderInfo(string Name, int Count, int Unread);

public sealed record EmailSummary(
    uint Uid,
    string Folder,
    string From,
    string Subject,
    DateTimeOffset? Date,
    bool Seen,
    bool Flagged,
    bool HasAttachments);

public sealed record EmailAttachmentInfo(string FileName, string ContentType, long? Size);

public sealed class EmailFullMessage
{
    public required uint Uid { get; init; }
    public required string Folder { get; init; }
    public string From { get; init; } = string.Empty;
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public string? ReplyTo { get; init; }
    public string Subject { get; init; } = string.Empty;
    public DateTimeOffset? Date { get; init; }
    public bool Seen { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public bool BodyTruncated { get; init; }
    public IReadOnlyList<EmailAttachmentInfo> Attachments { get; init; } = [];
}

// The filters a caller can combine when listing a mailbox. Everything is
// optional; the default is "the newest messages in INBOX".
public sealed class EmailMailboxQuery
{
    public string? Folder { get; init; }
    public bool UnreadOnly { get; init; }
    public string? FromContains { get; init; }
    public string? SubjectContains { get; init; }
    public string? TextContains { get; init; }
    public DateTimeOffset? Since { get; init; }
    public int Limit { get; init; } = 20;
}

public interface IEmailChannelMailbox
{
    Task<IReadOnlyList<EmailFolderInfo>> ListFoldersAsync(ChannelEntity channel, CancellationToken ct);
    Task<IReadOnlyList<EmailSummary>> ListAsync(ChannelEntity channel, EmailMailboxQuery query, CancellationToken ct);

    // Null when the uid does not exist (deleted or moved since it was listed).
    Task<EmailFullMessage?> GetAsync(ChannelEntity channel, string? folder, uint uid, CancellationToken ct);

    // All bulk operations return how many of the requested uids actually existed.
    Task<int> MarkAsync(
        ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, bool seen, CancellationToken ct);
    Task<int> MoveAsync(
        ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct);
    Task<(int Moved, string ArchiveFolder)> ArchiveAsync(
        ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, CancellationToken ct);
    Task<(int Affected, string Outcome)> DeleteAsync(
        ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, bool permanent, CancellationToken ct);
}

// The inbound (IMAP) counterpart of EmailChannelSender, on the same channel row.
// Same library for the same reason: MailKit expresses implicit TLS on 993
// honestly, and it is already in the project for SMTP.
//
// Reads open the folder read-only, so listing or fetching a message never flips
// \Seen behind the caller's back — mark is its own operation. Deletes prefer the
// server's Trash folder (recoverable) and only flag+expunge when the caller asks
// for `permanent` or the server advertises no Trash.
public sealed class EmailChannelMailbox : IEmailChannelMailbox
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    // A mailbox body can be megabytes of quoted history; the consumers here are
    // an LLM context and a workflow payload, and both are better served by a
    // bounded excerpt plus the truth that it was cut.
    private const int BodyCharCap = 50_000;
    private const int MaxListLimit = 100;

    private readonly ISecretProtector _protector;
    private readonly ILogger<EmailChannelMailbox> _logger;

    public EmailChannelMailbox(ISecretProtector protector, ILogger<EmailChannelMailbox> logger)
    {
        _protector = protector;
        _logger = logger;
    }

    public Task<IReadOnlyList<EmailFolderInfo>> ListFoldersAsync(ChannelEntity channel, CancellationToken ct) =>
        RunAsync<IReadOnlyList<EmailFolderInfo>>(channel, async (client, token) =>
        {
            var personal = client.GetFolder(client.PersonalNamespaces[0]);
            var folders = await personal.GetSubfoldersAsync(subscribedOnly: false, token);
            var list = new List<EmailFolderInfo>();
            foreach (var folder in Flatten(client.Inbox, folders))
            {
                if (folder.Attributes.HasFlag(FolderAttributes.NonExistent)
                    || folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                    continue;
                // Status() refreshes Count/Unread on the folder object itself.
                await folder.StatusAsync(StatusItems.Count | StatusItems.Unread, token);
                list.Add(new EmailFolderInfo(folder.FullName, folder.Count, folder.Unread));
            }
            return list;
        }, ct);

    public Task<IReadOnlyList<EmailSummary>> ListAsync(
        ChannelEntity channel, EmailMailboxQuery query, CancellationToken ct) =>
        RunAsync<IReadOnlyList<EmailSummary>>(channel, async (client, token) =>
        {
            var folder = await OpenAsync(client, query.Folder, FolderAccess.ReadOnly, token);
            var uids = await folder.SearchAsync(BuildQuery(query), token);
            var limit = Math.Clamp(query.Limit, 1, MaxListLimit);

            // Uids ascend with arrival; the newest messages are the interesting end.
            var wanted = uids.OrderByDescending(u => u.Id).Take(limit).ToList();
            if (wanted.Count == 0) return [];

            var summaries = await folder.FetchAsync(wanted,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope
                | MessageSummaryItems.Flags | MessageSummaryItems.BodyStructure, token);

            return summaries
                .OrderByDescending(s => s.UniqueId.Id)
                .Select(s => new EmailSummary(
                    s.UniqueId.Id,
                    folder.FullName,
                    s.Envelope?.From?.ToString() ?? string.Empty,
                    s.Envelope?.Subject ?? string.Empty,
                    s.Envelope?.Date,
                    s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                    s.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
                    s.Attachments.Any()))
                .ToList();
        }, ct);

    public Task<EmailFullMessage?> GetAsync(
        ChannelEntity channel, string? folderName, uint uid, CancellationToken ct) =>
        RunAsync<EmailFullMessage?>(channel, async (client, token) =>
        {
            var folder = await OpenAsync(client, folderName, FolderAccess.ReadOnly, token);
            var id = new UniqueId(uid);
            var summaries = await folder.FetchAsync(new[] { id },
                MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, token);
            if (summaries.Count == 0) return null;

            var message = await folder.GetMessageAsync(id, token);

            var text = Cap(message.TextBody, out var textCut);
            var html = Cap(message.HtmlBody, out var htmlCut);

            return new EmailFullMessage
            {
                Uid = uid,
                Folder = folder.FullName,
                From = message.From.ToString(),
                To = message.To.Select(a => a.ToString()).ToList(),
                Cc = message.Cc.Select(a => a.ToString()).ToList(),
                ReplyTo = message.ReplyTo.Count > 0 ? message.ReplyTo.ToString() : null,
                Subject = message.Subject ?? string.Empty,
                Date = message.Date,
                Seen = summaries[0].Flags?.HasFlag(MessageFlags.Seen) ?? false,
                TextBody = text,
                HtmlBody = html,
                BodyTruncated = textCut || htmlCut,
                Attachments = message.Attachments
                    .OfType<MimeKit.MimePart>()
                    .Select(p => new EmailAttachmentInfo(
                        p.FileName ?? "unnamed",
                        p.ContentType?.MimeType ?? "application/octet-stream",
                        AttachmentSize(p)))
                    .ToList(),
            };
        }, ct);

    public Task<int> MarkAsync(
        ChannelEntity channel, string? folderName, IReadOnlyList<uint> uids, bool seen, CancellationToken ct) =>
        RunAsync(channel, async (client, token) =>
        {
            var folder = await OpenAsync(client, folderName, FolderAccess.ReadWrite, token);
            var (ids, found) = await ExistingAsync(folder, uids, token);
            if (found == 0) return 0;
            if (seen) await folder.AddFlagsAsync(ids, MessageFlags.Seen, silent: true, token);
            else await folder.RemoveFlagsAsync(ids, MessageFlags.Seen, silent: true, token);
            return found;
        }, ct);

    public Task<int> MoveAsync(
        ChannelEntity channel, string? folderName, IReadOnlyList<uint> uids, string targetFolder,
        CancellationToken ct) =>
        RunAsync(channel, async (client, token) =>
        {
            var folder = await OpenAsync(client, folderName, FolderAccess.ReadWrite, token);
            var target = await FindFolderAsync(client, targetFolder, token)
                ?? throw new ValidationException($"mailbox folder '{targetFolder}' does not exist");
            var (ids, found) = await ExistingAsync(folder, uids, token);
            if (found == 0) return 0;
            await folder.MoveToAsync(ids, target, token);
            return found;
        }, ct);

    public Task<(int Moved, string ArchiveFolder)> ArchiveAsync(
        ChannelEntity channel, string? folderName, IReadOnlyList<uint> uids, CancellationToken ct) =>
        RunAsync(channel, async (client, token) =>
        {
            var folder = await OpenAsync(client, folderName, FolderAccess.ReadWrite, token);
            var existing = client.GetFolder(SpecialFolder.Archive)
                ?? await FindFolderAsync(client, "Archive", token);
            if (existing is null)
            {
                // No archive anywhere: create one at the top of the personal
                // namespace rather than failing an operation whose whole point
                // is tidying up.
                var personal = client.GetFolder(client.PersonalNamespaces[0]);
                existing = await personal.CreateAsync("Archive", isMessageFolder: true, token);
            }
            IMailFolder archive = existing;
            var (ids, found) = await ExistingAsync(folder, uids, token);
            if (found == 0) return (0, archive.FullName);
            await folder.MoveToAsync(ids, archive, token);
            return (found, archive.FullName);
        }, ct);

    public Task<(int Affected, string Outcome)> DeleteAsync(
        ChannelEntity channel, string? folderName, IReadOnlyList<uint> uids, bool permanent, CancellationToken ct) =>
        RunAsync(channel, async (client, token) =>
        {
            var folder = await OpenAsync(client, folderName, FolderAccess.ReadWrite, token);
            var (ids, found) = await ExistingAsync(folder, uids, token);
            if (found == 0) return (0, "none");

            var trash = client.GetFolder(SpecialFolder.Trash);
            if (!permanent && trash is not null && trash.FullName != folder.FullName)
            {
                await folder.MoveToAsync(ids, trash, token);
                return (found, $"moved to {trash.FullName}");
            }

            await folder.AddFlagsAsync(ids, MessageFlags.Deleted, silent: true, token);
            await folder.ExpungeAsync(token);
            return (found, "expunged");
        }, ct);

    // ---- plumbing ----------------------------------------------------------

    private async Task<T> RunAsync<T>(
        ChannelEntity channel, Func<ImapClient, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        if (!channel.Enabled)
            throw new ValidationException($"email channel '{channel.Name}' is disabled");
        if (!channel.ImapConfigured())
            throw new ValidationException(
                $"email channel '{channel.Name}' has no IMAP host — configure inbound mail in /admin/email");

        var options = channel.ImapSecurity switch
        {
            ChannelEntity.SecuritySsl => SecureSocketOptions.SslOnConnect,
            // StartTls, not WhenAvailable — same no-silent-downgrade stance as the sender.
            ChannelEntity.SecurityStartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };

        using var client = new ImapClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OperationTimeout);

        try
        {
            // ImapConfigured() guaranteed a host above; the compiler cannot see through it.
            await client.ConnectAsync(channel.ImapHost!, channel.ImapPort, options, cts.Token);

            var username = !string.IsNullOrWhiteSpace(channel.ImapUsername)
                ? channel.ImapUsername
                : channel.Username;
            if (!string.IsNullOrWhiteSpace(username))
            {
                var encrypted = channel.ImapPasswordEncrypted is { Length: > 0 }
                    ? channel.ImapPasswordEncrypted
                    : channel.PasswordEncrypted;
                var password = _protector.Decrypt(encrypted);
                if (password is null && encrypted is { Length: > 0 })
                    throw new ValidationException(
                        $"the IMAP password for channel '{channel.Name}' cannot be decrypted; re-enter it");
                await client.AuthenticateAsync(username, password ?? string.Empty, cts.Token);
            }

            var result = await body(client, cts.Token);
            await client.DisconnectAsync(quit: true, cts.Token);
            return result;
        }
        catch (ValidationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ValidationException(
                $"IMAP on channel '{channel.Name}' timed out after {OperationTimeout.TotalSeconds:0}s");
        }
        catch (SslHandshakeException ex)
        {
            _logger.LogWarning(ex, "email.mailbox.tls_failed channel={Channel}", channel.Name);
            throw new ValidationException(
                $"TLS handshake with '{channel.ImapHost}' failed — check the IMAP security mode and port "
                + $"pairing (ssl/993, starttls/143): {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            throw new ValidationException(
                $"the IMAP server rejected the credentials for '{channel.Name}': {ex.Message}");
        }
        catch (FolderNotFoundException ex)
        {
            throw new ValidationException($"mailbox folder '{ex.FolderName}' does not exist");
        }
        catch (Exception ex) when (ex is ImapCommandException or ImapProtocolException or IOException
                                   or System.Net.Sockets.SocketException)
        {
            _logger.LogWarning(ex, "email.mailbox.failed channel={Channel}", channel.Name);
            throw new ValidationException($"IMAP on channel '{channel.Name}' failed: {ex.Message}");
        }
    }

    private static async Task<IMailFolder> OpenAsync(
        ImapClient client, string? name, FolderAccess access, CancellationToken ct)
    {
        var folder = string.IsNullOrWhiteSpace(name) || name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await FindFolderAsync(client, name!, ct)
              ?? throw new ValidationException($"mailbox folder '{name}' does not exist");
        await folder.OpenAsync(access, ct);
        return folder;
    }

    private static async Task<IMailFolder?> FindFolderAsync(ImapClient client, string name, CancellationToken ct)
    {
        if (name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return client.Inbox;
        try
        {
            return await client.GetFolderAsync(name, ct);
        }
        catch (FolderNotFoundException)
        {
            // Fall through to a case-insensitive scan: "sent" should find "Sent".
        }
        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        var folders = await personal.GetSubfoldersAsync(subscribedOnly: false, ct);
        return Flatten(client.Inbox, folders).FirstOrDefault(f =>
            f.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<IMailFolder> Flatten(IMailFolder inbox, IEnumerable<IMailFolder> folders)
    {
        yield return inbox;
        foreach (var f in folders)
        {
            if (f.FullName != inbox.FullName) yield return f;
            IEnumerable<IMailFolder> children;
            try
            {
                children = f.GetSubfolders(subscribedOnly: false);
            }
            catch (ImapCommandException)
            {
                continue;
            }
            foreach (var child in children) yield return child;
        }
    }

    // Filters uids down to the ones the folder still has, so a bulk operation
    // can report "3 of 4" instead of failing wholesale on one stale uid.
    private static async Task<(IList<UniqueId> Ids, int Found)> ExistingAsync(
        IMailFolder folder, IReadOnlyList<uint> uids, CancellationToken ct)
    {
        if (uids.Count == 0) throw new ValidationException("at least one message uid is required");
        var requested = uids.Distinct().Select(u => new UniqueId(u)).ToList();
        var summaries = await folder.FetchAsync(requested, MessageSummaryItems.UniqueId, ct);
        var ids = summaries.Select(s => s.UniqueId).ToList();
        return (ids, ids.Count);
    }

    private static SearchQuery BuildQuery(EmailMailboxQuery q)
    {
        var query = SearchQuery.All;
        if (q.UnreadOnly) query = query.And(SearchQuery.NotSeen);
        if (q.Since is { } since) query = query.And(SearchQuery.DeliveredAfter(since.UtcDateTime));
        if (!string.IsNullOrWhiteSpace(q.FromContains)) query = query.And(SearchQuery.FromContains(q.FromContains));
        if (!string.IsNullOrWhiteSpace(q.SubjectContains))
            query = query.And(SearchQuery.SubjectContains(q.SubjectContains));
        if (!string.IsNullOrWhiteSpace(q.TextContains)) query = query.And(SearchQuery.BodyContains(q.TextContains));
        return query;
    }

    private static long? AttachmentSize(MimeKit.MimePart part)
    {
        try
        {
            return part.Content?.Stream is { CanSeek: true } s ? s.Length : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? Cap(string? body, out bool truncated)
    {
        truncated = false;
        if (body is null || body.Length <= BodyCharCap) return body;
        truncated = true;
        return body[..BodyCharCap];
    }
}
