using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using nashira_backend.Exceptions;
using nashira_backend.Services.Security;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Services.Email;

// The full message shape the email_send snippet needs; the simple (to, subject,
// body) overload below builds one of these. Attachments carry raw bytes — the
// caller owns base64 decoding, because "invalid base64" is its input error to
// report, not a transport failure.
public sealed class EmailChannelMessage
{
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
    public required string Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public string? ReplyTo { get; init; }
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Overrides the channel's configured sender for this one message. Null leaves the
    /// channel's own <c>FromAddress</c> / <c>FromName</c> in place, which is what every
    /// existing caller gets.
    /// </summary>
    /// <remarks>
    /// The interchange contract treats the sender as a key that is honoured or refused,
    /// never quietly dropped: a message that went out from the wrong address cannot be
    /// fixed by re-running the step. A named channel can set it; the deployment's default
    /// relay has no such parameter and refuses instead — see EmailSendSnippetHandler.
    /// </remarks>
    public string? FromAddress { get; init; }

    public string? FromName { get; init; }
}

public interface IEmailChannelSender
{
    Task SendAsync(
        ChannelEntity channel, IReadOnlyList<string> to, string subject, string body, CancellationToken ct);

    Task SendAsync(ChannelEntity channel, EmailChannelMessage message, CancellationToken ct);
}

// Sends through a named EmailChannel row instead of the appsettings relay.
//
// MailKit rather than System.Net.Mail, and the difference is not taste: this path
// offers implicit TLS on 465 ("ssl"), which System.Net.Mail cannot express — its
// EnableSsl flag means STARTTLS, so an "ssl" option built on it would be a
// setting that silently does something else. The config-based EmailService keeps
// System.Net.Mail and keeps not offering "ssl"; converging the two is worth doing
// only when that legacy path is next touched.
//
// The SMTP host is deliberately NOT run through IUrlGuard: the guard parses
// http(s) URLs, an SMTP relay is a bare host+port, and the cloud-metadata SSRF
// the guard exists for is an HTTP attack SMTP cannot express. The channel is
// Admin-only configuration, which is the actual control here.
public sealed class EmailChannelSender : IEmailChannelSender
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly ISecretProtector _protector;
    private readonly ILogger<EmailChannelSender> _logger;

    public EmailChannelSender(ISecretProtector protector, ILogger<EmailChannelSender> logger)
    {
        _protector = protector;
        _logger = logger;
    }

    public Task SendAsync(
        ChannelEntity channel, IReadOnlyList<string> to, string subject, string body, CancellationToken ct) =>
        SendAsync(channel, new EmailChannelMessage { To = to, Subject = subject, TextBody = body }, ct);

    public async Task SendAsync(ChannelEntity channel, EmailChannelMessage mail, CancellationToken ct)
    {
        if (!channel.Enabled)
            throw new ValidationException($"email channel '{channel.Name}' is disabled");
        if (string.IsNullOrWhiteSpace(channel.Host))
            throw new ValidationException($"email channel '{channel.Name}' has no host");
        if (mail.To.Count + mail.Cc.Count + mail.Bcc.Count == 0)
            throw new ValidationException("at least one recipient is required");
        if (string.IsNullOrWhiteSpace(mail.TextBody) && string.IsNullOrWhiteSpace(mail.HtmlBody))
            throw new ValidationException("the message needs a text body, an html body, or both");

        var message = new MimeMessage();
        try
        {
            // The message's address, or the channel's. What must NOT happen is the channel's
            // display name over the message's address: that reads as "Network Ops" above a
            // mailbox Network Ops does not use, which is worse than either alternative. So
            // once the message names an address, the display name is the message's too —
            // blank if it gave none.
            //
            // A display name alone is honoured over the channel's own address: it labels the
            // message without misattributing the mailbox, and dropping it would be exactly
            // the silent discard of a declared key this change exists to remove.
            message.From.Add(string.IsNullOrWhiteSpace(mail.FromAddress)
                ? new MailboxAddress(mail.FromName ?? channel.FromName ?? string.Empty, channel.FromAddress)
                : new MailboxAddress(mail.FromName ?? string.Empty, mail.FromAddress));
            foreach (var addr in mail.To) message.To.Add(MailboxAddress.Parse(addr));
            foreach (var addr in mail.Cc) message.Cc.Add(MailboxAddress.Parse(addr));
            foreach (var addr in mail.Bcc) message.Bcc.Add(MailboxAddress.Parse(addr));
            if (!string.IsNullOrWhiteSpace(mail.ReplyTo))
                message.ReplyTo.Add(MailboxAddress.Parse(mail.ReplyTo));
        }
        catch (ParseException)
        {
            throw new ValidationException("an email address is not valid");
        }
        message.Subject = mail.Subject;

        var builder = new BodyBuilder();
        if (!string.IsNullOrWhiteSpace(mail.TextBody)) builder.TextBody = mail.TextBody;
        if (!string.IsNullOrWhiteSpace(mail.HtmlBody)) builder.HtmlBody = mail.HtmlBody;
        foreach (var att in mail.Attachments)
            builder.Attachments.Add(att.FileName, att.Content,
                !string.IsNullOrWhiteSpace(att.ContentType) && ContentType.TryParse(att.ContentType, out var parsed)
                    ? parsed
                    : new ContentType("application", "octet-stream"));
        message.Body = builder.ToMessageBody();

        var options = channel.Security switch
        {
            ChannelEntity.SecuritySsl => SecureSocketOptions.SslOnConnect,
            // StartTls (not WhenAvailable): a channel configured for TLS must fail
            // when the relay cannot offer it, not silently downgrade to plaintext.
            ChannelEntity.SecurityStartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };

        using var client = new SmtpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SendTimeout);

        try
        {
            await client.ConnectAsync(channel.Host, channel.Port, options, cts.Token);

            if (!string.IsNullOrWhiteSpace(channel.Username))
            {
                var password = _protector.Decrypt(channel.PasswordEncrypted);
                if (password is null && channel.PasswordEncrypted is { Length: > 0 })
                    // A rotated keyring, not a missing password. Naming it saves an
                    // operator from chasing an SMTP auth failure that isn't one.
                    throw new ValidationException(
                        $"the password for channel '{channel.Name}' cannot be decrypted; re-enter it");
                await client.AuthenticateAsync(channel.Username, password ?? string.Empty, cts.Token);
            }

            await client.SendAsync(message, cts.Token);
            await client.DisconnectAsync(quit: true, cts.Token);

            _logger.LogInformation("email.channel.sent channel={Channel} to={Count}",
                channel.Name, mail.To.Count + mail.Cc.Count + mail.Bcc.Count);
        }
        catch (ValidationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ValidationException(
                $"channel '{channel.Name}' timed out after {SendTimeout.TotalSeconds:0}s");
        }
        catch (SslHandshakeException ex)
        {
            _logger.LogWarning(ex, "email.channel.tls_failed channel={Channel}", channel.Name);
            throw new ValidationException(
                $"TLS handshake with '{channel.Host}' failed — check the security mode and port pairing " +
                $"(starttls/587, ssl/465): {ex.Message}");
        }
        catch (MailKit.Security.AuthenticationException ex)
        {
            throw new ValidationException($"the relay rejected the credentials for '{channel.Name}': {ex.Message}");
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or IOException
                                   or System.Net.Sockets.SocketException)
        {
            _logger.LogWarning(ex, "email.channel.failed channel={Channel}", channel.Name);
            throw new ValidationException($"SMTP send via '{channel.Name}' failed: {ex.Message}");
        }
    }
}
