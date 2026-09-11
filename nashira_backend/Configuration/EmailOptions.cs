namespace nashira_backend.Configuration;

// SMTP settings. Disabled by default; set Smtp:Enabled=true + host/from in prod.
// The password is a secret — supply it via environment (Smtp__Password) or a
// mounted secret file, not committed config.
public sealed class EmailOptions
{
    public const string SectionName = "Smtp";

    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "nashira";
}
