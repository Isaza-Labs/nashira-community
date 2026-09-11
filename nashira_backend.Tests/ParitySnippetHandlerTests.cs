using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Email;
using nashira_backend.Services.Notifications;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Workflow;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Tests;

// The FlowWeaver-parity snippet types: report, email_send, slack_message, and the
// netconf / snmp_v3 stubs. What each must get right is the translation — bad config
// fails as bad_input before any service is reached, the run identity on the request
// lands on the artifact, and a delivery failure fails the step instead of passing a
// false success down the DAG.
public class ParitySnippetHandlerTests
{
    static ParitySnippetHandlerTests()
    {
        // QuestPDF requires a licence; Program.cs sets it at boot, tests don't run
        // Program. The `report` handler renders pdf through the same builder, so a
        // test that asks it for one has to set it too.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"parity-{Guid.NewGuid()}")
        .Options);

    private static SnippetRequest Request(
        string json, Guid? runId = null, Guid? workflowId = null, Guid? user = null, string? code = null) => new()
    {
        NodeId = "n1",
        WorkflowId = workflowId ?? Guid.Empty,
        SnippetId = Guid.NewGuid(),
        SnippetType = "test",
        Input = JsonDocument.Parse(json).RootElement.Clone(),
        WorkflowRunId = runId,
        TriggeredBy = user,
        Code = code,
    };

    // ── report ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_markdown_report_is_persisted_with_the_run_identity_and_returned_as_base64()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);
        var runId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var result = await handler.ExecuteAsync(Request(
            """{"title":"Nightly audit","content":"All 12 devices answered.","retain_days":7}""",
            runId, workflowId, userId), default);

        Assert.True(result.Success);
        var row = Assert.Single(await db.ReportArtifacts.ToListAsync());
        // The attribution is the point of pre-allocating the run id.
        Assert.Equal(runId, row.WorkflowRunId);
        Assert.Equal(workflowId, row.WorkflowId);
        Assert.Equal(userId, row.CreatedBy);
        Assert.NotNull(row.ExpiresAt);
        Assert.Equal("text/markdown", row.ContentType);

