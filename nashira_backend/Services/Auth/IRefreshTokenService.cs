using nashira_backend.Data.Models;

namespace nashira_backend.Services.Auth;

public enum RotateStatus { Ok, Unknown, Expired, ReplayDetected }

public sealed record RotateResult(RotateStatus Status, User? User = null, string? NewRawToken = null);

public interface IRefreshTokenService
{
    Task<(string RawToken, DateTime ExpiresAt)> CreateAsync(User user, string ip, CancellationToken ct);
    Task<RotateResult> RotateAsync(string rawToken, string ip, CancellationToken ct);
    Task RevokeAsync(string rawToken, CancellationToken ct);
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct);
}
