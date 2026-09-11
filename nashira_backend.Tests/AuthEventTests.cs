using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Auth;
using nashira_backend.Data.Models;
using nashira_backend.Services.Auth;

namespace nashira_backend.Tests;

// The authentication trail.
//
// The rows that matter most are the ones nothing else records: a sign-in for a username
// that does not exist, the attempt that trips a lockout, a refresh token presented
// twice. None of those change any entity, so the mutation audit never sees them, and
// none survive log rotation. These tests pin that they are written, attributed, and
// carry enough context to act on.
public class AuthEventTests
{
    private const string Password = "Correct-Horse-9!";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"auth-{Guid.NewGuid()}").Options);

    private static AuthService NewService(AppDbContext db, int maxFailed = 3)
    {
        var hasher = new PasswordHasher<User>();
        var options = Options.Create(new AuthOptions
        {
            Lockout = new nashira_backend.Configuration.LockoutOptions { MaxFailedAttempts = maxFailed, LockoutMinutes = 15 },
        });
        return new AuthService(
            db,
            new FakeJwt(),
            new FakeRefreshTokens(),
            new PasswordPolicy(options),
            hasher,
            options,
            Options.Create(new JwtOptions { Key = "test-key-for-attempt-redaction-0123456789" }),
            new AuthAuditLogger(db, NullLogger<AuthAuditLogger>.Instance),
            NullLogger<AuthService>.Instance);
    }

    private static User AddUser(AppDbContext db, string username = "admin")
    {
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@localhost",
            Role = "admin",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, Password);
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static Task<List<AuthEvent>> EventsAsync(AppDbContext db) =>
        db.AuthEvents.AsNoTracking().OrderBy(e => e.At).ToListAsync();

    [Fact]
    public async Task A_successful_sign_in_is_recorded()
    {
        using var db = NewDb();
        var user = AddUser(db);

        await NewService(db).LoginAsync(
            new LoginRequest { Username = "admin", Password = Password }, "10.0.0.1", "curl", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.Equal(AuthEvent.LoginSuccess, e.Event);
        Assert.Equal(user.UserId, e.UserId);
        Assert.Equal("10.0.0.1", e.Ip);
    }

    // The row no other trail contains: there is no user to attribute it to, and it is
    // the shape account enumeration takes.
    [Fact]
    public async Task A_sign_in_for_an_unknown_username_is_recorded_unattributed()
    {
        using var db = NewDb();

        await NewService(db).LoginAsync(
            new LoginRequest { Username = "ghost", Password = Password }, "10.0.0.1", "curl", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.Equal(AuthEvent.LoginFailure, e.Event);
        Assert.Null(e.UserId);
        // Redacted, not verbatim: a prefix an operator can recognise, the length, and a
        // keyed hash that makes repeats correlate.
        Assert.Contains("\"attempted_prefix\":\"gho\"", e.MetadataJson);
        Assert.Contains("\"attempted_length\":5", e.MetadataJson);
        Assert.DoesNotContain("ghost", e.MetadataJson);
    }

    // The reason the value is redacted rather than dropped: the same attempt has to
    // produce the same hash, or a burst of tries against one account is unrecognisable.
    [Fact]
    public async Task The_same_attempted_name_always_hashes_the_same()
    {
        using var db = NewDb();
        var auth = NewService(db);

        await auth.LoginAsync(new LoginRequest { Username = "root", Password = Password }, "ip", "ua", default);
        await auth.LoginAsync(new LoginRequest { Username = "root", Password = Password }, "ip", "ua", default);
        await auth.LoginAsync(new LoginRequest { Username = "oracle", Password = Password }, "ip", "ua", default);

        var hashes = (await EventsAsync(db))
            .Select(e => System.Text.Json.JsonDocument.Parse(e.MetadataJson!).RootElement
                .GetProperty("attempted_hash").GetString())
            .ToList();

        Assert.Equal(hashes[0], hashes[1]);
        Assert.NotEqual(hashes[0], hashes[2]);
    }

    // The whole point: a password typed into the username box must not be readable
    // afterwards. Only its first three characters and its length survive.
    [Fact]
    public async Task A_password_typed_into_the_username_box_is_not_stored()
    {
        using var db = NewDb();

        await NewService(db).LoginAsync(
            new LoginRequest { Username = Password, Password = Password }, "ip", "ua", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.DoesNotContain(Password, e.MetadataJson);
    }

    [Fact]
    public async Task A_wrong_password_is_recorded_against_the_user()
    {
        using var db = NewDb();
        var user = AddUser(db);

        await NewService(db).LoginAsync(
            new LoginRequest { Username = "admin", Password = "wrong" }, "10.0.0.1", "curl", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.Equal(AuthEvent.LoginFailure, e.Event);
        Assert.Equal(user.UserId, e.UserId);
        Assert.Contains("wrong_password", e.MetadataJson);
    }

    // The attempt that locks the account gets its own row. The failure counter is reset
    // in the same breath, so "when did this lock" is otherwise unanswerable from the
    // user row alone.
    [Fact]
    public async Task The_attempt_that_locks_the_account_emits_a_lockout_row()
    {
        using var db = NewDb();
        AddUser(db);
        var auth = NewService(db, maxFailed: 3);

        for (var i = 0; i < 3; i++)
            await auth.LoginAsync(
                new LoginRequest { Username = "admin", Password = "wrong" }, "10.0.0.1", "curl", default);

        var events = await EventsAsync(db);
        Assert.Equal(3, events.Count(e => e.Event == AuthEvent.LoginFailure));
        var lockout = Assert.Single(events, e => e.Event == AuthEvent.Lockout);
        Assert.Contains("locked_until", lockout.MetadataJson);
    }

    // Once locked, further attempts are refused before the password is even checked —
    // and that refusal is itself worth recording, with the reason.
    [Fact]
    public async Task An_attempt_while_locked_says_so()
    {
        using var db = NewDb();
        var user = AddUser(db);
        user.LockedUntil = DateTime.UtcNow.AddMinutes(10);
        await db.SaveChangesAsync();

        await NewService(db).LoginAsync(
            new LoginRequest { Username = "admin", Password = Password }, "10.0.0.1", "curl", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.Equal(AuthEvent.LoginFailure, e.Event);
        Assert.Contains("locked", e.MetadataJson);
    }

    [Fact]
    public async Task A_password_change_is_recorded()
    {
        using var db = NewDb();
        var user = AddUser(db);

        await NewService(db).ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "An0ther-Long-Pass!" },
            user.UserId, "10.0.0.1", "curl", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.Equal(AuthEvent.PasswordChange, e.Event);
        Assert.Equal(user.UserId, e.UserId);
    }

    // A live session that cannot produce the current password is either a mistyping
    // owner or a hijacked session, and only the trail tells them apart afterwards.
    [Fact]
    public async Task A_failed_password_change_is_recorded()
    {
        using var db = NewDb();
        var user = AddUser(db);

        await NewService(db).ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = "wrong", NewPassword = "An0ther-Long-Pass!" },
            user.UserId, "10.0.0.1", "curl", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.Equal(AuthEvent.LoginFailure, e.Event);
        Assert.Contains("wrong_current_password", e.MetadataJson);
    }

    [Fact]
    public async Task A_logout_is_recorded()
    {
        using var db = NewDb();
        var user = AddUser(db);

        await NewService(db).LogoutAsync(
            new RefreshRequest { RefreshToken = "" }, user.UserId, "10.0.0.1", "curl", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.Equal(AuthEvent.Logout, e.Event);
    }

    // Nothing here may carry a credential: this table is append-only and read by
    // admins, which is the worst possible place for one to surface.
    [Fact]
    public async Task No_event_carries_the_password()
    {
        using var db = NewDb();
        AddUser(db);
        var auth = NewService(db);

        await auth.LoginAsync(new LoginRequest { Username = "admin", Password = Password }, "ip", "ua", default);
        await auth.LoginAsync(new LoginRequest { Username = "admin", Password = "wrong" }, "ip", "ua", default);
        await auth.LoginAsync(new LoginRequest { Username = "ghost", Password = Password }, "ip", "ua", default);

        foreach (var e in await EventsAsync(db))
        {
            Assert.DoesNotContain(Password, e.MetadataJson ?? string.Empty);
            Assert.DoesNotContain("wrong", (e.MetadataJson ?? string.Empty).Replace("wrong_password", ""));
        }
    }

    // The username is a request-body field on an anonymous endpoint, so it is the one
    // value a stranger writes into an append-only table nothing prunes. Uncapped, one
    // POST with a multi-megabyte username is a multi-megabyte row — and people type
    // their password into the username box, which would land here in cleartext.
    [Fact]
    public async Task An_oversized_attempted_username_is_not_stored_whole()
    {
        using var db = NewDb();

        await NewService(db).LoginAsync(
            new LoginRequest { Username = new string('x', 100_000), Password = Password },
            "10.0.0.1", "curl", default);

        var e = Assert.Single(await EventsAsync(db));
        Assert.NotNull(e.MetadataJson);
        // A prefix, a length and a digest are a fixed cost whatever arrives — which is
        // what stops an anonymous caller sizing the row.
        Assert.True(e.MetadataJson!.Length < 256, $"metadata was {e.MetadataJson.Length} chars");
        Assert.Contains("\"attempted_length\":100000", e.MetadataJson);
    }

    // The Nth failure is the one anybody looks up, and the counter is reset in the same
    // call that locks the account.
    [Fact]
    public async Task The_locking_failure_reports_its_real_attempt_number()
    {
        using var db = NewDb();
        AddUser(db);
        var auth = NewService(db, maxFailed: 3);

        for (var i = 0; i < 3; i++)
            await auth.LoginAsync(
                new LoginRequest { Username = "admin", Password = "wrong" }, "10.0.0.1", "curl", default);

        var failures = (await EventsAsync(db)).Where(e => e.Event == AuthEvent.LoginFailure).ToList();
        Assert.Equal(3, failures.Count);
        Assert.Contains("\"failed_count\":3", failures[2].MetadataJson);
    }

    private sealed class FakeJwt : IJwtTokenService
    {
        public string CreateAccessToken(User user, out DateTime expiresAt)
        {
            expiresAt = DateTime.UtcNow.AddMinutes(15);
            return "token";
        }

        public string CreateAccessToken(
            Guid userId, string username, IEnumerable<string> roles, out DateTime expiresAt)
        {
            expiresAt = DateTime.UtcNow.AddMinutes(15);
            return "token";
        }
    }

    private sealed class FakeRefreshTokens : IRefreshTokenService
    {
        public Task<(string RawToken, DateTime ExpiresAt)> CreateAsync(User user, string ip, CancellationToken ct) =>
            Task.FromResult(("refresh", DateTime.UtcNow.AddDays(7)));

        public Task<RotateResult> RotateAsync(string rawToken, string ip, CancellationToken ct) =>
            Task.FromResult(new RotateResult(RotateStatus.Unknown));

        public Task RevokeAsync(string rawToken, CancellationToken ct) => Task.CompletedTask;

        public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }
}
