namespace nashira_backend.Services.Email;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

// Sends transactional email over SMTP. Throws DomainException when email is not
// configured, on invalid addresses, or on send failure.
public interface IEmailService
{
    // True when the deployment relay is usable: enabled with a host and a from
    // address. Lets callers decide up front instead of catching the
    // "email is not configured" rejection.
    bool IsConfigured { get; }

    Task SendAsync(
        IReadOnlyList<string> to,
        string subject,
        string body,
        bool isHtml,
        IReadOnlyList<string>? cc,
        EmailAttachment? attachment,
        CancellationToken ct);
}
