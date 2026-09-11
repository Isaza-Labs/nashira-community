using nashira_backend.Configuration.Modules;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.RestExecutor;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// The agent's only route to triggers, snippets and the rest of the platform is the
// thirteen shipped na_* specs, which describe Nashira's own API. They were seeded with
// no base URL and no auth, so every execute_operation against them died on "spec has no
// base_url configured" — the agent could read the catalog and could not use it, and the
// observed result was a workflow authored around an invented snippet id.
//
// Two things had to be true for that to work, and both are guarded here: the executor
// has to know where it can reach itself, and the call has to carry the calling user's
// own token so nothing the agent does exceeds what that user may do.
public class SelfApiCallTests
{
    // ─── the session credential ─────────────────────────────────────────────

    private static SecretResolver NewResolver(
        out AppDbContext db, HttpContext? http = null, ICurrentUser? user = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"secrets-{Guid.NewGuid()}")
            .Options;
        db = new AppDbContext(options);

        var accessor = new HttpContextAccessor { HttpContext = http };
        var jwt = new JwtTokenService(
            Options.Create(new JwtOptions
            {
                Issuer = "nashira",
                Audience = "nashira",
                Key = "test-signing-key-that-is-long-enough-for-hmac-256",
                AccessTokenMinutes = 15,
            }),
            NullLogger<JwtTokenService>.Instance);

