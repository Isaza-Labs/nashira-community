using nashira_backend.Data.Models;

namespace nashira_backend.Services.Auth;

public interface IJwtTokenService
{
    string CreateAccessToken(User user, out DateTime expiresAt);

    string CreateAccessToken(
        Guid userId, string username,
        IEnumerable<string> roles, out DateTime expiresAt);
}
