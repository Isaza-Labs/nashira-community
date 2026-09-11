using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Services.Auth;

namespace nashira_backend.Tests;

public class AuthTests
{
    [Fact]
    public void PasswordPolicy_rejects_weak_and_accepts_strong()
    {
        var policy = new PasswordPolicy(Options.Create(new AuthOptions()));

        Assert.False(policy.Validate("short").IsValid);              // too short
        Assert.False(policy.Validate("alllowercase123!").IsValid);   // no uppercase
        Assert.True(policy.Validate("Str0ng-P@ssw0rd!").IsValid);    // meets all rules
    }

    [Fact]
    public void JwtTokenService_issues_a_three_part_token()
    {
        var opts = Options.Create(new JwtOptions
        {
            Key = "test-signing-key-that-is-definitely-long-enough-1234567890",
            Issuer = "nashira",
            Audience = "nashira-api",
            AccessTokenMinutes = 15,
        });
        var svc = new JwtTokenService(opts, NullLogger<JwtTokenService>.Instance);

        var token = svc.CreateAccessToken(
            Guid.NewGuid(), "alice", ["admin"], out var expiresAt);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(expiresAt > DateTime.UtcNow);
        Assert.Equal(3, token.Split('.').Length); // header.payload.signature
    }
}
