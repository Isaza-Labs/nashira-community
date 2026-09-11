namespace nashira_backend.Services.Security;

// Encrypts/decrypts tenant secret values at rest via ASP.NET Data Protection.
// Null/empty round-trips to null so optional secrets stay NULL in the DB.
public interface ISecretProtector
{
    byte[]? Encrypt(string? plaintext);
    string? Decrypt(byte[]? ciphertext);
}
