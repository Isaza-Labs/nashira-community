using System.Text.Json;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Export;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Email;
using nashira_backend.Services.Export;

namespace nashira_backend.Tests;

// The AI `send_email` tool: relay when configured, fallback channel when not,
// and a tool error — not an exception — when neither exists.
public class SendEmailHandlerTests
{
    private sealed class FakeEmail : IEmailService
    {
        public bool IsConfigured { get; set; } = true;
        public (IReadOnlyList<string> To, string Subject, string Body)? Sent;

        public Task SendAsync(
            IReadOnlyList<string> to, string subject, string body, bool isHtml,
            IReadOnlyList<string>? cc, EmailAttachment? attachment, CancellationToken ct)
        {
            Sent = (to, subject, body);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFallback : IEmailChannelFallback
    {
        public EmailChannel? Channel;
        public Task<EmailChannel?> FindAsync(CancellationToken ct) => Task.FromResult(Channel);
    }

    private sealed class FakeChannelSender : IEmailChannelSender
    {
        public (EmailChannel Channel, EmailChannelMessage Message)? Sent;

        public Task SendAsync(
            EmailChannel channel, IReadOnlyList<string> to, string subject, string body, CancellationToken ct) =>
            SendAsync(channel, new EmailChannelMessage { To = to, Subject = subject, TextBody = body }, ct);

        public Task SendAsync(EmailChannel channel, EmailChannelMessage message, CancellationToken ct)
        {
            Sent = (channel, message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExport : IExportService
    {
        public Task<ExportArtifactResponse> CreateAsync(
            string format, string? fileName, IReadOnlyList<string>? columns,
            IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ExportArtifactResponse> CreateDocumentAsync(
            string format, string? fileName, string? title, string content, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ListResponse<ExportArtifactResponse>> ListAsync(int limit, int offset, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ExportArtifact?> GetForDownloadAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<ExportArtifact?>(null);
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static SendEmailHandler Handler(
        FakeEmail email, FakeFallback? fallback = null, FakeChannelSender? sender = null) =>
        new(email, fallback ?? new FakeFallback(), sender ?? new FakeChannelSender(), new FakeExport());

    [Fact]
    public async Task Uses_the_relay_when_it_is_configured()
    {
        var email = new FakeEmail();
        var sender = new FakeChannelSender();

        var result = await Handler(email, sender: sender)
            .ExecuteAsync(Args(new { to = "ops@test.local", subject = "hi", body = "text" }), default);

        Assert.True(result.GetProperty("sent").GetBoolean());
        Assert.NotNull(email.Sent);
        Assert.Null(sender.Sent);
    }

    [Fact]
    public async Task Falls_back_to_an_email_channel_when_the_relay_is_not_configured()
    {
        var email = new FakeEmail { IsConfigured = false };
        var fallback = new FakeFallback { Channel = new EmailChannel { Name = "ops-relay" } };
        var sender = new FakeChannelSender();

        var result = await Handler(email, fallback, sender)
            .ExecuteAsync(Args(new { to = "ops@test.local", subject = "hi", body = "text" }), default);

        Assert.True(result.GetProperty("sent").GetBoolean());
        Assert.Equal("ops-relay", result.GetProperty("channel").GetString());
        Assert.Null(email.Sent);
        Assert.NotNull(sender.Sent);
        Assert.Equal(["ops@test.local"], sender.Sent!.Value.Message.To);
        Assert.Equal("text", sender.Sent.Value.Message.TextBody);
        Assert.Null(sender.Sent.Value.Message.HtmlBody);
    }

    [Fact]
    public async Task Routes_an_html_body_to_the_channel_html_slot()
    {
        var email = new FakeEmail { IsConfigured = false };
        var fallback = new FakeFallback { Channel = new EmailChannel { Name = "ops-relay" } };
        var sender = new FakeChannelSender();

        await Handler(email, fallback, sender)
            .ExecuteAsync(Args(new { to = "ops@test.local", subject = "hi", body = "<p>x</p>", is_html = true }), default);

        Assert.Equal("<p>x</p>", sender.Sent!.Value.Message.HtmlBody);
        Assert.Null(sender.Sent.Value.Message.TextBody);
    }

    [Fact]
    public async Task Returns_a_tool_error_when_neither_relay_nor_channel_is_configured()
    {
        var email = new FakeEmail { IsConfigured = false };

        var result = await Handler(email)
            .ExecuteAsync(Args(new { to = "ops@test.local", subject = "hi", body = "text" }), default);

        Assert.Contains("not configured", result.GetProperty("error").GetString());
        Assert.Null(email.Sent);
    }
}
