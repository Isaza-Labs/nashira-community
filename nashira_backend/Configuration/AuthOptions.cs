namespace nashira_backend.Configuration;

// Bound to the "Auth" section. Controls password strength and account lockout.
public class AuthOptions
{
    public const string SectionName = "Auth";

    public PasswordPolicyOptions PasswordPolicy { get; set; } = new();
    public LockoutOptions Lockout { get; set; } = new();

    // How long the authentication trail is kept. Long by default: this is the evidence a
    // security review reaches for, and "when did that account start failing" is asked
    // months after the fact. 0 keeps it forever.
    //
    // Bounded at all because the table is written by anonymous callers — every failed
    // sign-in from anywhere is a row — so an unbounded one grows without limit and holds
    // attempted identifiers longer than anyone needs them.
    public int AuthEventRetentionDays { get; set; } = 180;
}

public class PasswordPolicyOptions
{
    public int MinLength { get; set; } = 12;
    public bool RequireUpper { get; set; } = true;
    public bool RequireLower { get; set; } = true;
    public bool RequireDigit { get; set; } = true;
    public bool RequireSymbol { get; set; } = true;
}

public class LockoutOptions
{
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
