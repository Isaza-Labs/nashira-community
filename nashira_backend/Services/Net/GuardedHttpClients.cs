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

    /// <summary>The deepest level of an exception graph that is searched (the failure itself is level 0).</summary>
    internal const int MaxSearchDepth = 8;

    /// <summary>The most distinct exceptions examined in one search, however the graph is shaped.</summary>
    internal const int MaxSearchNodes = 64;

    /// <summary>
    /// The most child links followed in one search. Links to an exception that was already examined cost
    /// nothing against <see cref="MaxSearchNodes"/>, so this is what bounds a very wide or repetitive list.
    /// </summary>
    internal const int MaxSearchLinks = 256;

    /// <summary>
    /// Looks for the refusal behind a failure. A client library between the transport and the caller (the
    /// MCP SDK, for one) may wrap what the handler threw, so the graph of inner exceptions (every child of an
    /// <see cref="AggregateException"/> included) is searched instead of relying on the outermost type.
    /// The walk is depth-first and visits children in order. An exception reached again by a shorter path
    /// is searched again from there, so what is found does not depend on which path got to it first.
    /// One node budget, one link budget and one depth limit cover the whole search. There are three answers:
    /// <see cref="RedirectSearchOutcome.Found"/>; <see cref="RedirectSearchOutcome.NotFound"/>, which means
    /// every exception in the graph was examined; and <see cref="RedirectSearchOutcome.Incomplete"/>, which
    /// means a limit stopped the search before it could say. A caller must treat the last one as unknown:
    /// it neither reports a refusal nor reads the exception graph's text.
    /// </summary>
    public static RedirectSearch Search(Exception? error)
    {
        if (error is null) return RedirectSearch.NotFoundResult;

        var depthOf = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
        var cutShort = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var path = new Stack<SearchFrame>();
        var links = 0;

        Exception? entering = error;
        var enteringDepth = 0;
        while (true)
        {
            if (entering is not null)
            {
                var node = entering;
                var depth = enteringDepth;
                entering = null;

                if (depthOf.TryGetValue(node, out var seenAt))
                {
                    // Already searched from here or from a shallower point: nothing new below it.
                    if (seenAt <= depth) continue;
                }
                else if (depthOf.Count >= MaxSearchNodes)
                {
                    return RedirectSearch.IncompleteResult;
                }
                depthOf[node] = depth;

                if (node is RedirectRefusedException refused) return RedirectSearch.FoundResult(refused);

                var children = ChildrenOf(node);
                if (children.Count == 0)
                {
                    cutShort.Remove(node);
                }
                else if (depth + 1 >= MaxSearchDepth)
                {
                    cutShort.Add(node);
                }
                else
                {
                    cutShort.Remove(node);
                    path.Push(new SearchFrame(depth, children));
                }
                continue;
            }

            if (path.Count == 0) break;
            var frame = path.Peek();
            if (frame.Next >= frame.Children.Count)
            {
                path.Pop();
                continue;
            }
            if (++links > MaxSearchLinks) return RedirectSearch.IncompleteResult;
            entering = frame.Children[frame.Next++];
            enteringDepth = frame.Depth + 1;
        }

        // An exception whose children sat below the depth limit, and that was not searched again from a
        // shallower point, leaves the answer open.
        return cutShort.Count > 0 ? RedirectSearch.IncompleteResult : RedirectSearch.NotFoundResult;
    }

    private static IReadOnlyList<Exception> ChildrenOf(Exception error) => error switch
    {
        AggregateException aggregate => aggregate.InnerExceptions,
        { InnerException: { } inner } => [inner],
        _ => [],
    };

    private sealed class SearchFrame(int depth, IReadOnlyList<Exception> children)
    {
        public int Depth { get; } = depth;
        public IReadOnlyList<Exception> Children { get; } = children;
        public int Next { get; set; }
    }
}

public enum RedirectSearchOutcome
{
    /// <summary>Every exception in the graph was examined and none is a refusal.</summary>
    NotFound,

    /// <summary>A refusal is in the graph.</summary>
    Found,

    /// <summary>A limit stopped the search; whether the graph holds a refusal is not known.</summary>
    Incomplete,
}

/// <summary>The answer of <see cref="RedirectRefusedException.Search"/>.</summary>
public readonly record struct RedirectSearch
{
    /// <summary>
    /// The error code for a failure whose graph could not be searched in full. It is not a refusal code,
    /// and it is final: repeating the call would run the original request again.
    /// </summary>
    public const string IncompleteCode = "failure_unclassified";

    /// <summary>The fixed text an incomplete search is reported with; nothing from the failure goes in it.</summary>
    public const string IncompleteMessage =
        "MCP call failed; the error details were withheld because the failure could not be fully inspected";

    public RedirectSearchOutcome Outcome { get; private init; }

    /// <summary>The refusal, when <see cref="Outcome"/> is <see cref="RedirectSearchOutcome.Found"/>.</summary>
    public RedirectRefusedException? Refusal { get; private init; }

    public bool IsIncomplete => Outcome == RedirectSearchOutcome.Incomplete;

    internal static RedirectSearch NotFoundResult => new() { Outcome = RedirectSearchOutcome.NotFound };
    internal static RedirectSearch IncompleteResult => new() { Outcome = RedirectSearchOutcome.Incomplete };
    internal static RedirectSearch FoundResult(RedirectRefusedException refusal) =>
        new() { Outcome = RedirectSearchOutcome.Found, Refusal = refusal };
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
