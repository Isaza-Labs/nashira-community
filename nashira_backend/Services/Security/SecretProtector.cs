using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace nashira_backend.Services.Security;

// Data Protection-backed secret encryption. The purpose string isolates these
// from any other protector. Never logs plaintext or ciphertext.
public sealed class SecretProtector : ISecretProtector
{
    private const string Purpose = "nashira.secrets.v1";
    private readonly IDataProtector _protector;
    private readonly ILogger<SecretProtector> _logger;

    public SecretProtector(IDataProtectionProvider provider, ILogger<SecretProtector> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public byte[]? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        try
        {
            return _protector.Protect(Encoding.UTF8.GetBytes(plaintext));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "secret.encrypt.failed");
            throw;
        }
    }

    public string? Decrypt(byte[]? ciphertext)
    {
        if (ciphertext is null || ciphertext.Length == 0) return null;
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "secret.decrypt.failed ciphertext_length={Length}", ciphertext.Length);
            throw;
        }
    }
}
