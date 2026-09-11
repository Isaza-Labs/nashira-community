using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Email;

public sealed class EmailService : IEmailService
{
    private readonly EmailOptions _opt;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<EmailOptions> opt, ILogger<EmailService> logger)
    {
        _opt = opt.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _opt.Enabled && !string.IsNullOrWhiteSpace(_opt.Host) && !string.IsNullOrWhiteSpace(_opt.FromAddress);

    public async Task SendAsync(
        IReadOnlyList<string> to, string subject, string body, bool isHtml,
        IReadOnlyList<string>? cc, EmailAttachment? attachment, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new ValidationException("email is not configured");
        if (to is null || to.Count == 0)
            throw new ValidationException("at least one recipient is required");

        using var msg = new MailMessage
        {
            Subject = subject ?? string.Empty,
            Body = body ?? string.Empty,
            IsBodyHtml = isHtml,
        };
        MemoryStream? attachmentStream = null;
        try
        {
            msg.From = new MailAddress(_opt.FromAddress, _opt.FromName);
            foreach (var addr in to) msg.To.Add(addr);
            if (cc is not null)
                foreach (var addr in cc) msg.CC.Add(addr);
        }
        catch (FormatException)
        {
            throw new ValidationException("an email address is not valid");
        }

        if (attachment is not null)
        {
            attachmentStream = new MemoryStream(attachment.Content);
            msg.Attachments.Add(new Attachment(attachmentStream, attachment.FileName, attachment.ContentType));
        }

        using var client = new SmtpClient(_opt.Host, _opt.Port)
        {
            EnableSsl = _opt.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(_opt.User))
            client.Credentials = new NetworkCredential(_opt.User, _opt.Password);

        try
        {
            await client.SendMailAsync(msg, ct);
            _logger.LogInformation("email.sent to={To} subject={Subject}", string.Join(",", to), subject);
        }
        catch (SmtpException ex)
        {
            _logger.LogWarning(ex, "email.send.failed host={Host}", _opt.Host);
            throw new ValidationException($"email send failed: {ex.Message}");
        }
        finally
        {
            attachmentStream?.Dispose();
        }
    }
}
