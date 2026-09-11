namespace nashira_backend.Services.Auth;

// Append-only write surface for the authentication trail. Each call persists one
// AuthEvent. Never reads — the admin screen queries the table directly.
public interface IAuthAuditLogger
{
    Task LogAsync(
        AuthEventKind kind,
        Guid? userId,
        string ip,
        string userAgent,
        object? metadata = null,
        CancellationToken ct = default);
}

// An enum rather than raw strings at the call sites: "login_failure" typed by hand in
// nine places is "login_failed" in one of them, and the screen that filters on it
// silently stops showing that case.
public enum AuthEventKind
{
    LoginSuccess,
    LoginFailure,
    Logout,
    PasswordChange,
    Lockout,
    Refresh,
    TokenRevoked,
}

public static class AuthEventKindExtensions
{
    public static string ToWireString(this AuthEventKind kind) => kind switch
    {
        AuthEventKind.LoginSuccess => Data.Models.AuthEvent.LoginSuccess,
        AuthEventKind.LoginFailure => Data.Models.AuthEvent.LoginFailure,
        AuthEventKind.Logout => Data.Models.AuthEvent.Logout,
        AuthEventKind.PasswordChange => Data.Models.AuthEvent.PasswordChange,
        AuthEventKind.Lockout => Data.Models.AuthEvent.Lockout,
        AuthEventKind.Refresh => Data.Models.AuthEvent.Refresh,
        AuthEventKind.TokenRevoked => Data.Models.AuthEvent.TokenRevoked,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
