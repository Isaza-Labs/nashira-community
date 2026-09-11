namespace nashira_backend.Data.Models;

// Append-only log of authentication events: sign-ins, refreshes, logouts, lockouts,
// password changes.
//
// Deliberately separate from AuditEvent, which records domain mutations. The two answer
// different questions and have different shapes — an audit row is about an entity that
// changed and carries before/after; an auth row is about an attempt, and the most
// valuable ones changed nothing at all. A failed sign-in has no entity, no diff, and is
// exactly the row a security review is looking for.
//
// Not hash-chained, unlike AuditEvent. The chain serialises every write behind one
// process-wide gate, which is the wrong trade here: sign-in attempts arrive
// concurrently and a burst of failures — the case that matters most — is precisely
// when a global lock would turn a login page into a queue.
public class AuthEvent
{
    // Wire values, matching what the admin screen filters on. Strings in the column so a
    // new kind does not need a migration.
    public const string LoginSuccess = "login_success";
    public const string LoginFailure = "login_failure";
    public const string Logout = "logout";
    public const string PasswordChange = "password_change";
    public const string Lockout = "lockout";
    public const string Refresh = "refresh";
    public const string TokenRevoked = "token_revoked";

    public Guid AuthEventId { get; set; }

    // Null when the attempt could not be attributed: a sign-in for a username that does
    // not exist, or a refresh token that resolves to nothing. Those are kept precisely
    // because they are unattributable — a burst of them is account enumeration.
    public Guid? UserId { get; set; }

    public string Event { get; set; } = string.Empty;

    public string Ip { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime At { get; set; } = DateTime.UtcNow;

    // Free-form context as JSON: the reason a sign-in failed, the username that was
    // attempted, how long a lockout lasts. Never the password, and never a token.
    public string? MetadataJson { get; set; }
}
