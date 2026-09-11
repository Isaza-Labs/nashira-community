using nashira_backend.Services.Mcp;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

// MCP auth material is the one place Nashira stores ciphertext rather than a
// ${secret:...} reference, because OAuth tokens are obtained at runtime. These
// cover the round trip and, more importantly, what happens when it fails.
public class McpAuthConfigTests
{
    // Reversible stand-in for Data Protection: the codec's contract is
    // "whatever the protector returns comes back", not any particular cipher.
    private sealed class FakeProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) =>
            string.IsNullOrEmpty(plaintext) ? null : System.Text.Encoding.UTF8.GetBytes(plaintext);

        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null || ciphertext.Length == 0
                ? null
                : System.Text.Encoding.UTF8.GetString(ciphertext);
    }

    private static readonly FakeProtector Protector = new();

    [Fact]
    public void Round_trip_preserves_every_field()
    {
        var config = new McpAuthConfig
        {
            ApiKey = "k",
            ApiKeyHeader = "X-Custom",
            Token = "t",
            Username = "u",
            Password = "p",
            SecretHeaders = new Dictionary<string, string> { ["X-Extra"] = "v" },
            TokenUrl = "https://idp.example.com/token",
            ClientId = "cid",
            ClientSecret = "csecret",
            Scope = "read",
            AccessToken = "at",
            RefreshToken = "rt",
            ExpiresAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var back = McpAuthConfigCodec.Decrypt(McpAuthConfigCodec.Encrypt(config, Protector), Protector);

        Assert.Equal("k", back.ApiKey);
        Assert.Equal("X-Custom", back.ApiKeyHeader);
        Assert.Equal("t", back.Token);
        Assert.Equal("u", back.Username);
        Assert.Equal("p", back.Password);
        Assert.Equal("v", back.SecretHeaders!["X-Extra"]);
        Assert.Equal("https://idp.example.com/token", back.TokenUrl);
        Assert.Equal("cid", back.ClientId);
        Assert.Equal("csecret", back.ClientSecret);
        Assert.Equal("read", back.Scope);
        Assert.Equal("at", back.AccessToken);
        Assert.Equal("rt", back.RefreshToken);
        Assert.Equal(config.ExpiresAt, back.ExpiresAt);
    }

    [Fact]
    public void Encrypting_nothing_yields_null_rather_than_an_empty_blob()
    {
        Assert.Null(McpAuthConfigCodec.Encrypt(null, Protector));
    }

    [Fact]
    public void Absent_material_decrypts_to_an_empty_config_not_null()
    {
        // Callers apply "no auth" off this; returning null would make every call
        // site a null check, and one missed check is a NullReferenceException on
        // a path that should simply have sent no credentials.
        Assert.False(McpAuthConfigCodec.Decrypt(null, Protector).HasSecret);
        Assert.False(McpAuthConfigCodec.Decrypt([], Protector).HasSecret);
    }

    // A rotated Data Protection keyring makes stored blobs unreadable. That has to
    // degrade to "no credentials" — surfacing as a 401 upstream — rather than
    // throwing out of the connection factory.
    [Fact]
    public void Undecryptable_material_degrades_to_an_empty_config()
    {
        var garbage = new byte[] { 0xFF, 0xFE, 0x00, 0x42 };
        var result = McpAuthConfigCodec.Decrypt(garbage, Protector);
        Assert.False(result.HasSecret);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("token")]
    [InlineData("password")]
    [InlineData("client_secret")]
    public void HasSecret_is_true_for_any_kind_of_credential(string field)
    {
        var config = new McpAuthConfig();
        switch (field)
        {
            case "api_key": config.ApiKey = "x"; break;
            case "token": config.Token = "x"; break;
            case "password": config.Password = "x"; break;
            case "client_secret": config.ClientSecret = "x"; break;
        }
        Assert.True(config.HasSecret);
    }

    [Fact]
    public void HasSecret_is_false_for_non_secret_configuration_alone()
    {
        // A token_url and client_id are not secrets; reporting has_credentials for
        // them would tell an admin the server is configured when it is not.
        var config = new McpAuthConfig { TokenUrl = "https://idp.example.com/token", ClientId = "cid" };
        Assert.False(config.HasSecret);
    }
}
