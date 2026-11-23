using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Net;
using nashira_backend.Services.Notifications;

namespace nashira_backend.Tests;

public class GuardedHttpClientTests
{
    [Theory]
    [InlineData(300, "http://[fd00::1]/")]
    [InlineData(301, "http://[fc00::1]/")]
    [InlineData(302, "http://[fd00::1]/")]
    [InlineData(303, "http://[fe80::1]/")]
    [InlineData(307, "http://[::ffff:169.254.169.254]/")]
    [InlineData(308, "http://127.0.0.1/")]
    public async Task Redirect_targets_are_checked_without_a_second_request(int status, string target)
    {
        var transport = new ResponseHandler((HttpStatusCode)status, target);
        using var client = new HttpClient(new RejectRedirectHandler(Guard()) { InnerHandler = transport });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("https://public.invalid/start"));

        Assert.Contains("SSRF blocked", error.Message);
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task A_relative_redirect_rechecks_dns_for_the_original_host()
    {
        var lookups = 0;
        var guard = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance,
            _ => [IPAddress.Parse(++lookups == 1 ? "2606:4700:4700::1111" : "fd00::1")]);
        guard.EnsureSafe("https://test.invalid/start");
        var transport = new ResponseHandler(HttpStatusCode.Redirect, "/next");
        using var client = new HttpClient(new RejectRedirectHandler(guard) { InnerHandler = transport });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("https://test.invalid/start"));

        Assert.Contains("SSRF blocked", error.Message);
        Assert.Equal(2, lookups);
        Assert.Equal(1, transport.Requests);
    }

    [Theory]
    [InlineData("https://[2606:4700:4700::1111]/next")]
    [InlineData(null)]
    public async Task Other_redirects_also_fail_closed_instead_of_replaying_credentials(string? location)
    {
        var transport = new ResponseHandler(HttpStatusCode.TemporaryRedirect, location);
        using var client = new HttpClient(new RejectRedirectHandler(Guard()) { InnerHandler = transport });

        var error = await Assert.ThrowsAsync<RedirectRefusedException>(
            () => client.PostAsync("https://public.invalid/start", new StringContent("sensitive-body")));

        Assert.Contains("redirects are disabled", error.Message);
        Assert.Equal(307, error.StatusCode);
        Assert.Equal("redirect_refused", RedirectRefusedException.Code);
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task A_normal_response_is_preserved()
    {
        using var client = new HttpClient(new RejectRedirectHandler(Guard())
        {
            InnerHandler = new ResponseHandler(HttpStatusCode.OK, null),
        });
        using var response = await client.GetAsync("https://public.invalid/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Exercise the real named-client registration and transport, not just the
    // delegating handler: automatic redirects underneath it would hide the 302.
    // This listener is created by the test on an ephemeral loopback port only.
    [Theory]
    [InlineData("rest_call")]
    [InlineData("rest_call_insecure")]
    [InlineData(IntegrationHttpClients.Secure)]
    [InlineData(IntegrationHttpClients.Insecure)]
    [InlineData(McpHttpClients.Secure)]
    [InlineData(McpHttpClients.Insecure)]
    [InlineData(NotificationHttpClients.Name)]
    public async Task Named_clients_never_follow_a_redirect_in_the_transport(string name)
    {
        var services = new ServiceCollection();
        var guard = new RecordingGuard();
        services.AddSingleton<IUrlGuard>(guard);
        services.AddGuardedHttpClients();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var count = 0;
        var server = ServeAsync();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var error = await Assert.ThrowsAsync<RedirectRefusedException>(
                () => client.GetAsync($"http://127.0.0.1:{port}/start", stop.Token));
            Assert.Contains("redirects are disabled", error.Message);
            Assert.Equal(302, error.StatusCode);
            Assert.Equal(1, Volatile.Read(ref count));
            Assert.Single(guard.Urls);
            Assert.EndsWith("/redirected", guard.Urls[0]);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await server;
        }

        async Task ServeAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                    var current = Interlocked.Increment(ref count);
                    var headers = current == 1
                        ? "HTTP/1.1 302 Found\r\nLocation: /redirected\r\n"
                        : "HTTP/1.1 200 OK\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        headers + "Content-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
    }

    private static UrlGuard Guard() => new(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance);

    private sealed class RecordingGuard : IUrlGuard
    {
        public List<string> Urls { get; } = [];
        public void EnsureSafe(string url, bool allowPrivate = false) => Urls.Add(url);
    }

    private sealed class ResponseHandler(HttpStatusCode status, string? location) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var response = new HttpResponseMessage(status);
            if (location is not null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return Task.FromResult(response);
        }
    }
}
