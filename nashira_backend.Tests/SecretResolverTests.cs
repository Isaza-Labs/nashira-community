using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// SecretResolver turns `${secret:<source>:<id|name>:<field>}` markers into plaintext
// right before a request leaves the process. These tests pin the grammar (what
// resolves, what stays literal) and the per-source field maps, because a silent miss
// here ships a request with a literal marker instead of a credential — and an
// over-eager match puts one into the wrong field.
//
// The session source has its own file: SelfApiCallTests owns the rule that a session
// credential is withheld unless the caller is talking to this backend.
public class SecretResolverTests
{
    private static SecretResolver Build(out AppDbContext db)
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"secret-resolver-{Guid.NewGuid()}")
            .Options);

        return new SecretResolver(
            db, new PassthroughProtector(), new AnonymousUser(),
            new HttpContextAccessor(),
            new JwtTokenService(
                Options.Create(new JwtOptions
                {
                    Issuer = "nashira",
                    Audience = "nashira",
                    Key = "test-signing-key-that-is-long-enough-for-hmac-256",
                    AccessTokenMinutes = 15,
                }),
                NullLogger<JwtTokenService>.Instance),
            new NoopTrace(), NullLogger<SecretResolver>.Instance);
    }

    private static byte[] Cipher(string plaintext) => Encoding.UTF8.GetBytes(plaintext);

    // ─── secret source ──────────────────────────────────────────────────

    [Fact]
    public async Task Resolves_a_secret_by_name()
    {
        var resolver = Build(out var db);
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(),
            Name = "api-token",
            EncryptedValue = Cipher("s3cr3t"),
        });
        await db.SaveChangesAsync();

        Assert.Equal("s3cr3t", await resolver.ResolveAsync("secret", "api-token", "value", default));
    }

    [Fact]
    public async Task Resolves_a_secret_by_id()
    {
        var resolver = Build(out var db);
        var id = Guid.NewGuid();
        db.Secrets.Add(new Secret
        {
            SecretId = id,
            Name = "whatever",
            EncryptedValue = Cipher("by-id"),
        });
        await db.SaveChangesAsync();

        Assert.Equal("by-id", await resolver.ResolveAsync("secret", id.ToString(), "value", default));
    }

    // `value` is the only legal field on a secret; anything else has to miss rather
    // than fall through to some other column.
    [Fact]
    public async Task A_secret_has_no_field_other_than_value()
    {
        var resolver = Build(out var db);
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(),
            Name = "api-token",
            EncryptedValue = Cipher("s3cr3t"),
        });
        await db.SaveChangesAsync();

        Assert.Null(await resolver.ResolveAsync("secret", "api-token", "name", default));
    }

    // A deleted secret keeps its row so the audit trail resolves, but it must stop
    // authenticating anything.
    [Fact]
    public async Task A_soft_deleted_secret_stops_resolving()
    {
        var resolver = Build(out var db);
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(),
            Name = "api-token",
            EncryptedValue = Cipher("s3cr3t"),
            IsActive = false,
        });
        await db.SaveChangesAsync();

        Assert.Null(await resolver.ResolveAsync("secret", "api-token", "value", default));
    }

    // ─── credential source ──────────────────────────────────────────────

    [Theory]
    [InlineData("username", "ada")]
    [InlineData("password", "hunter2")]
    [InlineData("private_key", "-----BEGIN KEY-----")]
    [InlineData("passphrase", "phrase")]
    [InlineData("token", "pat_123")]
    [InlineData("client_secret", "oauth-secret")]
    public async Task Resolves_each_credential_field(string field, string expected)
    {
        var resolver = Build(out var db);
        db.Credentials.Add(new Credential
        {
            CredentialId = Guid.NewGuid(),
            Name = "netbox",
            Username = "ada",
            EncryptedPassword = Cipher("hunter2"),
            EncryptedPrivateKey = Cipher("-----BEGIN KEY-----"),
            EncryptedKeyPassphrase = Cipher("phrase"),
            EncryptedToken = Cipher("pat_123"),
            EncryptedClientSecret = Cipher("oauth-secret"),
        });
        await db.SaveChangesAsync();

        Assert.Equal(expected, await resolver.ResolveAsync("credential", "netbox", field, default));
    }

    [Fact]
    public async Task An_unknown_credential_field_resolves_to_nothing()
    {
        var resolver = Build(out var db);
        db.Credentials.Add(new Credential
        {
            CredentialId = Guid.NewGuid(),
            Name = "netbox",
            EncryptedPassword = Cipher("hunter2"),
        });
        await db.SaveChangesAsync();

        Assert.Null(await resolver.ResolveAsync("credential", "netbox", "scopes", default));
    }

    // ─── ai_provider source ─────────────────────────────────────────────

    [Fact]
    public async Task Resolves_an_ai_provider_api_key()
    {
        var resolver = Build(out var db);
        db.AIProviders.Add(new AIProvider
        {
            AIProviderId = Guid.NewGuid(),
            Name = "openai",
            Type = "openai",
            EncryptedApiKey = Cipher("sk-test"),
        });
        await db.SaveChangesAsync();

        Assert.Equal("sk-test", await resolver.ResolveAsync("ai_provider", "openai", "api_key", default));
        Assert.Null(await resolver.ResolveAsync("ai_provider", "openai", "base_url", default));
    }

    // ─── integration source ─────────────────────────────────────────────

    [Fact]
    public async Task Resolves_a_dotted_path_inside_an_integration_auth_config()
    {
        var resolver = Build(out var db);
        db.Integrations.Add(new Integration
        {
            IntegrationId = Guid.NewGuid(),
            Name = "netbox",
            Slug = "netbox",
            Type = "netbox",
            BaseUrl = "https://netbox.example",
            AuthConfig = """{"method":"token","token":"tok_1","basic":{"password":"pw"}}""",
        });
        await db.SaveChangesAsync();

        Assert.Equal("tok_1", await resolver.ResolveAsync("integration", "netbox", "token", default));
        Assert.Equal("pw", await resolver.ResolveAsync("integration", "netbox", "basic.password", default));
        Assert.Null(await resolver.ResolveAsync("integration", "netbox", "basic.missing", default));
    }

    // An integration usually stores a reference rather than the material, so reading a
    // field out of one has to follow it — otherwise the executor sends the marker.
    [Fact]
    public async Task An_integration_field_holding_a_reference_is_followed_once()
    {
        var resolver = Build(out var db);
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(),
            Name = "netbox-token",
            EncryptedValue = Cipher("tok_real"),
        });
        db.Integrations.Add(new Integration
        {
            IntegrationId = Guid.NewGuid(),
            Name = "netbox",
            Slug = "netbox",
            Type = "netbox",
            BaseUrl = "https://netbox.example",
            AuthConfig = """{"token":"${secret:secret:netbox-token:value}"}""",
        });
        await db.SaveChangesAsync();

        Assert.Equal("tok_real", await resolver.ResolveAsync("integration", "netbox", "token", default));
    }

    [Fact]
    public async Task Malformed_integration_auth_config_resolves_to_nothing()
    {
        var resolver = Build(out var db);
        db.Integrations.Add(new Integration
        {
            IntegrationId = Guid.NewGuid(),
            Name = "netbox",
            Slug = "netbox",
            Type = "netbox",
            BaseUrl = "https://netbox.example",
            AuthConfig = "not json",
        });
        await db.SaveChangesAsync();

        Assert.Null(await resolver.ResolveAsync("integration", "netbox", "token", default));
    }

    // ─── the grammar itself ─────────────────────────────────────────────

    [Fact]
    public async Task Substitutes_several_markers_in_one_template()
    {
        var resolver = Build(out var db);
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(), Name = "a", EncryptedValue = Cipher("A"),
        });
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(), Name = "b", EncryptedValue = Cipher("B"),
        });
        await db.SaveChangesAsync();

        Assert.Equal(
            "x=A y=B",
            await resolver.SubstituteAsync("x=${secret:secret:a:value} y=${secret:secret:b:value}", default));
    }

    // Unresolved markers stay literal. An empty string would be sent as a bearer token
    // and read as "the credential is wrong" instead of "the credential is missing".
    [Theory]
    [InlineData("${secret:secret:nope:value}")]           // no such row
    [InlineData("${secret:nonsense:whatever:value}")]     // no such source
    [InlineData("${secret:secret:only-two-segments}")]    // not the grammar
    public async Task Unresolved_markers_are_left_alone(string template)
    {
        var resolver = Build(out _);

        Assert.Equal(template, await resolver.SubstituteAsync(template, default));
    }

    [Fact]
    public async Task Text_without_markers_is_returned_untouched()
    {
        var resolver = Build(out _);

        Assert.Equal("https://netbox.example/api", await resolver.SubstituteAsync("https://netbox.example/api", default));
        Assert.Equal(string.Empty, await resolver.SubstituteAsync(string.Empty, default));
    }

    // ─── fakes ──────────────────────────────────────────────────────────

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) =>
            plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);

        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null || ciphertext.Length == 0 ? null : Encoding.UTF8.GetString(ciphertext);
    }

    private sealed class AnonymousUser : ICurrentUser
    {
        public Guid UserId => throw new InvalidOperationException("anonymous");
        public string? Username => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => false;
    }

    private sealed class NoopTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null,
            string? error = null, int? durationMs = null) { }

        public ITraceScope Begin(string category, string action, object? metadata = null) => new Scope();

        private sealed class Scope : ITraceScope
        {
            public void Complete(object? metadata = null) { }
            public void Fail(string error, object? metadata = null) { }
            public void Dispose() { }
        }
    }
}
