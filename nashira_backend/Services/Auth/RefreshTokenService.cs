using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Auth;

// Persists only SHA256(raw) so a DB leak doesn't hand out active sessions.
// Rotation revokes the used token and records the replacement's hash; a revoked
// token presented again is replay → the whole chain is revoked.
public sealed class RefreshTokenService : IRefreshTokenService
{
    private readonly AppDbContext _db;
    private readonly JwtOptions _jwt;

    public RefreshTokenService(AppDbContext db, IOptions<JwtOptions> jwt)
    {
        _db = db;
        _jwt = jwt.Value;
    }

    public async Task<(string RawToken, DateTime ExpiresAt)> CreateAsync(
        User user, string ip, CancellationToken ct)
    {
        var raw = GenerateToken();
        var expires = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays);
        _db.RefreshTokens.Add(new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            UserId = user.UserId,
            TokenHash = Hash(raw),
            ExpiresAt = expires,
            CreatedAt = DateTime.UtcNow,
            CreatedFromIp = ip,
        });
        await _db.SaveChangesAsync(ct);
        return (raw, expires);
    }

    public async Task<RotateResult> RotateAsync(string rawToken, string ip, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return new(RotateStatus.Unknown);
        if (token.RevokedAt is not null)
        {
            // Replay of an already-rotated/revoked token: kill the whole chain.
            await RevokeAllForUserAsync(token.UserId, ct);
            return new(RotateStatus.ReplayDetected);
        }
        if (token.ExpiresAt <= DateTime.UtcNow) return new(RotateStatus.Expired);

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.UserId == token.UserId && u.IsActive, ct);
        if (user is null) return new(RotateStatus.Unknown);

        var newRaw = GenerateToken();
        token.RevokedAt = DateTime.UtcNow;
        token.ReplacedByTokenHash = Hash(newRaw);
        _db.RefreshTokens.Add(new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            UserId = user.UserId,
            TokenHash = token.ReplacedByTokenHash,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays),
            CreatedAt = DateTime.UtcNow,
            CreatedFromIp = ip,
        });
        await _db.SaveChangesAsync(ct);
        return new(RotateStatus.Ok, user, newRaw);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash && t.RevokedAt == null, ct);
        if (token is null) return;
        token.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var live = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var t in live) t.RevokedAt = now;
        if (live.Count > 0) await _db.SaveChangesAsync(ct);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Hash(string raw)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
