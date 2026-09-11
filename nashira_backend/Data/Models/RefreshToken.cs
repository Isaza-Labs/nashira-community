namespace nashira_backend.Data.Models;

// Short-lived rotation token that buys a fresh access token. We persist only
// the SHA256 of the raw token so a DB leak doesn't hand an attacker active
// sessions. On every use the current token is revoked and the replacement's
// hash recorded; presenting a revoked token again is treated as replay and
// invalidates the whole chain for that user.
public class RefreshToken
{
    public Guid RefreshTokenId { get; set; }
    public Guid UserId { get; set; }

    // SHA256(raw_token) as 64-char hex. The raw token is never stored.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    // Null while live. Set on logout, rotation, or revoke-chain.
    public DateTime? RevokedAt { get; set; }

    // When rotated, points to the hash of the replacement. Used by the replay detector.
    public string? ReplacedByTokenHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedFromIp { get; set; } = string.Empty;
}
