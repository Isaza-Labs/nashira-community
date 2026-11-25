using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Inventory;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// An Operator may run an inventory sync (InventoryController, the sync_netbox_inventory tool), and
// only an Admin may use stored secrets. The source's token reference comes in three shapes: a full
// ${secret:...} reference, a bare secret name and, on rows older than the write-time check, the
// literal token. The rules these tests pin:
//   - a legacy literal token (and no token at all) syncs for an Operator, as it always did;
//   - a full reference to a stored secret is denied before any lookup, decryption or request;
//   - for a caller who may not use stored secrets a bare value is read as a literal and the secrets
//     table is never consulted, so a bare name is sent as itself, not replaced by the stored value;
//   - for an Admin nothing changes: a stored secret of that name wins, a literal is the fallback.
// Real service and real secret resolver; only the transport is a double.
public class NetBoxOperatorSyncTests
{
    private const string StoredValue = "example-stored-value";
    private const string LegacyLiteral = "0123456789abcdef0123456789abcdef01234567";

    private sealed class Rig : IDisposable
    {
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"netbox-operator-sync-{Guid.NewGuid()}").Options);
        public MutableCurrentUser User { get; } = new();
        public CountingProtector Protector { get; } = new();
        public Transport Http { get; } = new();
        public NetBoxSyncService Service { get; }

        public Rig(string role)
        {
            User.Bind(Guid.NewGuid(), "example-user", [role]);
            Db.Secrets.Add(new Secret
            {
                SecretId = Guid.NewGuid(), Name = "example-token", EncryptedValue = Encoding.UTF8.GetBytes(StoredValue),
            });
            Db.SaveChanges();
            var resolver = new SecretResolver(Db, Protector, User, new HttpContextAccessor { HttpContext = null },
                new JwtTokenService(Options.Create(new JwtOptions
                {
                    Issuer = "nashira", Audience = "nashira", AccessTokenMinutes = 15,
                    Key = "test-signing-key-that-is-long-enough-for-hmac-256",
                }), NullLogger<JwtTokenService>.Instance),
                new QuietTrace(), NullLogger<SecretResolver>.Instance);
            Service = new NetBoxSyncService(Db, User, resolver, new RedirectRefusalMappingTests.AllowAllGuard(), Http,
                NullLogger<NetBoxSyncService>.Instance);
        }

        public InventorySource Source(string? tokenRef) => new()
        {
            InventorySourceId = Guid.NewGuid(), Name = "example-netbox", BaseUrl = "https://203.0.113.30",
            TokenSecretRef = tokenRef, IsActive = true,
        };

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData(LegacyLiteral)]
    [InlineData(null)]
    public async Task An_operator_can_sync_a_source_with_a_legacy_literal_token_or_none(string? token)
    {
        using var rig = new Rig("operator");

        var result = await rig.Service.SyncAsync(rig.Source(token), dryRun: true, default);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, rig.Http.Requests);
        Assert.Equal(token is null ? null : $"Token {token}", rig.Http.Authorization);
        Assert.Equal(0, rig.Protector.Decryptions);
    }

    [Fact]
    public async Task An_operator_running_the_tool_syncs_a_legacy_literal_source()
    {
        using var rig = new Rig("operator");
        var source = rig.Source(LegacyLiteral);
        rig.Db.InventorySources.Add(source);
        await rig.Db.SaveChangesAsync();
        var tool = new SyncNetBoxInventoryHandler(rig.Db, rig.User, rig.Service);

        var output = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { dry_run = true }), default);

        Assert.False(output.TryGetProperty("error", out _), output.GetRawText());
        Assert.Equal(1, output.GetProperty("created").GetInt32());
        Assert.Equal($"Token {LegacyLiteral}", rig.Http.Authorization);
    }

    [Theory]
    [InlineData("${secret:secret:example-token:value}")]
    [InlineData("${secret:credential:example-credential:token}")]
    public async Task An_operator_is_denied_a_reference_to_stored_material_before_any_lookup(string reference)
    {
        using var rig = new Rig("operator");

        var denied = await Assert.ThrowsAsync<ForbiddenException>(
            () => rig.Service.SyncAsync(rig.Source(reference), dryRun: true, default));

        Assert.Equal(SecretResolver.AccessDeniedCode, denied.Code);
        Assert.Equal(0, rig.Protector.Decryptions);
        Assert.Equal(0, rig.Http.Requests);
    }

    [Fact]
    public async Task For_an_operator_a_bare_secret_name_is_never_looked_up_and_the_stored_value_is_not_sent()
    {
        using var rig = new Rig("operator");

        var result = await rig.Service.SyncAsync(rig.Source("example-token"), dryRun: true, default);

        Assert.Equal(1, result.Created);
        Assert.Equal("Token example-token", rig.Http.Authorization);
        Assert.DoesNotContain(StoredValue, rig.Http.Authorization);
        Assert.Equal(0, rig.Protector.Decryptions);
    }

    [Theory]
    [InlineData("${secret:secret:example-token:value}")]
    [InlineData("example-token")]
    public async Task An_admin_still_resolves_a_stored_secret_by_reference_or_by_name(string reference)
    {
        using var rig = new Rig("admin");

        var result = await rig.Service.SyncAsync(rig.Source(reference), dryRun: true, default);

        Assert.Equal(1, result.Created);
        Assert.Equal($"Token {StoredValue}", rig.Http.Authorization);
        Assert.Equal(1, rig.Protector.Decryptions);
    }

    [Fact]
    public async Task An_admin_still_falls_back_to_a_legacy_literal_when_no_secret_has_that_name()
    {
        using var rig = new Rig("admin");

        await rig.Service.SyncAsync(rig.Source(LegacyLiteral), dryRun: true, default);

        Assert.Equal($"Token {LegacyLiteral}", rig.Http.Authorization);
        Assert.Equal(0, rig.Protector.Decryptions);
    }

    // Answers like NetBox with one device and records the Authorization header it received.
    internal sealed class Transport : IHttpClientFactory
    {
        public int Requests;
        public string? Authorization;

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Transport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Requests++;
                owner.Authorization = request.Headers.TryGetValues("Authorization", out var v) ? v.Single() : null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"results":[{"id":7,"name":"example-device"}],"next":null}""", Encoding.UTF8, "application/json"),
                });
            }
        }
    }

    private sealed class CountingProtector : ISecretProtector
    {
        public int Decryptions;
        public byte[]? Encrypt(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);
        public string? Decrypt(byte[]? value)
        {
            Decryptions++;
            return value is null ? null : Encoding.UTF8.GetString(value);
        }
    }

    private sealed class QuietTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null, string? error = null, int? durationMs = null) { }
        public ITraceScope Begin(string category, string action, object? metadata = null) => throw new NotSupportedException();
    }
}
