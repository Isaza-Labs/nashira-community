using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Email;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Workflow;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Tests;

// The email_mailbox snippet: action dispatch, channel resolution and the flat
// `uids` output that lets a list step feed an acting step — over a fake IMAP
// service, same stance as the tool tests.
public class EmailMailboxSnippetHandlerTests
{
    private sealed class FakeMailbox : IEmailChannelMailbox
    {
        public IReadOnlyList<EmailSummary> Listed = [];
        public (IReadOnlyList<uint> Uids, string Target)? MoveCall;
        public int Calls;

        public Task<IReadOnlyList<EmailFolderInfo>> ListFoldersAsync(ChannelEntity channel, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<EmailFolderInfo>>([new("INBOX", 3, 1), new("Archive", 10, 0)]);
        }

        public Task<IReadOnlyList<EmailSummary>> ListAsync(
            ChannelEntity channel, EmailMailboxQuery query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Listed);
        }

        public Task<EmailFullMessage?> GetAsync(
            ChannelEntity channel, string? folder, uint uid, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<EmailFullMessage?>(null);
        }

        public Task<int> MarkAsync(
            ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, bool seen, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(uids.Count);
        }

        public Task<int> MoveAsync(
            ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, string targetFolder,
            CancellationToken ct)
        {
            Calls++;
            MoveCall = (uids, targetFolder);
            return Task.FromResult(uids.Count);
        }

        public Task<(int Moved, string ArchiveFolder)> ArchiveAsync(
            ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult((uids.Count, "Archive"));
        }

        public Task<(int Affected, string Outcome)> DeleteAsync(
            ChannelEntity channel, string? folder, IReadOnlyList<uint> uids, bool permanent, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult((uids.Count, permanent ? "expunged" : "moved to Trash"));
        }
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"email-mailbox-{Guid.NewGuid()}")
        .Options);

    private static ChannelEntity Channel(string slug) => new()
    {
        EmailChannelId = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        Host = "smtp.example.test",
        FromAddress = "noreply@example.test",
        ImapHost = "imap.example.test",
        Enabled = true,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static SnippetRequest Request(string json) => new()
    {
        NodeId = "n1",
        WorkflowId = Guid.NewGuid(),
        SnippetId = Guid.NewGuid(),
        SnippetType = "email_mailbox",
        Input = JsonDocument.Parse(json).RootElement.Clone(),
    };

    private static (EmailMailboxSnippetHandler Handler, FakeMailbox Mailbox, AppDbContext Db) New()
    {
        var db = Db();
        var mailbox = new FakeMailbox();
        return (new EmailMailboxSnippetHandler(
            db, mailbox, NullLogger<EmailMailboxSnippetHandler>.Instance), mailbox, db);
    }

    [Fact]
    public async Task A_missing_or_unknown_action_fails_without_touching_the_mailbox()
    {
        var (handler, mailbox, db) = New();
        db.EmailChannels.Add(Channel("mb"));
        await db.SaveChangesAsync();

        var missing = await handler.ExecuteAsync(Request("""{}"""), default);
        var unknown = await handler.ExecuteAsync(Request("""{"action":"peek"}"""), default);

        Assert.False(missing.Success);
        Assert.Equal("bad_input", missing.ErrorCode);
        Assert.False(unknown.Success);
        Assert.Contains("peek", unknown.Error);
        Assert.Equal(0, mailbox.Calls);
    }

    [Fact]
    public async Task An_unknown_channel_is_not_found_and_a_missing_imap_setup_says_where_to_fix_it()
    {
        var (handler, mailbox, db) = New();
        await using var scope = db;

        var named = await handler.ExecuteAsync(
            Request("""{"action":"list","channel":"nope"}"""), default);
        var unnamed = await handler.ExecuteAsync(Request("""{"action":"list"}"""), default);

        Assert.Equal("not_found", named.ErrorCode);
        Assert.Contains("nope", named.Error);
        Assert.Contains("/admin/email", unnamed.Error);
        Assert.Equal(0, mailbox.Calls);
    }

    [Fact]
    public async Task List_emits_a_flat_uids_array_for_downstream_steps()
    {
        var (handler, mailbox, db) = New();
        db.EmailChannels.Add(Channel("mb"));
        await db.SaveChangesAsync();
        mailbox.Listed =
        [
            new(11, "INBOX", "a@example.test", "one", DateTimeOffset.UtcNow, false, false, false),
            new(12, "INBOX", "b@example.test", "two", DateTimeOffset.UtcNow, true, false, false),
        ];

        var result = await handler.ExecuteAsync(Request("""{"action":"list","unread_only":true}"""), default);

        Assert.True(result.Success);
        Assert.Equal(StepChange.Unchanged, result.Change);
        var uids = result.Output.GetProperty("uids").EnumerateArray().Select(e => e.GetUInt32()).ToList();
        Assert.Equal(new uint[] { 11, 12 }, uids);
    }

    [Fact]
    public async Task Move_accepts_a_single_uid_as_well_as_the_array()
    {
        var (handler, mailbox, db) = New();
        db.EmailChannels.Add(Channel("mb"));
        await db.SaveChangesAsync();

        var result = await handler.ExecuteAsync(
            Request("""{"action":"move","uid":7,"target_folder":"Done"}"""), default);

        Assert.True(result.Success);
        Assert.Equal(StepChange.Changed, result.Change);
        Assert.Equal(new uint[] { 7 }, mailbox.MoveCall!.Value.Uids);
        Assert.Equal("Done", mailbox.MoveCall!.Value.Target);
    }

    [Fact]
    public async Task The_default_idempotency_is_requires_compensation()
    {
        var (handler, _, db) = New();
        await using var scope = db;
        Assert.Equal(IdempotencyKind.RequiresCompensation, handler.DefaultIdempotency);
    }
}
