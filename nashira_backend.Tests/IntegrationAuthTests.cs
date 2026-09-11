using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Security;
using CredentialEntity = nashira_backend.Data.Models.Credential;

namespace nashira_backend.Tests;

// Auth-config parsing and method inference. The method decides which header a
// credential lands in, so getting it wrong sends the token somewhere the upstream
// ignores — and the symptom is a 401 that looks like a bad credential.
public class IntegrationAuthTests
{
    [Fact]
    public void An_absent_config_parses_to_null()
    {
        Assert.Null(IntegrationAuthConfig.TryParse(null));
        Assert.Null(IntegrationAuthConfig.TryParse("   "));
    }

    // A malformed config must not read as "no auth": an anonymous request to an
    // authenticated API returns a 401 that names the wrong problem.
    [Fact]
    public void A_malformed_config_is_rejected_rather_than_ignored()
    {
        Assert.Throws<ValidationException>(() => IntegrationAuthConfig.TryParse("{not json"));
    }

    [Fact]
    public void An_explicit_method_wins_and_is_normalized()
    {
        var auth = IntegrationAuthConfig.TryParse("""{"method":"BEARER","token":"t"}""");
        Assert.Equal(IntegrationAuthConfig.MethodBearer, auth!.ResolvedMethod());
    }

    [Fact]
    public void A_bare_token_is_inferred_as_the_token_method()
    {
        var auth = IntegrationAuthConfig.TryParse("""{"token":"abc"}""");
        Assert.Equal(IntegrationAuthConfig.MethodToken, auth!.ResolvedMethod());
    }

    [Fact]
    public void A_username_without_a_method_is_inferred_as_basic()
    {
        var auth = IntegrationAuthConfig.TryParse("""{"username":"u","password":"p"}""");
        Assert.Equal(IntegrationAuthConfig.MethodBasic, auth!.ResolvedMethod());
    }

    [Fact]
    public void An_empty_config_resolves_to_none()
    {
        var auth = IntegrationAuthConfig.TryParse("{}");
        Assert.Equal(IntegrationAuthConfig.MethodNone, auth!.ResolvedMethod());
    }

    [Fact]
    public void Secret_references_survive_parsing_untouched()
    {
        // They are resolved at call time, not at parse time — storing the resolved
        // value would defeat having a secret store at all.
        var auth = IntegrationAuthConfig.TryParse("""{"method":"token","token":"${secret:secret:netbox-api-token:value}"}""");
        Assert.Equal("${secret:secret:netbox-api-token:value}", auth!.Token);
    }
}

// An integration can take its secret material from a stored Credential instead of
// inlining it. The split under test: auth_config holds the SHAPE (method, header,
// scheme prefix), the credential holds the MATERIAL, and the shape always wins.
public class IntegrationCredentialAuthTests
{
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

    private static CredentialEntity Credential(string authMethod, Action<CredentialEntity>? configure = null)
    {
        var c = new CredentialEntity
        {
            CredentialId = Guid.NewGuid(),
            Name = $"e2e {authMethod}",
            AuthMethod = authMethod,
            IsActive = true,
        };
        configure?.Invoke(c);
        return c;
    }

    private static IntegrationAuthConfig Merge(string? shapeJson, CredentialEntity credential) =>
        IntegrationCredentialAuth.Merge(IntegrationAuthConfig.TryParse(shapeJson), credential, Protector);

    [Theory]
    [InlineData(CredentialEntity.AuthMethodPassword, IntegrationAuthConfig.MethodBasic)]
    [InlineData(CredentialEntity.AuthMethodToken, IntegrationAuthConfig.MethodBearer)]
    [InlineData(CredentialEntity.AuthMethodApiKey, IntegrationAuthConfig.MethodApiKey)]
    [InlineData(CredentialEntity.AuthMethodOAuth2, IntegrationAuthConfig.MethodOAuthClientCredentials)]
    public void Each_credential_kind_implies_a_method(string credentialMethod, string expected)
    {
        Assert.Equal(expected, IntegrationCredentialAuth.MethodFor(credentialMethod));
        Assert.True(IntegrationCredentialAuth.IsHttpUsable(credentialMethod));
    }

    // An SSH private key has no HTTP scheme to travel in. Accepting one would produce
    // an integration that silently sends no credentials at all.
    [Fact]
    public void An_ssh_key_credential_cannot_authenticate_an_http_request()
    {
        Assert.False(IntegrationCredentialAuth.IsHttpUsable(CredentialEntity.AuthMethodKey));
        Assert.Equal(IntegrationAuthConfig.MethodNone,
            IntegrationCredentialAuth.MethodFor(CredentialEntity.AuthMethodKey));
    }

