using System.Text.Json.Serialization;

namespace nashira_backend.Data.Models;

// A named SMTP destination, so different workflows can mail through different
// relays without an appsettings change.
//
// Nashira already has `Smtp:*` in configuration, driving EmailService. That path
// stays as the deployment default; this is the per-channel override, and a channel
// falls back to the configured relay for anything it leaves blank.
public class EmailChannel : BaseModel
{
    // The channel path sends through MailKit, which does all three modes
    // honestly — including implicit TLS on 465, which System.Net.Mail cannot
    // express (its EnableSsl means STARTTLS). The config-based default path in
    // EmailService keeps System.Net.Mail and therefore keeps NOT offering "ssl".
    public const string SecurityStartTls = "starttls";
    public const string SecuritySsl = "ssl";       // implicit TLS, port 465
    public const string SecurityNone = "none";

    public static readonly string[] SecurityModes = [SecurityStartTls, SecuritySsl, SecurityNone];

    public Guid EmailChannelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Security { get; set; } = SecurityStartTls;

    public string? Username { get; set; }

    [JsonIgnore]
    public byte[]? PasswordEncrypted { get; set; }

    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }

    // Default recipients when a caller does not name any.
    public string? DefaultRecipients { get; set; }

    // Inbound (IMAP) side of the same account. Optional: a channel with no
    // ImapHost is outbound-only, which every channel created before this field
    // existed is. Credentials default to the SMTP ones — the common case is one
    // account for both directions — and can be overridden for providers that
    // split them (e.g. an SMTP relay plus a real mailbox).
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;
    public string ImapSecurity { get; set; } = SecuritySsl;

    public string? ImapUsername { get; set; }

    [JsonIgnore]
    public byte[]? ImapPasswordEncrypted { get; set; }

    public bool ImapConfigured() => !string.IsNullOrWhiteSpace(ImapHost);

    // An SMTP relay on an RFC-1918 address is the normal case on-prem, so this
    // defaults differently from the HTTP-facing entities.
    public bool AllowPrivateNetwork { get; set; } = true;

    public bool Enabled { get; set; } = true;
    public Guid? CreatedBy { get; set; }
}
