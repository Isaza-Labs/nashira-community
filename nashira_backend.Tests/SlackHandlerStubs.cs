namespace nashira_backend.Tests;

// No Slack MessagingChannel is seeded in these tests, so the resolver is never asked;
// it exists because the handler's constructor needs one. Returning null rather than a
// working provider keeps it that way — a test that accidentally reached the bot path
// would fail on a missing provider instead of quietly taking it.
internal sealed class NoBotProviderResolver : nashira_backend.Services.Messaging.IMessagingProviderResolver
{
    public nashira_backend.Services.Messaging.IMessagingProvider? Resolve(string? provider) => null;
}

internal sealed class PassthroughSecretProtector : nashira_backend.Services.Security.ISecretProtector
{
    public byte[]? Encrypt(string? plaintext) =>
        plaintext is null ? null : System.Text.Encoding.UTF8.GetBytes(plaintext);
    public string? Decrypt(byte[]? ciphertext) =>
        ciphertext is null ? null : System.Text.Encoding.UTF8.GetString(ciphertext);
}
