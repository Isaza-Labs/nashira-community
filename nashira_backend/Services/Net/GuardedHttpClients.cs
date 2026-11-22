using nashira_backend.Services.Integration;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Notifications;

namespace nashira_backend.Services.Net;

// Clients whose destinations are checked by UrlGuard must not let the transport
// silently follow a redirect past that check. Keep TLS policy and timeouts intact.
public static class GuardedHttpClients
{
    public static IServiceCollection AddGuardedHttpClients(this IServiceCollection services)
    {
        Add(services, "rest_call");
        Add(services, "rest_call_insecure", insecure: true);
        Add(services, IntegrationHttpClients.Secure);
        Add(services, IntegrationHttpClients.Insecure, insecure: true);
        Add(services, McpHttpClients.Secure)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
        Add(services, McpHttpClients.Insecure, insecure: true)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
        Add(services, NotificationHttpClients.Name)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20));
        return services;
    }

    private static IHttpClientBuilder Add(IServiceCollection services, string name, bool insecure = false)
        => services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = insecure
                    ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    : null,
            })
            .AddHttpMessageHandler(sp => new RejectRedirectHandler(sp.GetRequiredService<IUrlGuard>()));
}

// Recheck redirect destinations (including DNS answers), then fail closed rather
// than replaying a request/body or forwarding credentials to a new destination.
// Integrations that redirect must be configured with their final endpoint.
internal sealed class RejectRedirectHandler(IUrlGuard guard) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if ((int)response.StatusCode is not (300 or 301 or 302 or 303 or 307 or 308))
            return response;

        try
        {
            if (response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
                guard.EnsureSafe(target.AbsoluteUri);
            }
            throw new InvalidOperationException("HTTP redirects are disabled for guarded requests; configure the final endpoint");
        }
        finally
        {
            response.Dispose();
        }
    }
}
