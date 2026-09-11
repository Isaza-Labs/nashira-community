using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

// CredentialRules is the single validation seam shared by the REST controller and
// the create/update agent tools — these tests pin the per-method material matrix.
public class CredentialRulesTests
{
    private static readonly byte[] Bytes = [1, 2, 3];

    [Theory]
    [InlineData(null, "password")]
    [InlineData("", "password")]
    [InlineData("password", "password")]
    [InlineData("KEY", "key")]
    [InlineData(" token ", "token")]
    [InlineData("api_key", "api_key")]
    [InlineData("OAuth2", "oauth2")]
    [InlineData("basic", "password")] // unknown → password (legacy coercion)
    public void Normalize_maps_every_input_to_a_known_method(string? raw, string expected)
    {
        Assert.Equal(expected, CredentialRules.Normalize(raw));
    }

    [Fact]
    public void Password_stays_lenient_for_backward_compatibility()
    {
        Assert.Null(CredentialRules.MissingMaterial(new Credential { AuthMethod = "password" }));
    }

    [Fact]
    public void Key_requires_private_key()
    {
        Assert.NotNull(CredentialRules.MissingMaterial(new Credential { AuthMethod = "key" }));
        Assert.Null(CredentialRules.MissingMaterial(
            new Credential { AuthMethod = "key", EncryptedPrivateKey = Bytes }));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("api_key")]
    public void Token_and_api_key_require_the_token_value(string method)
    {
        Assert.NotNull(CredentialRules.MissingMaterial(new Credential { AuthMethod = method }));
        Assert.Null(CredentialRules.MissingMaterial(
            new Credential { AuthMethod = method, EncryptedToken = Bytes }));
    }

    [Fact]
    public void OAuth2_requires_client_id_secret_and_token_url()
    {
        var c = new Credential { AuthMethod = "oauth2" };
        Assert.Contains("client_id", CredentialRules.MissingMaterial(c));

        c.ClientId = "svc-nashira";
        Assert.Contains("client_secret", CredentialRules.MissingMaterial(c));

        c.EncryptedClientSecret = Bytes;
        Assert.Contains("token_url", CredentialRules.MissingMaterial(c));

        c.TokenUrl = "https://idp.example.com/oauth2/token";
        Assert.Null(CredentialRules.MissingMaterial(c));
    }

    [Fact]
    public void Empty_secret_bytes_count_as_missing()
    {
        Assert.NotNull(CredentialRules.MissingMaterial(
            new Credential { AuthMethod = "token", EncryptedToken = [] }));
    }
}
