using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Email;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Tests;

// The mailbox tool family (list_emails, read_email, …) against a fake IMAP
// service: what these tests own is channel resolution, argument validation and
// the shape of the tool result — the IMAP wire itself belongs to MailKit.
public class MailboxToolHandlerTests
{
    private sealed class FakeMailbox : IEmailChannelMailbox
    {
        public ChannelEntity? UsedChannel;
        public IReadOnlyList<EmailSummary> Listed = [];
        public EmailFullMessage? Message;
        public (IReadOnlyList<uint> Uids, bool Permanent)? DeleteCall;
        public (IReadOnlyList<uint> Uids, bool Seen)? MarkCall;

        public Task<IReadOnlyList<EmailFolderInfo>> ListFoldersAsync(ChannelEntity channel, CancellationToken ct)
        {
            UsedChannel = channel;
            return Task.FromResult<IReadOnlyList<EmailFolderInfo>>([new("INBOX", 5, 2)]);
        }

        public Task<IReadOnlyList<EmailSummary>> ListAsync(
            ChannelEntity channel, EmailMailboxQuery query, CancellationToken ct)
        {
            UsedChannel = channel;
            return Task.FromResult(Listed);
        }

        public Task<EmailFullMessage?> GetAsync(
            ChannelEntity channel, string? folder, uint uid, CancellationToken ct)
        {
            UsedChannel = channel;
            return Task.FromResult(Message);
        }

        public Task<int> MarkAsync(
            ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, bool seen, CancellationToken ct)
        {
            UsedChannel = channel;
            MarkCall = (uids, seen);
            return Task.FromResult(uids.Count);
        }

        public Task<int> MoveAsync(
            ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, string targetFolder,
            CancellationToken ct)
        {
            UsedChannel = channel;
            return Task.FromResult(uids.Count);
        }

        public Task<(int Moved, string ArchiveFolder)> ArchiveAsync(
            ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, CancellationToken ct)
        {
            UsedChannel = channel;
            return Task.FromResult((uids.Count, "Archive"));
        }

        public Task<(int Affected, string Outcome)> DeleteAsync(
            ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, bool permanent, CancellationToken ct)
        {
            UsedChannel = channel;
            DeleteCall = (uids, permanent);
            return Task.FromResult((uids.Count, permanent ? "expunged" : "moved to Trash"));
        }
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"mailbox-tools-{Guid.NewGuid()}")
        .Options);

    private static ChannelEntity Channel(string slug, string? imapHost, DateTime createdAt) => new()
    {
        EmailChannelId = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        Host = "smtp.example.test",
        FromAddress = "noreply@example.test",
        ImapHost = imapHost,
        Enabled = true,
        IsActive = true,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task An_unnamed_channel_resolves_to_the_oldest_one_with_imap()
    {
        await using var db = Db();
        // Oldest of all, but outbound-only — must be skipped, not chosen.
        db.EmailChannels.Add(Channel("smtp-only", null, new DateTime(2026, 1, 1)));
        db.EmailChannels.Add(Channel("newer-mailbox", "imap.example.test", new DateTime(2026, 3, 1)));
        db.EmailChannels.Add(Channel("older-mailbox", "imap.example.test", new DateTime(2026, 2, 1)));
        await db.SaveChangesAsync();
        var mailbox = new FakeMailbox();

        var result = await new ListEmailsHandler(db, mailbox).ExecuteAsync(Args(new { }), default);

        Assert.Equal("older-mailbox", mailbox.UsedChannel!.Slug);
        Assert.Equal("older-mailbox", result.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task A_missing_channel_is_a_tool_error_not_an_exception()
    {
        await using var db = Db();
        var mailbox = new FakeMailbox();

        var unknown = await new ListEmailsHandler(db, mailbox)
            .ExecuteAsync(Args(new { channel = "no-such" }), default);
        var none = await new ListEmailsHandler(db, mailbox).ExecuteAsync(Args(new { }), default);

        Assert.Contains("no-such", unknown.GetProperty("error").GetString());
        Assert.Contains("IMAP", none.GetProperty("error").GetString());
        Assert.Null(mailbox.UsedChannel);
    }

    [Fact]
    public async Task Listing_projects_the_summary_the_triage_needs()
    {
        await using var db = Db();
        db.EmailChannels.Add(Channel("mb", "imap.example.test", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var mailbox = new FakeMailbox
        {
            Listed = [new(42, "INBOX", "Ana <ana@example.test>", "Hola", DateTimeOffset.UtcNow, false, false, true)],
        };

        var result = await new ListEmailsHandler(db, mailbox).ExecuteAsync(Args(new { unread_only = true }), default);

        var message = result.GetProperty("messages")[0];
        Assert.Equal(42u, message.GetProperty("uid").GetUInt32());
        Assert.True(message.GetProperty("has_attachments").GetBoolean());
        Assert.False(message.GetProperty("seen").GetBoolean());
        Assert.Equal(1, result.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Reading_a_stale_uid_says_to_list_again()
    {
        await using var db = Db();
        db.EmailChannels.Add(Channel("mb", "imap.example.test", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var mailbox = new FakeMailbox { Message = null };

        var result = await new ReadEmailHandler(db, mailbox).ExecuteAsync(Args(new { uid = 7 }), default);

        Assert.Contains("list_emails", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_passes_the_permanent_flag_through_and_reports_the_outcome()
    {
        await using var db = Db();
        db.EmailChannels.Add(Channel("mb", "imap.example.test", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var mailbox = new FakeMailbox();

        var trash = await new DeleteEmailHandler(db, mailbox)
            .ExecuteAsync(Args(new { uids = new[] { 1, 2 } }), default);
        Assert.False(mailbox.DeleteCall!.Value.Permanent);
        Assert.Equal("moved to Trash", trash.GetProperty("outcome").GetString());

        var expunged = await new DeleteEmailHandler(db, mailbox)
            .ExecuteAsync(Args(new { uids = new[] { 3 }, permanent = true }), default);
        Assert.True(mailbox.DeleteCall!.Value.Permanent);
        Assert.Equal("expunged", expunged.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Actions_without_uids_fail_before_touching_the_mailbox()
    {
        await using var db = Db();
        db.EmailChannels.Add(Channel("mb", "imap.example.test", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var mailbox = new FakeMailbox();

        var mark = await new MarkEmailHandler(db, mailbox).ExecuteAsync(Args(new { }), default);
        var move = await new MoveEmailHandler(db, mailbox)
            .ExecuteAsync(Args(new { uids = Array.Empty<int>(), target_folder = "Done" }), default);

        Assert.Contains("uids", mark.GetProperty("error").GetString());
        Assert.Contains("uids", move.GetProperty("error").GetString());
        Assert.Null(mailbox.MarkCall);
        Assert.Null(mailbox.UsedChannel);
    }
}