    [Fact]
    public void A_password_credential_fills_basic_auth()
    {
        var merged = Merge(null, Credential(CredentialEntity.AuthMethodPassword, c =>
        {
            c.Username = "svc_nashira";
            c.EncryptedPassword = Protector.Encrypt("s3cret");
        }));

        Assert.Equal(IntegrationAuthConfig.MethodBasic, merged.ResolvedMethod());
        Assert.Equal("svc_nashira", merged.Username);
        Assert.Equal("s3cret", merged.Password);
    }

    [Fact]
    public void An_api_key_credential_brings_its_header()
    {
        var merged = Merge(null, Credential(CredentialEntity.AuthMethodApiKey, c =>
        {
            c.EncryptedToken = Protector.Encrypt("abc123");
            c.ApiKeyHeader = "X-Custom-Key";
        }));

        Assert.Equal(IntegrationAuthConfig.MethodApiKey, merged.ResolvedMethod());
        Assert.Equal("abc123", merged.Token);
        Assert.Equal("X-Custom-Key", merged.Header);
    }

    [Fact]
    public void An_oauth2_credential_fills_the_client_credentials_grant()
    {
        var merged = Merge(null, Credential(CredentialEntity.AuthMethodOAuth2, c =>
        {
            c.ClientId = "nashira";
            c.EncryptedClientSecret = Protector.Encrypt("shh");
            c.TokenUrl = "https://idp.example.com/token";
            c.Scopes = "read write";
        }));

        Assert.Equal(IntegrationAuthConfig.MethodOAuthClientCredentials, merged.ResolvedMethod());
        Assert.Equal("nashira", merged.ClientId);
        Assert.Equal("shh", merged.ClientSecret);
        Assert.Equal("https://idp.example.com/token", merged.TokenUrl);
        Assert.Equal("read write", merged.Scope);
    }

    // The point of keeping auth_config alongside the credential: NetBox wants the
    // "Token" scheme, not "Bearer", and that knob belongs to the integration rather
    // than to a credential shared with everything else.
    [Fact]
    public void The_stored_shape_overrides_what_the_credential_implies()
    {
        var merged = Merge("""{"method":"token","prefix":"Token"}""",
            Credential(CredentialEntity.AuthMethodToken, c => c.EncryptedToken = Protector.Encrypt("nb-token")));

        Assert.Equal(IntegrationAuthConfig.MethodToken, merged.ResolvedMethod());
        Assert.Equal("Token", merged.Prefix);
        Assert.Equal("nb-token", merged.Token);
    }

    // A value typed into the integration wins over the credential's, so an override
    // never has to mean detaching from the credential entirely.
    [Fact]
    public void An_inline_value_is_not_overwritten_by_the_credential()
    {
        var merged = Merge("""{"method":"bearer","token":"inline"}""",
            Credential(CredentialEntity.AuthMethodToken, c => c.EncryptedToken = Protector.Encrypt("from-credential")));

        Assert.Equal("inline", merged.Token);
    }

    [Fact]
    public void An_ssh_key_credential_contributes_no_material()
    {
        var merged = Merge(null, Credential(CredentialEntity.AuthMethodKey, c =>
            c.EncryptedPrivateKey = Protector.Encrypt("-----BEGIN OPENSSH PRIVATE KEY-----")));

        Assert.Equal(IntegrationAuthConfig.MethodNone, merged.ResolvedMethod());
        Assert.Null(merged.Token);
        Assert.Null(merged.Password);
    }
}

// Slugs are the cross-instance identity an exported bundle names, so collisions and
// drift both break imports on the far side.
public class SlugTests
{
    [Theory]
    [InlineData("NetBox", "netbox")]
    [InlineData("NetBox (lab)", "netbox-lab")]
    [InlineData("  Service   Now  ", "service-now")]
    [InlineData("Añejo Systems", "anejo-systems")]
    [InlineData("---", "")]
    public void Names_slugify_predictably(string name, string expected)
    {
        Assert.Equal(expected, Slug.From(name));
    }

    [Fact]
    public void Two_names_that_slugify_alike_get_distinct_slugs()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = Slug.Unique("NetBox (lab)", taken.Contains);
        taken.Add(first);
        var second = Slug.Unique("NetBox lab", taken.Contains);

        Assert.Equal("netbox-lab", first);
        Assert.Equal("netbox-lab-2", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_name_with_nothing_sluggable_still_yields_an_identifier()
    {
        var slug = Slug.Unique("!!!", _ => false);
        Assert.False(string.IsNullOrWhiteSpace(slug));
    }

    [Theory]
    [InlineData(41)] // the cut lands exactly on the separator
    [InlineData(45)] // the cut lands inside the second word
    [InlineData(64)]
    public void Long_names_are_truncated_without_a_trailing_separator(int maxLength)
    {
        var slug = Slug.From(new string('a', 40) + " " + new string('b', 40), maxLength);

        Assert.True(slug.Length <= maxLength, $"{slug.Length} > {maxLength}");
        Assert.False(slug.EndsWith('-'), "a truncated slug must not end in the separator");
        Assert.DoesNotContain("--", slug);
    }
}
