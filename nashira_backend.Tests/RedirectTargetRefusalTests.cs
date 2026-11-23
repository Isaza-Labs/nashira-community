using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Git;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Inventory;
using nashira_backend.Services.Net;
using nashira_backend.Services.Worker.Handlers;
using IntegrationEntity = nashira_backend.Data.Models.Integration;
using Mapping = nashira_backend.Tests.RedirectRefusalMappingTests;

namespace nashira_backend.Tests;

// A redirect whose target the validator rejects (a blocked address, a failed lookup, a non-HTTP
// scheme) used to escape as the validator's own exception, which repeats the Location. Each
// caller then missed its redirect mapping, and the text could carry whatever the remote server
// put in the header. These tests use the real validator with injected DNS, so nothing resolves.
public class RedirectTargetRefusalTests
{
    private const string Code = "redirect_refused";
    private const string Token = Mapping.LocationToken;

    public static TheoryData<string, bool> Targets => new()
    {
        { $"http://[fd00::1]/next?t={Token}", false },                  // blocked address
        { $"http://127.0.0.1/next?t={Token}", false },                  // loopback
        { $"ftp://203.0.113.50/next?t={Token}", false },                // not HTTP(S)
        { $"https://example-lookup-fails.invalid/next?t={Token}", true }, // DNS failure
    };

    private static Mapping.RedirectingFactory Factory(string location, bool dnsFails)
    {
        var guard = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance,
            host => dnsFails ? throw new SocketException(11001) : [IPAddress.Parse("2606:4700:4700::1111")]);
        return new Mapping.RedirectingFactory(HttpStatusCode.Found, location, guard);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task The_handler_throws_only_the_typed_refusal_and_never_the_location(string location, bool dnsFails)
    {
        var factory = Factory(location, dnsFails);
        using var client = factory.CreateClient("x");

        var error = await Assert.ThrowsAsync<RedirectRefusedException>(
            () => client.GetAsync("https://203.0.113.10/start"));

        Assert.Equal(RedirectRefusalReason.TargetNotAllowed, error.Reason);
        Assert.Equal(302, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.DoesNotContain("fd00", error.ToString());
        Assert.Equal(1, factory.Requests);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Rest_call_raw_form_maps_a_rejected_target_to_redirect_refused(string location, bool dnsFails)
    {
        var factory = Factory(location, dnsFails);
        var handler = new RestCallSnippetHandler(
            new Mapping.UnusedExecutor(), new Mapping.AllowAllGuard(), new Mapping.PassthroughSecrets(), factory);

        var result = await handler.ExecuteAsync(
            Mapping.Request(Snippet.TypeRestCall, new { url = "https://203.0.113.20/status", method = "GET" }), default);

        Mapping.AssertRefusal(result, 302);
        Assert.Equal(1, factory.Requests);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Rest_call_catalogued_form_and_its_log_hide_a_rejected_target(string location, bool dnsFails)
    {
        using var db = Mapping.NewDb();
        var factory = Factory(location, dnsFails);
        var logger = new CapturingLogger<Services.Ai.RestExecutor.RestOperationExecutor>();
        var executor = await Mapping.NewExecutorAsync(db, factory, logger);
        var handler = new RestCallSnippetHandler(
            executor, new Mapping.AllowAllGuard(), new Mapping.PassthroughSecrets(), factory);

        var result = await handler.ExecuteAsync(
            Mapping.Request(Snippet.TypeRestCall, new { operation_id = "get_status" }), default);

        Mapping.AssertRefusal(result, 302);
        Assert.DoesNotContain(Token, logger.Text);
        Assert.DoesNotContain("fd00", logger.Text);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task The_chat_tool_hides_a_rejected_target(string location, bool dnsFails)
    {
        using var db = Mapping.NewDb();
        var tool = new ExecuteOperationHandler(await Mapping.NewExecutorAsync(db, Factory(location, dnsFails)));

        var output = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { operation_id = "get_status" }), default);

        Assert.Equal(Code, output.GetProperty("error_code").GetString());
        Assert.DoesNotContain(Token, output.GetRawText());
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task An_integration_check_with_a_rejected_target_is_degraded_not_unreachable(string location, bool dnsFails)
    {
        var checker = new IntegrationHealthChecker(
            new Mapping.NoAuth(), new Mapping.PassthroughSecrets(), new Mapping.AllowAllGuard(),
            Factory(location, dnsFails), NullLogger<IntegrationHealthChecker>.Instance);

        var health = await checker.CheckAsync(new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(), Name = "example-api", Slug = "example-api", Type = "generic",
            BaseUrl = "https://203.0.113.10", HealthCheckPath = "/status",
        }, default);

        Assert.Equal(IntegrationEntity.StatusDegraded, health.Status);
        Assert.Equal(302, health.StatusCode);
        Assert.DoesNotContain(Token, health.Error);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Github_and_netbox_map_a_rejected_target_to_a_400_with_the_code(string location, bool dnsFails)
    {
        using var db = Mapping.NewDb();
        var credentialId = Guid.NewGuid();
        db.Credentials.Add(new Credential
        {
            CredentialId = credentialId, Name = "example-git", Type = "git_token",
            AuthMethod = Credential.AuthMethodToken, EncryptedToken = [1], IsActive = true,
        });
        await db.SaveChangesAsync();
        var github = new GitHubService(
            db, new FixedProtector(), new Mapping.AllowAllGuard(), Factory(location, dnsFails),
            new ConfigurationBuilder().Build(), NullLogger<GitHubService>.Instance);
        var netbox = new NetBoxSyncService(
            db, new HardeningTestUser(), new Mapping.PassthroughSecrets(), new Mapping.AllowAllGuard(),
            Factory(location, dnsFails), NullLogger<NetBoxSyncService>.Instance);

        var gh = await Assert.ThrowsAsync<ValidationException>(() => github.CreateRepoAsync(
            new GitHubCreateRepoRequest { CredentialId = credentialId, Name = "example-repo" }, default));
        var nb = await Assert.ThrowsAsync<ValidationException>(() => netbox.SyncAsync(
            new InventorySource { Name = "example-netbox", BaseUrl = "https://203.0.113.30" }, dryRun: true, default));

        foreach (var error in new[] { gh, nb })
        {
            Assert.Equal(Code, error.Code);
            Assert.Equal(400, error.Status);
            Assert.DoesNotContain(Token, error.Message);
        }
    }

    private sealed class FixedProtector : Services.Security.ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) => plaintext is null ? null : [1];
        public string? Decrypt(byte[]? ciphertext) => ciphertext is null ? null : "example-token";
    }

    // Everything a component logs, message and exception text together.
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];
        public string Text => string.Join('\n', _lines);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _lines.Add(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
    }
}
