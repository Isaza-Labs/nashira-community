using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Auth;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Auth;

// Login / refresh / logout / change-password / me / bootstrap. Talks to
// AppDbContext directly (no repository layer).
//
// Every outcome here also writes one AuthEvent. The ILogger lines stay: they are for
// whoever is tailing the process, while the table is what an admin reads weeks later
// and what survives log rotation.
public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly IPasswordHasher<User> _hasher;
    private readonly nashira_backend.Configuration.LockoutOptions _lockout;
    private readonly IAuthAuditLogger _authAudit;
    // Derived once: the redaction key for attempted usernames. See AttemptedIdentifier.
    private readonly byte[] _attemptKey;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        IJwtTokenService jwt,
        IRefreshTokenService refreshTokens,
        IPasswordPolicy passwordPolicy,
        IPasswordHasher<User> hasher,
        IOptions<AuthOptions> authOptions,
        IOptions<JwtOptions> jwtOptions,
        IAuthAuditLogger authAudit,
        ILogger<AuthService> logger)
    {
        _db = db;
        _jwt = jwt;
        _refreshTokens = refreshTokens;
        _passwordPolicy = passwordPolicy;
        _hasher = hasher;
        _lockout = authOptions.Value.Lockout;
        _attemptKey = AttemptedIdentifier.DeriveKey(jwtOptions.Value.Key ?? string.Empty);
        _authAudit = authAudit;
        _logger = logger;
    }

    public async Task<ActionResult<LoginResponse>> LoginAsync(
        LoginRequest request, string ip, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return new BadRequestObjectResult(new { error = "username and password are required" });

        // Usernames are globally unique (single-tenant), so this is a direct lookup.
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == request.Username && u.IsActive, ct);

        if (user is null)
        {
            _hasher.HashPassword(new User(), request.Password); // throwaway work
            // Unattributable by construction — there is no user to point at. Recorded
            // anyway: a run of these against different usernames from one IP is account
            // enumeration, and it is invisible in any per-user view.
            // Redacted, not stored: a prefix, the length and a keyed hash. Enough to
            // recognise "adm…(5)" as admin and to spot the same value repeating, and not
            // enough to read back a password typed into the wrong box. See
            // AttemptedIdentifier for why the hash has to be keyed.
            var attempted = AttemptedIdentifier.Redact(request.Username, _attemptKey);
            await _authAudit.LogAsync(
                AuthEventKind.LoginFailure, null, ip, userAgent,
                new
                {
                    reason = "unknown_user",
                    attempted_prefix = attempted.Prefix,
                    attempted_length = attempted.Length,
                    attempted_hash = attempted.Hash,
                }, ct);
            _logger.LogWarning("auth.login.invalid_credentials reason=unknown_user ip={Ip}", ip);
            return Unauthorized();
        }

        if (user.LockedUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
        {
            await _authAudit.LogAsync(
                AuthEventKind.LoginFailure, user.UserId, ip, userAgent,
                new { reason = "locked", locked_until = lockedUntil }, ct);
            _logger.LogWarning(
                "auth.login.locked user_id={UserId} locked_until={LockedUntil} ip={Ip}",
                user.UserId, lockedUntil, ip);
            return LockoutResponse(lockedUntil);
        }

        var check = _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (check == PasswordVerificationResult.Failed)
        {
            // Read before the call: RegisterFailedLoginAsync zeroes the counter on the
            // attempt that locks, so reading it afterwards reports 0 for the Nth
            // failure — the only one anybody looks up.
            var attempt = user.FailedLoginCount + 1;
            var lockedNow = await RegisterFailedLoginAsync(user, ct);
            await _authAudit.LogAsync(
                AuthEventKind.LoginFailure, user.UserId, ip, userAgent,
                new { reason = "wrong_password", failed_count = attempt }, ct);
            // The attempt that trips the threshold gets its own row. Reconstructing
            // "when did this account lock" by counting failures is exactly the work the
            // trail exists to save.
            if (lockedNow)
                await _authAudit.LogAsync(
                    AuthEventKind.Lockout, user.UserId, ip, userAgent,
                    new { locked_until = user.LockedUntil, after_attempts = _lockout.MaxFailedAttempts }, ct);
            _logger.LogWarning(
                "auth.login.invalid_credentials reason=wrong_password user_id={UserId} ip={Ip}",
                user.UserId, ip);
            return Unauthorized();
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _authAudit.LogAsync(AuthEventKind.LoginSuccess, user.UserId, ip, userAgent, null, ct);
        _logger.LogInformation("auth.login.ok user_id={UserId} ip={Ip}",
            user.UserId, ip);

        return await IssueTokensAsync(user, ip, ct);
    }

    public async Task<ActionResult<LoginResponse>> RefreshAsync(
        RefreshRequest request, string ip, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return new BadRequestObjectResult(new { error = "refresh_token is required" });

        var rotate = await _refreshTokens.RotateAsync(request.RefreshToken, ip, ct);
        switch (rotate.Status)
        {
            case RotateStatus.ReplayDetected:
                // A token presented twice means the first use was not by its owner, so
                // the family is revoked. This is the single highest-signal row in the
                // table and it must not read as an ordinary expiry.
                await _authAudit.LogAsync(
                    AuthEventKind.TokenRevoked, rotate.User?.UserId, ip, userAgent,
                    new { reason = "replay_detected" }, ct);
                _logger.LogWarning("auth.refresh.invalid status={Status} ip={Ip}", rotate.Status, ip);
                return Unauthorized();

            case RotateStatus.Unknown:
            case RotateStatus.Expired:
                await _authAudit.LogAsync(
                    AuthEventKind.LoginFailure, rotate.User?.UserId, ip, userAgent,
                    new { reason = "refresh_invalid", status = rotate.Status.ToString() }, ct);
                _logger.LogWarning("auth.refresh.invalid status={Status} ip={Ip}", rotate.Status, ip);
                return Unauthorized();

            case RotateStatus.Ok:
                var user = rotate.User!;
                var accessToken = _jwt.CreateAccessToken(user, out var expiresAt);
                await _authAudit.LogAsync(AuthEventKind.Refresh, user.UserId, ip, userAgent, null, ct);
                _logger.LogInformation("auth.refresh.ok user_id={UserId} ip={Ip}",
                    user.UserId, ip);
                return new OkObjectResult(new LoginResponse
                {
                    AccessToken = accessToken,
                    RefreshToken = rotate.NewRawToken!,
                    UserId = user.UserId,
                    Username = user.Username,
                    Role = user.Role,
                    ExpiresIn = (int)(expiresAt - DateTime.UtcNow).TotalSeconds,
                });

            default:
                return ServerError();
        }
    }

    public async Task<ActionResult> LogoutAsync(
        RefreshRequest request, Guid userId,
        string ip, string userAgent, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
            await _refreshTokens.RevokeAsync(request.RefreshToken, ct);

        await _authAudit.LogAsync(AuthEventKind.Logout, userId, ip, userAgent, null, ct);
        _logger.LogInformation("auth.logout.ok user_id={UserId} ip={Ip}",
            userId, ip);
        return new NoContentResult();
    }

    public async Task<ActionResult> ChangePasswordAsync(
        ChangePasswordRequest request, Guid userId,
        string ip, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return new BadRequestObjectResult(new { error = "current_password and new_password are required" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct);
        if (user is null) return Unauthorized();

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            // Someone with a live session who cannot produce the current password is
            // either a mistyping owner or a hijacked session, and only the trail can
            // tell those apart later.
            await _authAudit.LogAsync(
                AuthEventKind.LoginFailure, userId, ip, userAgent,
                new { reason = "wrong_current_password", during = "password_change" }, ct);
            _logger.LogWarning("auth.password_change.failed reason=wrong_current_password user_id={UserId} ip={Ip}", userId, ip);
            return Unauthorized();
        }

        var policy = _passwordPolicy.Validate(request.NewPassword, user.Username, user.Email);
        if (!policy.IsValid)
            return new BadRequestObjectResult(new { error = policy.Error });

        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword);
        user.PasswordChangedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Force any active session back through login.
        await _refreshTokens.RevokeAllForUserAsync(user.UserId, ct);

        await _authAudit.LogAsync(
            AuthEventKind.PasswordChange, user.UserId, ip, userAgent,
            new { sessions_revoked = true }, ct);
        _logger.LogInformation("auth.password_change.ok user_id={UserId} ip={Ip}",
            user.UserId, ip);
        return new NoContentResult();
    }

    public async Task<ActionResult<MeResponse>> MeAsync(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct);
        if (user is null) return Unauthorized();

        return new OkObjectResult(new MeResponse
        {
            UserId = user.UserId,
            Username = user.Username,
            Email = user.Email,
            Role = user.Role,
            PasswordChangedAt = user.PasswordChangedAt,
        });
    }

    public async Task<ActionResult<BootstrapResponse>> BootstrapAsync(
        string ip, string userAgent, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var initialPassword = GenerateInitialPassword();
        var admin = new User
        {
            UserId = Guid.NewGuid(),
            Username = "admin",
            Email = "admin@localhost",
            Role = "admin",
            PasswordChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = true,
        };
        admin.PasswordHash = _hasher.HashPassword(admin, initialPassword);

        _db.Users.Add(admin);
        await _db.SaveChangesAsync(ct);

        var accessToken = _jwt.CreateAccessToken(admin, out _);
        var (refreshToken, _) = await _refreshTokens.CreateAsync(admin, ip, ct);

        // This creates an administrator from an anonymous request and hands back its
        // tokens, and it used to write to neither trail — AuthController is [SkipAudit],
        // so the mutation filter never saw it either. Development-only, but "an admin
        // account appeared and nobody knows when" is precisely the row this log is for.
        await _authAudit.LogAsync(
            AuthEventKind.LoginSuccess, admin.UserId, ip, userAgent,
            new { reason = "bootstrap", created_admin = true }, ct);

        _logger.LogInformation("auth.bootstrap.ok user_id={UserId} ip={Ip}", admin.UserId, ip);

        return new OkObjectResult(new BootstrapResponse
        {
            UserId = admin.UserId,
            Username = admin.Username,
            InitialPassword = initialPassword,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
        });
    }

    // ─── helpers ────────────────────────────────────────────────────

    private async Task<ActionResult<LoginResponse>> IssueTokensAsync(User user, string ip, CancellationToken ct)
    {
        var accessToken = _jwt.CreateAccessToken(user, out var expiresAt);
        var (refreshToken, _) = await _refreshTokens.CreateAsync(user, ip, ct);
        return new OkObjectResult(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            UserId = user.UserId,
            Username = user.Username,
            Role = user.Role,
            ExpiresIn = (int)(expiresAt - DateTime.UtcNow).TotalSeconds,
        });
    }

    // Returns whether THIS attempt is the one that locked the account, so the caller
    // can record the transition rather than leaving it to be inferred from a count that
    // is reset in the same breath.
    private async Task<bool> RegisterFailedLoginAsync(User user, CancellationToken ct)
    {
        user.FailedLoginCount++;
        var locked = user.FailedLoginCount >= _lockout.MaxFailedAttempts;
        if (locked)
        {
            user.LockedUntil = DateTime.UtcNow.AddMinutes(_lockout.LockoutMinutes);
            user.FailedLoginCount = 0;
        }
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return locked;
    }

    private static UnauthorizedObjectResult Unauthorized() =>
        new(new { error = "invalid_credentials" });

    private static ObjectResult LockoutResponse(DateTime lockedUntil)
    {
        var retrySeconds = Math.Max(1, (int)(lockedUntil - DateTime.UtcNow).TotalSeconds);
        return new ObjectResult(new { error = "invalid_credentials", retry_after_seconds = retrySeconds })
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
        };
    }

    private static ObjectResult ServerError() =>
        new(new { error = "server_error" }) { StatusCode = StatusCodes.Status500InternalServerError };

    // 16 chars with at least one of each class. Surfaced once via BootstrapResponse;
    // the user is expected to rotate it immediately.
    private static string GenerateInitialPassword()
    {
        const string upper = "ABCDEFGHJKMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digit = "23456789";
        const string symbol = "!@#$%^&*-_=+";
        const string all = upper + lower + digit + symbol;

        var chars = new char[16];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digit[RandomNumberGenerator.GetInt32(digit.Length)];
        chars[3] = symbol[RandomNumberGenerator.GetInt32(symbol.Length)];
        for (var i = 4; i < chars.Length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }
}