        // base64 is the file itself — a downstream email_send attaches it verbatim.
        var body = Encoding.UTF8.GetString(
            Convert.FromBase64String(result.Output.GetProperty("base64").GetString()!));
        Assert.Contains("# Nightly audit", body);
        Assert.Contains("All 12 devices answered.", body);
        Assert.Equal($"/api/reports/{row.ReportArtifactId}/download",
            result.Output.GetProperty("download_url").GetString());
    }

    [Fact]
    public async Task A_report_without_title_or_content_fails_before_touching_the_store()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        var noTitle = await handler.ExecuteAsync(Request("""{"content":"x"}"""), default);
        var noContent = await handler.ExecuteAsync(Request("""{"title":"x"}"""), default);
        var badFormat = await handler.ExecuteAsync(
            Request("""{"title":"x","content":"y","format":"docx"}"""), default);

        Assert.False(noTitle.Success);
        Assert.Equal("bad_input", noTitle.ErrorCode);
        Assert.False(noContent.Success);
        Assert.False(badFormat.Success);
        Assert.Empty(await db.ReportArtifacts.ToListAsync());
    }

    [Fact]
    public async Task An_html_report_renders_the_markdown_rather_than_storing_it_raw()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request(
            """{"title":"Audit","content":"## Section","format":"html"}"""), default);

        Assert.True(result.Success);
        var row = Assert.Single(await db.ReportArtifacts.ToListAsync());
        Assert.StartsWith("text/html", row.ContentType);
        Assert.EndsWith(".html", row.FileName);
        Assert.Contains("Section", Encoding.UTF8.GetString(row.Content));
    }

    // ── report: the tabular formats ──────────────────────────────

    // Prose with one table in it — the shape the csv/xlsx decision turns on.
    private const string TableReport = """
        Two links are down.

        ## Down links

        | device | port |
        | --- | --- |
        | r1 | Gi0/1 |
        | r2 | Gi0/2 |
        """;

    // csv carries the document's table and nothing else. A csv of narrative lines
    // would be a spreadsheet in name only.
    [Fact]
    public async Task A_csv_report_exports_the_documents_table_and_not_its_prose()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request(JsonSerializer.Serialize(
            new { title = "Link audit", content = TableReport, format = "csv" })), default);

        Assert.True(result.Success);
        var row = Assert.Single(await db.ReportArtifacts.ToListAsync());
        Assert.Equal("text/csv", row.ContentType);
        Assert.EndsWith(".csv", row.FileName);
        Assert.Equal("csv", result.Output.GetProperty("format").GetString());

        // A single table is a plain csv: header row, then its rows, nothing else.
        var csv = Encoding.UTF8.GetString(row.Content).TrimStart((char)0xFEFF);
        var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "device,port", "r1,Gi0/1", "r2,Gi0/2" }, lines);
        Assert.DoesNotContain("Two links are down", csv);
    }

    // FW's structured document renders `stats` and each section table as markdown
    // tables, so a document that carries both comes out as two sheets — each named
    // after the heading it sits under, which is how a reader tells them apart.
    [Fact]
    public async Task An_xlsx_report_puts_each_table_on_its_own_named_sheet()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request("""
            {"format":"xlsx","document":{"title":"Link audit",
              "stats":[{"label":"devices","value":"12","hint":"answered"}],
              "sections":[{"title":"Down links","markdown":"Two links are down.",
                "tables":[{"headers":["device","port"],"rows":[["r1","Gi0/1"],["r2","Gi0/2"]]}]}]}}
            """), default);

        Assert.True(result.Success);
        var row = Assert.Single(await db.ReportArtifacts.ToListAsync());
        Assert.EndsWith(".xlsx", row.FileName);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", row.ContentType);

        using var ms = new MemoryStream(row.Content);
        using var wb = new XLWorkbook(ms);
        Assert.Equal(2, wb.Worksheets.Count);

        // The stats block sits under the document title; the section table under its
        // own heading.
        var stats = wb.Worksheet("Link audit");
        Assert.Equal("Metric", stats.Cell(1, 1).GetString());
        Assert.Equal("devices", stats.Cell(2, 1).GetString());
        Assert.Equal("12", stats.Cell(2, 2).GetString());

        var down = wb.Worksheet("Down links");
        Assert.Equal("device", down.Cell(1, 1).GetString());
        Assert.Equal("port", down.Cell(1, 2).GetString());
        Assert.Equal("r2", down.Cell(3, 1).GetString());
        Assert.Equal("Gi0/2", down.Cell(3, 2).GetString());
    }

    // No table, no sheet. The refusal has to name the reason, because the fix is the
    // author's: add a table, or ask for a format that carries prose.
    [Fact]
    public async Task A_prose_only_report_refuses_csv_and_xlsx_and_names_the_missing_table()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        var csv = await handler.ExecuteAsync(Request(
            """{"title":"Nightly audit","content":"All 12 devices answered.","format":"csv"}"""), default);
        var xlsx = await handler.ExecuteAsync(Request(
            """{"title":"Nightly audit","content":"All 12 devices answered.","format":"xlsx"}"""), default);

        Assert.False(csv.Success);
        Assert.Equal("not_supported", csv.ErrorCode);
        Assert.Contains("this report has none", csv.Error);
        Assert.Contains("markdown table", csv.Error);
        Assert.Equal("not_supported", xlsx.ErrorCode);
        Assert.Contains("this report has none", xlsx.Error);
        // Refused before anything was written.
        Assert.Empty(await db.ReportArtifacts.ToListAsync());
    }

    // The prose formats are untouched by the tabular ones: the same body still comes
    // out whole, table and narrative together.
    [Fact]
    public async Task The_prose_formats_still_render_the_whole_document()
    {
        using var db = Db();
        var handler = new ReportSnippetHandler(db, NullLogger<ReportSnippetHandler>.Instance);

        foreach (var format in new[] { "markdown", "html", "pdf" })
        {
            var result = await handler.ExecuteAsync(Request(JsonSerializer.Serialize(
                new { title = "Link audit", content = TableReport, format })), default);

            Assert.True(result.Success, format);
            Assert.Equal(format, result.Output.GetProperty("format").GetString());
            var bytes = Convert.FromBase64String(result.Output.GetProperty("base64").GetString()!);
            if (format == "pdf")
            {
                Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
                continue;
            }
            var body = Encoding.UTF8.GetString(bytes);
            Assert.Contains("Two links are down.", body);
            Assert.Contains("Gi0/2", body);
        }

        Assert.Equal(3, await db.ReportArtifacts.CountAsync());
    }

    // ── email_send ──────────────────────────────────────────────────────

    private sealed class FakeChannelSender : IEmailChannelSender
    {
        public ChannelEntity? Channel;
        public EmailChannelMessage? Message;

        public Task SendAsync(ChannelEntity channel, IReadOnlyList<string> to, string subject, string body,
            CancellationToken ct) =>
            SendAsync(channel, new EmailChannelMessage { To = to, Subject = subject, TextBody = body }, ct);

        public Task SendAsync(ChannelEntity channel, EmailChannelMessage message, CancellationToken ct)
        {
            Channel = channel;
            Message = message;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDefaultRelay : IEmailService
    {
        public IReadOnlyList<string>? To;
        public string? Subject;

        public bool IsConfigured => true;

        public Task SendAsync(IReadOnlyList<string> to, string subject, string body, bool isHtml,
            IReadOnlyList<string>? cc, EmailAttachment? attachment, CancellationToken ct)
        {
            To = to;
            Subject = subject;
            return Task.CompletedTask;
        }
    }

    private static ChannelEntity Channel(string slug) => new()
    {
        EmailChannelId = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        Host = "smtp.example.test",
        FromAddress = "nashira@example.test",
        Enabled = true,
        IsActive = true,
    };

    private static (EmailSendSnippetHandler Handler, FakeChannelSender Sender, FakeDefaultRelay Relay, AppDbContext Db)
        NewEmail()
    {
        var db = Db();
        var sender = new FakeChannelSender();
        var relay = new FakeDefaultRelay();
        return (new EmailSendSnippetHandler(db, sender, relay, NullLogger<EmailSendSnippetHandler>.Instance),
            sender, relay, db);
    }

    [Fact]
    public async Task A_named_channel_gets_the_full_message_including_decoded_attachments()
    {
        var (handler, sender, _, db) = NewEmail();
        db.EmailChannels.Add(Channel("ops-relay"));
        await db.SaveChangesAsync();

        var pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-fake"));
        var result = await handler.ExecuteAsync(Request($$"""
            {"channel":"ops-relay","to":"a@x.test, b@x.test","cc":["c@x.test"],
             "subject":"Nightly","body":"done",
             "attachments":[{"file_name":"r.pdf","content_base64":"{{pdf}}","content_type":"application/pdf"}]}
            """), default);

        Assert.True(result.Success);
        Assert.Equal("ops-relay", sender.Channel!.Slug);
        Assert.Equal(["a@x.test", "b@x.test"], sender.Message!.To);
        Assert.Equal(["c@x.test"], sender.Message!.Cc);
        var att = Assert.Single(sender.Message!.Attachments);
        Assert.Equal("%PDF-fake", Encoding.UTF8.GetString(att.Content));
        Assert.Equal(3, result.Output.GetProperty("recipients").GetInt32());
    }

    [Fact]
    public async Task A_named_channel_honours_a_declared_sender()
    {
        var (handler, sender, _, db) = NewEmail();
        db.EmailChannels.Add(Channel("ops-relay"));
        await db.SaveChangesAsync();

        var result = await handler.ExecuteAsync(Request(
            """
            {"channel":"ops-relay","to":"a@x.test","subject":"s","body":"b",
             "from_address":"noreply@x.test","from_name":"Nightly Audit"}
            """), default);

        Assert.True(result.Success);
        Assert.Equal("noreply@x.test", sender.Message!.FromAddress);
        Assert.Equal("Nightly Audit", sender.Message!.FromName);
    }

    [Fact]
    public async Task A_display_name_alone_is_honoured_over_the_channels_own_address()
    {
        // Labelling a message without changing the mailbox it comes from is legitimate and
        // safe. Dropping it because no address came with it would be the silent discard the
        // contract singles this key out to prevent.
        var (handler, sender, _, db) = NewEmail();
        db.EmailChannels.Add(Channel("ops-relay"));
        await db.SaveChangesAsync();

        var result = await handler.ExecuteAsync(Request(
            """{"channel":"ops-relay","to":"a@x.test","subject":"s","body":"b","from_name":"Nightly Audit"}"""),
            default);

        Assert.True(result.Success);
        Assert.Null(sender.Message!.FromAddress);
        Assert.Equal("Nightly Audit", sender.Message!.FromName);
    }

    [Fact]
    public async Task The_default_relay_refuses_a_declared_sender_rather_than_dropping_it()
    {
        // IEmailService has no sender parameter: the deployment relay sends as its own
        // configured address. A message that went out from the wrong sender cannot be fixed
        // by re-running the step, which is why this refuses instead of proceeding.
        var (handler, _, relay, _) = NewEmail();

        var result = await handler.ExecuteAsync(Request(
            """{"to":"a@x.test","subject":"s","body":"b","from_address":"noreply@x.test"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("not_supported", result.ErrorCode);
        Assert.Contains("named channel", result.Error, StringComparison.Ordinal);
        // Nothing was sent — a refusal that still delivered would be the worst of both.
        Assert.Null(relay.To);
    }

    [Fact]
    public async Task Without_a_channel_the_default_relay_is_used_and_its_limits_are_stated()
    {
        var (handler, _, relay, _) = NewEmail();

        var ok = await handler.ExecuteAsync(Request(
            """{"to":"a@x.test","subject":"s","body":"b"}"""), default);
        var bcc = await handler.ExecuteAsync(Request(
            """{"to":"a@x.test","bcc":"hidden@x.test","subject":"s","body":"b"}"""), default);

        Assert.True(ok.Success);
        Assert.Equal(["a@x.test"], relay.To);
        // The default path cannot do bcc; saying so beats silently dropping it.
        Assert.False(bcc.Success);
        Assert.Equal("bad_input", bcc.ErrorCode);
    }

    [Fact]
    public async Task An_unknown_channel_or_missing_subject_fails_without_sending()
    {
        var (handler, sender, relay, _) = NewEmail();

        var noSubject = await handler.ExecuteAsync(Request("""{"to":"a@x.test","body":"b"}"""), default);
        var unknown = await handler.ExecuteAsync(Request(
            """{"channel":"nope","to":"a@x.test","subject":"s","body":"b"}"""), default);
        var badBase64 = await handler.ExecuteAsync(Request(
            """{"to":"a@x.test","subject":"s","body":"b","attachments":[{"file_name":"f","content_base64":"!!"}]}"""),
            default);

        Assert.False(noSubject.Success);
        Assert.Equal("not_found", unknown.ErrorCode);
        Assert.Equal("bad_input", badBase64.ErrorCode);
        Assert.Null(sender.Message);
        Assert.Null(relay.To);
    }

    [Fact]
    public void Email_send_is_non_reversible_and_cannot_be_declared_away()
    {
        var (handler, _, _, _) = NewEmail();
        Assert.Equal(IdempotencyKind.NonReversible, handler.DefaultIdempotency);
        // The asymmetry Idempotency.Effective documents: NonReversible is absolute.
        Assert.Equal(IdempotencyKind.NonReversible, Idempotency.Effective(
            new Snippet { Idempotency = "idempotent" }, handler.DefaultIdempotency));
    }

    // ── slack_message ───────────────────────────────────────────────────

    private sealed class FakeDispatcher : INotificationDispatcher
    {
        public NotificationResult Next = new(true, 200, null, 1, 12);
        public Guid? ChannelId;
        public Guid? RunId;
        public string? Text;

        public Task<NotificationResult> SendAsync(Guid channelId, string text, Guid? workflowRunId, CancellationToken ct, string? blocksJson = null)
        {
            ChannelId = channelId;
            RunId = workflowRunId;
            Text = text;
            return Task.FromResult(Next);
        }
    }

    private static NotificationChannel Notification(string slug) => new()
    {
        NotificationChannelId = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        Kind = NotificationChannel.KindSlack,
        Enabled = true,
        IsActive = true,
    };

    [Fact]
    public async Task The_message_goes_through_the_dispatcher_stamped_with_the_run()
    {
        using var db = Db();
        var dispatcher = new FakeDispatcher();
        var handler = new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(), new PassthroughSecretProtector(),
            NullLogger<SlackMessageSnippetHandler>.Instance);
        var channel = Notification("net-ops");
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        var runId = Guid.NewGuid();

        var result = await handler.ExecuteAsync(
            Request("""{"channel":"net-ops","text":"maintenance done"}""", runId), default);

        Assert.True(result.Success);
        Assert.Equal(channel.NotificationChannelId, dispatcher.ChannelId);
        // The delivery record must point back at this run — that is the audit trail.
        Assert.Equal(runId, dispatcher.RunId);
        Assert.Equal("maintenance done", dispatcher.Text);
    }

    [Fact]
    public async Task A_failed_delivery_fails_the_step_and_is_not_retried_again()
    {
        using var db = Db();
        var dispatcher = new FakeDispatcher
        {
            Next = new NotificationResult(false, 404, "HTTP 404: no_service", 1, 30),
        };
        var handler = new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(), new PassthroughSecretProtector(),
            NullLogger<SlackMessageSnippetHandler>.Instance);
        db.NotificationChannels.Add(Notification("net-ops"));
        await db.SaveChangesAsync();

        var result = await handler.ExecuteAsync(
            Request("""{"channel":"net-ops","text":"x"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("delivery_failed", result.ErrorCode);
        // The dispatcher already retried what was worth retrying.
        Assert.False(result.Retryable);
        Assert.Contains("404", result.Error);
    }

    [Fact]
    public async Task A_missing_channel_or_text_never_reaches_the_dispatcher()
    {
        using var db = Db();
        var dispatcher = new FakeDispatcher();
        var handler = new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(), new PassthroughSecretProtector(),
            NullLogger<SlackMessageSnippetHandler>.Instance);

        var noChannel = await handler.ExecuteAsync(Request("""{"text":"x"}"""), default);
        var noText = await handler.ExecuteAsync(Request("""{"channel":"c"}"""), default);
        var unknown = await handler.ExecuteAsync(Request("""{"channel":"ghost","text":"x"}"""), default);

        Assert.Equal("bad_input", noChannel.ErrorCode);
        Assert.Equal("bad_input", noText.ErrorCode);
        Assert.Equal("not_found", unknown.ErrorCode);
        Assert.Null(dispatcher.ChannelId);
    }

    // ── ansible_playbook ────────────────────────────────────────────────

    [Fact]
    public async Task On_a_non_linux_worker_ansible_refuses_with_a_clear_platform_error()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        using var db = Db();
        var handler = new AnsiblePlaybookSnippetHandler(db, new FakeProtector(),
            NullLogger<AnsiblePlaybookSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request("""{"host":"10.0.0.1"}""", code: "---"), default);

        Assert.False(result.Success);
        Assert.Equal("unsupported_platform", result.ErrorCode);
    }

    private sealed class FakeProtector : nashira_backend.Services.Security.ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) =>
            plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);
        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null ? null : Encoding.UTF8.GetString(ciphertext);
    }

    // ── slack_message: the bot-token transport ──────────────────────────

    private sealed class RecordingSlackProvider : nashira_backend.Services.Messaging.IMessagingProvider
    {
        public string? ThreadId;
        public string? Text;
        public string? BlocksJson;
        public string? Token;

        public string Provider => MessagingChannel.ProviderSlack;

        public Task<nashira_backend.Services.Messaging.WebhookVerifyResult> VerifyAsync(
            MessagingChannel channel, nashira_backend.Services.Messaging.MessagingHttpRequest request,
            string? decryptedSigningSecret, CancellationToken ct) => throw new NotSupportedException();

        public nashira_backend.Services.Messaging.InboundMessage? ParseInbound(
            MessagingChannel channel, nashira_backend.Services.Messaging.MessagingHttpRequest request) =>
            throw new NotSupportedException();

        public Task SendAsync(
            MessagingChannel channel, string? decryptedBotToken,
            nashira_backend.Services.Messaging.OutboundMessage message, CancellationToken ct)
        {
            Token = decryptedBotToken;
            ThreadId = message.ExternalThreadId;
            Text = message.Text;
            BlocksJson = message.BlocksJson;
            return Task.CompletedTask;
        }
    }

    private sealed class OneProviderResolver(nashira_backend.Services.Messaging.IMessagingProvider p)
        : nashira_backend.Services.Messaging.IMessagingProviderResolver
    {
        public nashira_backend.Services.Messaging.IMessagingProvider? Resolve(string? provider) => p;
    }

    private static (SlackMessageSnippetHandler Handler, RecordingSlackProvider Provider) NewBotSlack()
    {
        var db = Db();
        db.MessagingChannels.Add(new MessagingChannel
        {
            MessagingChannelId = Guid.NewGuid(),
            Provider = MessagingChannel.ProviderSlack,
            Name = "ops-slack",
            Slug = "ops-slack",
            BotTokenEncrypted = Encoding.UTF8.GetBytes("xoxb-test"),
            Enabled = true,
            IsActive = true,
        });
        db.SaveChanges();

        var provider = new RecordingSlackProvider();
        return (new SlackMessageSnippetHandler(db, new FakeDispatcher(), new OneProviderResolver(provider),
            new FakeProtector(), NullLogger<SlackMessageSnippetHandler>.Instance), provider);
    }

    [Fact]
    public async Task A_threaded_reply_goes_through_the_bot_transport_into_its_thread()
    {
        var (handler, provider) = NewBotSlack();

        var result = await handler.ExecuteAsync(Request(
            """{"channel":"#net-ops","text":"audit finished","thread_ts":"1724750400.000100"}"""), default);

        Assert.True(result.Success);
        // The provider addresses a thread as "channel:thread_ts". Posting to "#net-ops"
        // alone would be the root of the channel — a reply nobody sees in its thread.
        Assert.Equal("#net-ops:1724750400.000100", provider.ThreadId);
        Assert.Equal("xoxb-test", provider.Token);
    }

    [Fact]
    public async Task Without_a_thread_the_bot_transport_posts_at_the_channel_root()
    {
        var (handler, provider) = NewBotSlack();

        var result = await handler.ExecuteAsync(Request(
            """{"channel":"#net-ops","text":"audit finished"}"""), default);

        Assert.True(result.Success);
        Assert.Equal("#net-ops", provider.ThreadId);
    }

    [Fact]
    public async Task Rich_content_reaches_the_bot_transport_verbatim()
    {
        var (handler, provider) = NewBotSlack();

        var result = await handler.ExecuteAsync(Request(
            """
            {"channel":"#net-ops","text":"audit finished",
             "blocks":[{"type":"section","text":{"type":"mrkdwn","text":"*done*"}}]}
            """), default);

        Assert.True(result.Success);
        Assert.NotNull(provider.BlocksJson);
        Assert.Contains("mrkdwn", provider.BlocksJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_bot_channel_a_thread_is_refused_rather_than_flattened()
    {
        // The webhook transport has no thread parameter. Posting at the root and reporting
        // success is the silent drop the contract forbids, so the step refuses instead.
        using var db = Db();
        db.NotificationChannels.Add(new NotificationChannel
        {
            NotificationChannelId = Guid.NewGuid(),
            Name = "ops",
            Slug = "ops",
            Kind = NotificationChannel.KindSlack,
        });
        await db.SaveChangesAsync();

        var dispatcher = new FakeDispatcher();
        var handler = new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(),
            new PassthroughSecretProtector(), NullLogger<SlackMessageSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(Request(
            """{"channel":"#net-ops","text":"audit finished","thread_ts":"1724750400.000100"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("not_supported", result.ErrorCode);
        Assert.Contains("thread_ts", result.Error, StringComparison.Ordinal);
    }

    // ── the stubs ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_stub_types_fail_actionably_instead_of_unknown_handler()
    {
        var netconf = new NetconfSnippetHandler(NullLogger<NetconfSnippetHandler>.Instance);
        var snmp = new SnmpV3SnippetHandler(NullLogger<SnmpV3SnippetHandler>.Instance);

        var a = await netconf.ExecuteAsync(Request("{}"), default);
        var b = await snmp.ExecuteAsync(Request("{}"), default);

        Assert.False(a.Success);
        Assert.Equal("not_implemented", a.ErrorCode);
        Assert.False(b.Success);
        Assert.Equal("not_implemented", b.ErrorCode);
    }

    // ── the scope that carries the run identity ─────────────────────────

    [Fact]
    public void The_execution_scope_publishes_the_run_identity_and_restores_it_on_dispose()
    {
        var scope = new WorkflowExecutionScope();
        var runId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var user = Guid.NewGuid();

        using (scope.Enter("production", runId, workflowId, user))
        {
            Assert.Equal("production", scope.Environment);
            Assert.Equal(runId, scope.WorkflowRunId);
            Assert.Equal(workflowId, scope.WorkflowId);
            Assert.Equal(user, scope.TriggeredBy);

            // Nesting must not leak either way.
            using (scope.Enter("qa"))
            {
                Assert.Equal("qa", scope.Environment);
                Assert.Null(scope.WorkflowRunId);
            }
            Assert.Equal(runId, scope.WorkflowRunId);
        }

        Assert.Null(scope.Environment);
        Assert.Null(scope.WorkflowRunId);
        Assert.Null(scope.WorkflowId);
        Assert.Null(scope.TriggeredBy);
    }
}
