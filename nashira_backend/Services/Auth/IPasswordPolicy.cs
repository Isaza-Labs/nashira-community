namespace nashira_backend.Services.Auth;

public record PasswordValidationResult(bool IsValid, string? Error);

public interface IPasswordPolicy
{
    PasswordValidationResult Validate(string password, string? username = null, string? email = null);
}