        return new SecretResolver(
            db, new PassthroughProtector(), user ?? new FakeUser(),
            accessor, jwt, new NoopTrace(), NullLogger<SecretResolver>.Instance);
    }

    private static HttpContext RequestWith(string authorization)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = authorization;
        return ctx;
    }

    // The web-chat path. Reusing the incoming bearer is what makes the self-call
    // indistinguishable from the user having made it themselves.
    [Fact]
    public async Task A_session_reference_borrows_the_incoming_request_bearer()
    {
        var resolver = NewResolver(out _, RequestWith("Bearer abc.def.ghi"));

        var result = await resolver.SubstituteAsync(
            SecretResolver.SessionJwtRef, allowSessionRefs: true, CancellationToken.None);

        Assert.Equal("abc.def.ghi", result);
    }

    // Messaging channels and scheduled agent runs have no request to borrow from. The
    // minted token carries the identity already bound on ICurrentUser and nothing more.
    [Fact]
    public async Task Without_a_request_the_reference_mints_a_token_for_the_bound_identity()
    {
        var userId = Guid.NewGuid();
        var resolver = NewResolver(
            out _, http: null,
            user: new FakeUser { Authenticated = true, Id = userId, Name = "ada", RoleList = ["Operator"] });

        var token = await resolver.SubstituteAsync(
            SecretResolver.SessionJwtRef, allowSessionRefs: true, CancellationToken.None);

        Assert.NotEqual(SecretResolver.SessionJwtRef, token);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(userId.ToString(), jwt.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value);
        // The roles are the caller's own, so the self-call is capped exactly where the
        // caller is. A service token would have handed the agent a wider ceiling.
        Assert.Equal(["Operator"], jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value));
    }

    // Anonymous means anonymous: we do not invent an identity to make the call succeed.
    [Fact]
    public async Task An_anonymous_caller_leaves_the_marker_literal()
    {
        var resolver = NewResolver(out _, http: null, user: new FakeUser { Authenticated = false });

        var result = await resolver.SubstituteAsync(
            SecretResolver.SessionJwtRef, allowSessionRefs: true, CancellationToken.None);

        Assert.Equal(SecretResolver.SessionJwtRef, result);
    }

    [Theory]
    [InlineData("${secret:session:current:refresh}")]
    [InlineData("${secret:session:other:jwt}")]
    [InlineData("${secret:session:current}")]
    public async Task Only_current_jwt_is_a_valid_session_reference(string marker)
    {
        var resolver = NewResolver(out _, RequestWith("Bearer abc.def.ghi"));

        var result = await resolver.SubstituteAsync(marker, allowSessionRefs: true, CancellationToken.None);

        Assert.Equal(marker, result);
    }

    // A non-bearer Authorization header is another scheme entirely, not a JWT to forward.
    [Fact]
    public async Task A_non_bearer_authorization_header_is_not_forwarded()
    {
        var resolver = NewResolver(out _, RequestWith("Basic dXNlcjpwYXNz"));

        var result = await resolver.SubstituteAsync(
            SecretResolver.SessionJwtRef, allowSessionRefs: true, CancellationToken.None);

        Assert.Equal(SecretResolver.SessionJwtRef, result);
    }

    // The security decision, asserted. The session credential belongs to whoever is
    // chatting, not to the admin who wrote the spec, so it is off unless a caller
    // explicitly asks — and the only caller that asks is the executor, only for a
    // request it has already established lands on this backend. Without this an admin
    // could point a spec at a host they control, put the reference in its auth config,
    // and collect every user's token as they used the agent.
    [Fact]
    public async Task The_session_credential_is_withheld_by_default()
    {
        var resolver = NewResolver(out _, RequestWith("Bearer abc.def.ghi"));

        Assert.Equal(
            SecretResolver.SessionJwtRef,
            await resolver.SubstituteAsync(SecretResolver.SessionJwtRef, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("session", "current", "jwt", CancellationToken.None));
    }

    // Stored secrets are unaffected by any of the above.
    [Fact]
    public async Task Stored_secrets_still_resolve_and_ignore_the_session_switch()
    {
        var resolver = NewResolver(out var db, RequestWith("Bearer abc.def.ghi"));
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(),
            Name = "netbox-token",
            EncryptedValue = System.Text.Encoding.UTF8.GetBytes("s3cret"),
        });
        await db.SaveChangesAsync();

        const string marker = "${secret:secret:netbox-token:value}";
        Assert.Equal("s3cret", await resolver.SubstituteAsync(marker, CancellationToken.None));
        Assert.Equal("s3cret", await resolver.SubstituteAsync(marker, true, CancellationToken.None));
    }

    // ─── where the backend reaches itself ───────────────────────────────────

    private static IConfiguration Config(string? selfBaseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RestOperationExecutor.SelfBaseUrlKey] = selfBaseUrl,
            })
            .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_self_base_url_falls_back_to_the_container_kestrel(string? configured)
    {
        // The empty value was the bug: it made the shipped specs catalog-only on every
        // deployment, because docker-compose.yml never sets the key either.
        Assert.Equal(
            RestOperationExecutor.DefaultSelfBaseUrl,
            RestOperationExecutor.ResolveSelfBaseUrl(Config(configured)));
    }

    [Fact]
    public void A_configured_self_base_url_wins()
    {
        Assert.Equal(
            "https://nashira.internal",
            RestOperationExecutor.ResolveSelfBaseUrl(Config("https://nashira.internal/")));
    }

    // "Base URL of the API" invites https://host/api, and the shipped paths already
    // start with /api. Left alone that produces /api/api/workflows and a bare 404.
    [Theory]
    [InlineData("https://nashira.internal/api", "https://nashira.internal")]
    [InlineData("https://nashira.internal/api/", "https://nashira.internal")]
    [InlineData("https://nashira.internal/API", "https://nashira.internal")]
    [InlineData("https://nashira.internal/nashira/api", "https://nashira.internal/nashira")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("https://host/apiary", "https://host/apiary")]
    public void A_trailing_api_segment_is_dropped_so_the_path_is_not_doubled(string input, string expected)
    {
        Assert.Equal(expected, RestOperationExecutor.StripTrailingApiSegment(input));
    }

    [Fact]
    public void The_default_self_base_url_joins_a_shipped_path_without_doubling_api()
    {
        var baseUrl = RestOperationExecutor.ResolveSelfBaseUrl(Config("http://localhost:8080/api"));

        // Exactly what CombineUrl does with a na_triggers path.
        var joined = baseUrl.TrimEnd('/') + "/api/workflows/{workflowId}/triggers";

        Assert.Equal("http://localhost:8080/api/workflows/{workflowId}/triggers", joined);
        Assert.DoesNotContain("/api/api/", joined);
    }

    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080", true)]
    [InlineData("http://localhost:8080/api/x", "http://localhost:8080", true)]
    [InlineData("http://LOCALHOST:8080", "http://localhost:8080", true)]
    [InlineData("http://localhost:9090", "http://localhost:8080", false)]
    [InlineData("https://localhost:8080", "http://localhost:8080", false)]
    [InlineData("https://evil.example.com", "http://localhost:8080", false)]
    [InlineData("not a url", "http://localhost:8080", false)]
    public void Self_calls_are_recognised_by_origin(string candidate, string self, bool expected)
    {
        Assert.Equal(expected, RestOperationExecutor.IsSameOrigin(candidate, self));
    }

    // ─── the seeded rows ────────────────────────────────────────────────────

    // Thirteen rows already exist in every install, and the seeder is keyed on the api
    // name, so the insert path can never reach them. Without this step the fix would
    // only ever apply to a database created after it shipped.
    [Fact]
    public async Task Existing_catalog_only_rows_are_upgraded_to_executable()
    {
        var (services, db) = NewSeederHost();
        db.AiApiSpecs.Add(CatalogOnly("na_triggers"));
        db.AiApiSpecs.Add(CatalogOnly("na_snippets"));
        await db.SaveChangesAsync();

        await RunSeederAsync(services);

        foreach (var api in new[] { "na_triggers", "na_snippets" })
        {
            var row = await db.AiApiSpecs.AsNoTracking().FirstAsync(s => s.Api == api);
            Assert.Equal("bearer", row.AuthType);
            Assert.Contains(SecretResolver.SessionJwtRef, row.AuthConfig);
            // No base URL on purpose: the executor resolves it per call, so moving the
            // deployment does not require re-seeding.
            Assert.Null(row.BaseUrl);
        }
    }

    // The seeder's standing promise is that a redeploy never reverts an admin's edit.
    // The upgrade has to honour it, which is why the predicate covers every field it writes.
    [Fact]
    public async Task A_row_an_admin_has_configured_is_left_alone()
    {
        var (services, db) = NewSeederHost();
        var edited = CatalogOnly("na_triggers");
        edited.BaseUrl = "https://proxy.internal";
        edited.AuthType = "token";
        edited.AuthConfig = """{"value":"${secret:secret:nashira-manual:value}"}""";
        var deleted = CatalogOnly("na_snippets");
        deleted.IsActive = false;
        db.AiApiSpecs.AddRange(edited, deleted);
        await db.SaveChangesAsync();

        await RunSeederAsync(services);

        var row = await db.AiApiSpecs.AsNoTracking().FirstAsync(s => s.Api == "na_triggers");
        Assert.Equal("token", row.AuthType);
        Assert.Equal("https://proxy.internal", row.BaseUrl);

        // A spec deleted on purpose stays deleted and untouched.
        var gone = await db.AiApiSpecs.AsNoTracking().FirstAsync(s => s.Api == "na_snippets");
        Assert.False(gone.IsActive);
        Assert.Equal("none", gone.AuthType);
    }

    // A third-party spec that happens to carry no base URL is not ours to point at
    // ourselves; only names we ship qualify.
    [Fact]
    public async Task A_spec_we_do_not_ship_is_not_pointed_at_ourselves()
    {
        var (services, db) = NewSeederHost();
        db.AiApiSpecs.Add(CatalogOnly("netbox"));
        await db.SaveChangesAsync();

        await RunSeederAsync(services);

        var row = await db.AiApiSpecs.AsNoTracking().FirstAsync(s => s.Api == "netbox");
        Assert.Equal("none", row.AuthType);
        Assert.Null(row.AuthConfig);
    }

    // Every shipped spec is inserted ready to execute, so a fresh install needs no
    // configuration at all for the agent to reach the platform.
    [Fact]
    public async Task A_fresh_install_seeds_every_shipped_spec_ready_to_execute()
    {
        var (services, db) = NewSeederHost();

        await RunSeederAsync(services);

        var rows = await db.AiApiSpecs.AsNoTracking().ToListAsync();
        Assert.Equal(13, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("bearer", r.AuthType);
            Assert.Contains(SecretResolver.SessionJwtRef, r.AuthConfig);
        });
    }

    private static AiApiSpec CatalogOnly(string api) => new()
    {
        AiApiSpecId = Guid.NewGuid(),
        Api = api,
        Content = "openapi: 3.1.0\n",
        OperationCount = 0,
        BaseUrl = null,
        AuthType = "none",
        AuthConfig = null,
        IsActive = true,
    };

    private static (IServiceProvider Services, AppDbContext Db) NewSeederHost()
    {
        var name = $"specs-{Guid.NewGuid()}";
        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name))
            .BuildServiceProvider();
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);
        return (services, db);
    }

    private static Task RunSeederAsync(IServiceProvider services) =>
        BuiltinSpecSeeder.SeedAsync(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FakeEnvironment(BackendRoot()),
            new NoopSpecIndex(),
            ModuleSelection.Parse(null),
            NullLogger<SelfApiCallTests>.Instance);

    private static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nashira.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "nashira_backend");
    }

    // ─── doubles ────────────────────────────────────────────────────────────

    // The resolver records every decryption; the trail is not what these tests are
    // about, and a real logger would need the hosted writer behind it.
    private sealed class NoopTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null,
            string? error = null, int? durationMs = null) { }

        public ITraceScope Begin(string category, string action, object? metadata = null) =>
            new Scope();

        private sealed class Scope : ITraceScope
        {
            public void Complete(object? metadata = null) { }
            public void Fail(string error, object? metadata = null) { }
            public void Dispose() { }
        }
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) =>
            plaintext is null ? null : System.Text.Encoding.UTF8.GetBytes(plaintext);

        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null ? null : System.Text.Encoding.UTF8.GetString(ciphertext);
    }

    private sealed class FakeUser : ICurrentUser
    {
        public bool Authenticated { get; init; }
        public Guid Id { get; init; } = Guid.Empty;
        public string? Name { get; init; }
        public IReadOnlyList<string> RoleList { get; init; } = [];

        public Guid UserId => Authenticated
            ? Id
            : throw new InvalidOperationException("anonymous");
        public string? Username => Name;
        public IReadOnlyList<string> Roles => RoleList;
        public bool IsAuthenticated => Authenticated;
    }

    private sealed class NoopSpecIndex : IApiSpecIndex
    {
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [];
        public ApiOperation? GetByOperationId(string operationId) => null;
    }

    private sealed class FakeEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "nashira_backend";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }
}
