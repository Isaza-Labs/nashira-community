using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using nashira_backend.Configuration;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Auth;

public class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _signingCredentials;
    private readonly ILogger<JwtTokenService> _logger;

    public JwtTokenService(IOptions<JwtOptions> options, ILogger<JwtTokenService> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.Key) || _options.Key.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Key is missing or shorter than 32 characters. Configure via env var Jwt__Key.");

        var keyBytes = Encoding.UTF8.GetBytes(_options.Key);
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    }

    public string CreateAccessToken(User user, out DateTime expiresAt)
        => CreateAccessToken(user.UserId, user.Username, [user.Role], out expiresAt);

    public string CreateAccessToken(
        Guid userId, string username,
        IEnumerable<string> roles, out DateTime expiresAt)
    {
        var now = DateTime.UtcNow;
        expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new("preferred_username", username),
        };
        // One Role claim per role so the role-based policies see every role held.
        foreach (var role in roles)
            if (!string.IsNullOrWhiteSpace(role))
                claims.Add(new Claim(ClaimTypes.Role, role));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: _signingCredentials);

        var wire = new JwtSecurityTokenHandler().WriteToken(token);
        _logger.LogDebug(
            "auth.jwt.issued user_id={UserId} jti={Jti} expires_at={ExpiresAt}",
            userId, jti, expiresAt);
        return wire;
    }
}
