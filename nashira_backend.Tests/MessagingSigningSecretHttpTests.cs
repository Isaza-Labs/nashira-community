using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Messaging;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

public class MessagingSigningSecretHttpTests
{
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["Test-Role"].ToString();
            if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001"),
                new Claim(ClaimTypes.Name, "test-admin"), new Claim(ClaimTypes.Role, role) }, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class Protector : ISecretProtector
    {
        public byte[]? Encrypt(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);
        public string? Decrypt(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);
    }

    [Fact]
    public async Task Real_http_routes_enforce_admin_on_reads_and_rotation_and_never_echo_secrets()
    {
        // Disposable loopback host with an in-memory DB; no deployment configuration
        // or production authentication is loaded, and no external port is published.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var database = Guid.NewGuid().ToString();
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
        builder.Services.AddScoped<IAuditLogger, AuditLogger>();
        builder.Services.AddSingleton<ISecretProtector, Protector>();
        builder.Services.Configure<MessagingOptions>(_ => { });
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("admin")));
        builder.Services.AddRateLimiter(o =>
        {
            o.AddConcurrencyLimiter(RateLimitingConfiguration.ReadHeavy, p => p.PermitLimit = 100);
            o.AddConcurrencyLimiter(RateLimitingConfiguration.WriteNormal, p => p.PermitLimit = 100);
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(MessagingChannelController).Assembly);
        await using var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("Test-Role", "admin");
            const string secret = "0123456789abcdef0123456789abcdef";
            using var create = await client.PostAsJsonAsync("/api/messaging/channels", new { provider = "telegram", name = "http-test", signing_secret = secret });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var body = await create.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, body);
            var id = JsonDocument.Parse(body).RootElement.GetProperty("messaging_channel_id").GetGuid();
            foreach (var role in new[] { "operator", "viewer", "admin", "" })
            {
                client.DefaultRequestHeaders.Remove("Test-Role");
                if (role.Length > 0) client.DefaultRequestHeaders.Add("Test-Role", role);
                var expected = role == "admin" ? HttpStatusCode.OK : role == "" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
                foreach (var path in new[] { "/api/messaging/channels", $"/api/messaging/channels/{id}" })
                {
                    using var response = await client.GetAsync(path);
                    Assert.Equal(expected, response.StatusCode);
                    Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync());
                }
                using var rotate = await client.PutAsJsonAsync($"/api/messaging/channels/{id}", new { signing_secret = secret });
                Assert.Equal(expected, rotate.StatusCode);
                Assert.DoesNotContain(secret, await rotate.Content.ReadAsStringAsync());
            }
        }
        finally { await app.StopAsync(); }
    }
}
