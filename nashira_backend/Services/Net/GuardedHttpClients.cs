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

// Why a guarded client refused a redirect. A bounded classification, deliberately without
// the target: the Location header belongs to the remote server and may carry anything.
public enum RedirectRefusalReason
{
    /// <summary>Redirects are not followed, and the target was not examined further or was acceptable.</summary>
    Disabled,

    /// <summary>The target failed validation: a blocked address, a failed lookup, a non-HTTP scheme or an unparseable URL.</summary>
    TargetNotAllowed,
}

// A guarded client refused to follow a redirect. It is an InvalidOperationException so existing
// handlers keep working, but callers that can say something better catch it by type. Every
// refusal, including one for a rejected target, is this type with fixed text: neither the
// Location header nor the validator's own message is carried.
public sealed class RedirectRefusedException(int statusCode, RedirectRefusalReason reason = RedirectRefusalReason.Disabled)
    : InvalidOperationException("HTTP redirects are disabled for guarded requests; configure the final endpoint")
{
    public const string Code = "redirect_refused";

    /// <summary>The redirect status the server answered with.</summary>
    public int StatusCode { get; } = statusCode;

    public RedirectRefusalReason Reason { get; } = reason;

    /// <summary>
    /// The refusal behind a failure, if there is one. A client library between the transport and the
    /// caller (the MCP SDK, for one) may wrap what the handler threw, so the inner chain is searched,
    /// to a fixed depth, instead of relying on the outermost type.
    /// </summary>
    public static RedirectRefusedException? Find(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; depth++)
        {
            if (error is RedirectRefusedException refused) return refused;
            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    if (Find(inner) is { } found) return found;
            }
            error = error.InnerException;
        }
        return null;
    }
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

        var status = (int)response.StatusCode;
        try
        {
            if (response.Headers.Location is { } location)
            {
                try
                {
                    var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
                    guard.EnsureSafe(target.AbsoluteUri);
                }
                catch (Exception)
                {
                    // Whatever the validator or the URI parser said can repeat the Location, so it is
                    // not kept; the refusal says only that the target was not acceptable.
                    throw new RedirectRefusedException(status, RedirectRefusalReason.TargetNotAllowed);
                }
            }
            throw new RedirectRefusedException(status);
        }
        finally
        {
            response.Dispose();
        }
    }
}
