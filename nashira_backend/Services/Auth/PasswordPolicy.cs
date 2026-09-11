using Microsoft.Extensions.Options;
using nashira_backend.Configuration;

namespace nashira_backend.Services.Auth;

// Enforces the "Auth:PasswordPolicy" rules on the real login/bootstrap/create
// paths. Keep in sync with the frontend form validator.
public sealed class PasswordPolicy : IPasswordPolicy
{
    private readonly PasswordPolicyOptions _opts;

    public PasswordPolicy(IOptions<AuthOptions> options) => _opts = options.Value.PasswordPolicy;

    public PasswordValidationResult Validate(string password, string? username = null, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < _opts.MinLength)
            return new(false, $"password must be at least {_opts.MinLength} characters");
        if (_opts.RequireUpper && !password.Any(char.IsUpper))
            return new(false, "password must contain an uppercase letter");
        if (_opts.RequireLower && !password.Any(char.IsLower))
            return new(false, "password must contain a lowercase letter");
        if (_opts.RequireDigit && !password.Any(char.IsDigit))
            return new(false, "password must contain a digit");
        if (_opts.RequireSymbol && password.All(char.IsLetterOrDigit))
            return new(false, "password must contain a symbol");
        if (!string.IsNullOrWhiteSpace(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase))
            return new(false, "password must not contain the username");
        return new(true, null);
    }
}
