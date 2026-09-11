using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;

namespace nashira_backend.Tests;

// The credentials mail on user creation: sent through the configured relay when
// there is one, through the fallback email channel when there is not, and turned
// into an admin-facing warning — never an exception — when neither exists or the
// send fails.
public class UserCredentialsNotifierTests
{
    private sealed class FakeEmail : IEmailService
    {
        public bool IsConfigured { get; set; } = true;
        public Exception? Throws;
        public (IReadOnlyList<string> To, string Subject, string Body)? Sent;

        public Task SendAsync(
            IReadOnlyList<string> to, string subject, string body, bool isHtml,
            IReadOnlyList<string>? cc, EmailAttachment? attachment, CancellationToken ct)
        {
            if (Throws is not null) throw Throws;
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
        public Exception? Throws;
        public (EmailChannel Channel, IReadOnlyList<string> To, string Subject, string Body)? Sent;

        public Task SendAsync(
            EmailChannel channel, IReadOnlyList<string> to, string subject, string body, CancellationToken ct)
        {
            if (Throws is not null) throw Throws;
            Sent = (channel, to, subject, body);
            return Task.CompletedTask;
        }

        public Task SendAsync(EmailChannel channel, EmailChannelMessage message, CancellationToken ct) =>
            SendAsync(channel, message.To, message.Subject, message.TextBody ?? string.Empty, ct);
    }

    private static UserCredentialsNotifier Notifier(
        FakeEmail email, FakeFallback? fallback = null, FakeChannelSender? sender = null,
        string? configuredModules = null) =>
        new(email, fallback ?? new FakeFallback(), sender ?? new FakeChannelSender(),
            ModuleSelection.Parse(configuredModules),
            NullLogger<UserCredentialsNotifier>.Instance);

    [Fact]
    public async Task Sends_username_and_temporary_password_and_says_it_must_change()
    {
        var email = new FakeEmail();

        var warning = await Notifier(email)
            .SendCredentialsAsync("newbie@test.local", "newbie", "S3cret!pass", default);

        Assert.Null(warning);
        Assert.NotNull(email.Sent);
        Assert.Equal(["newbie@test.local"], email.Sent!.Value.To);
        var body = email.Sent.Value.Body;
        Assert.Contains("Username: newbie", body);
        Assert.Contains("Temporary password: S3cret!pass", body);
        Assert.Contains("temporary", body);
        Assert.Contains("change it", body);
    }

    [Fact]
    public async Task Falls_back_to_an_email_channel_when_the_relay_is_not_configured()
    {
        var email = new FakeEmail { IsConfigured = false };
        var fallback = new FakeFallback { Channel = new EmailChannel { Name = "ops-relay" } };
        var sender = new FakeChannelSender();

        var warning = await Notifier(email, fallback, sender)
            .SendCredentialsAsync("newbie@test.local", "newbie", "S3cret!pass", default);

        Assert.Null(warning);
        Assert.Null(email.Sent);
        Assert.NotNull(sender.Sent);
        Assert.Equal("ops-relay", sender.Sent!.Value.Channel.Name);
        Assert.Equal(["newbie@test.local"], sender.Sent.Value.To);
        Assert.Contains("Temporary password: S3cret!pass", sender.Sent.Value.Body);
    }

    [Fact]
    public async Task Warns_instead_of_sending_when_neither_relay_nor_channel_is_configured()
    {
        var email = new FakeEmail { IsConfigured = false };

        var warning = await Notifier(email)
            .SendCredentialsAsync("newbie@test.local", "newbie", "S3cret!pass", default);

        Assert.Null(email.Sent);
        Assert.NotNull(warning);
        Assert.Contains("not configured", warning);
        Assert.Contains("was not sent", warning);
    }

    [Fact]
    public async Task Warns_instead_of_throwing_when_the_relay_fails()
    {
        var email = new FakeEmail { Throws = new ValidationException("email send failed: relay down") };

        var warning = await Notifier(email)
            .SendCredentialsAsync("newbie@test.local", "newbie", "S3cret!pass", default);

        Assert.NotNull(warning);
        Assert.Contains("could not be sent", warning);
        // The failure detail reaches the admin; the password must not.
        Assert.Contains("relay down", warning);
        Assert.DoesNotContain("S3cret!pass", warning);
    }

    [Fact]
    public async Task Warns_instead_of_throwing_when_the_fallback_channel_fails()
    {
        var email = new FakeEmail { IsConfigured = false };
        var fallback = new FakeFallback { Channel = new EmailChannel { Name = "ops-relay" } };
        var sender = new FakeChannelSender { Throws = new ValidationException("channel 'ops-relay' timed out after 30s") };

        var warning = await Notifier(email, fallback, sender)
            .SendCredentialsAsync("newbie@test.local", "newbie", "S3cret!pass", default);

        Assert.NotNull(warning);
        Assert.Contains("could not be sent", warning);
        Assert.Contains("timed out", warning);
        Assert.DoesNotContain("S3cret!pass", warning);
    }

    // Creating the account is core's; delivering the password is not. With
    // communications disabled the user is still created and nothing is composed, sent
    // or attempted — the admin is told plainly that the password needs another route.
    [Fact]
    public async Task Sends_nothing_when_communications_is_disabled()
    {
        var email = new FakeEmail();
        var fallback = new FakeFallback { Channel = new EmailChannel { Name = "ops-relay" } };
        var sender = new FakeChannelSender();

        var warning = await Notifier(email, fallback, sender, configuredModules: "core")
            .SendCredentialsAsync("newbie@test.local", "newbie", "S3cret!pass", default);

        Assert.Null(email.Sent);
        Assert.Null(sender.Sent);
        Assert.NotNull(warning);
        Assert.Contains("Communications is disabled", warning);
        Assert.Contains("another channel", warning);
        Assert.DoesNotContain("S3cret!pass", warning);
    }

    [Fact]
    public async Task Still_sends_when_communications_is_enabled()
    {
        var email = new FakeEmail();

        var warning = await Notifier(
                email,
                configuredModules: "communications,chat,ai-studio,integrations")
            .SendCredentialsAsync("newbie@test.local", "newbie", "S3cret!pass", default);

        Assert.Null(warning);
        Assert.NotNull(email.Sent);
    }
}
