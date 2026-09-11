using nashira_backend.Data.Models;

namespace nashira_backend.Services.Trace;

// One trace row per HTTP request that changes something or fails.
//
// Not per request full stop. The live-tail screen polls its own endpoint every few
// seconds, so tracing every GET means the trail fills with the act of reading the
// trail — and the rows an operator came to find scroll off the top faster the harder
// they look. Reads are traced only when they fail, which is when a read is interesting.
public sealed class TraceRequestMiddleware
{
    private static readonly HashSet<string> Mutating =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    // Endpoints whose whole job is polling. Tracing them is a feedback loop.
    private static readonly string[] Excluded =
    [
        "/api/admin/traces",
        "/api/health",
        "/health",
    ];

    private readonly RequestDelegate _next;
    private readonly ITraceLogger _trace;

    public TraceRequestMiddleware(RequestDelegate next, ITraceLogger trace)
    {
        _next = next;
        _trace = trace;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
            || Excluded.Any(e => path.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var method = context.Request.Method;
        var mutating = Mutating.Contains(method);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await _next(context);
        }
        catch
        {
            // An unhandled exception never reaches the status code below, so it is
            // recorded here and rethrown untouched — the error handler still owns the
            // response.
            Write(context, method, path, started, status: 500, failed: true);
            throw;
        }

        var code = context.Response.StatusCode;
        // A 4xx is the caller's mistake and a 5xx is ours; both are worth a row on any
        // verb, because "it returned 403 and I don't know why" is the question this
        // table gets opened for.
        if (mutating || code >= 400) Write(context, method, path, started, code, code >= 400);
    }

    private void Write(
        HttpContext context, string method, string path, long started, int status, bool failed)
    {
        var ms = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _trace.Event(
            TraceEvent.CategoryHttp,
            // The route template, not the concrete path: `/api/device/{id}` groups, while
            // a thousand distinct guid paths do not.
            $"http.{method.ToLowerInvariant()}.{Template(context, path)}",
            metadata: new { method, path, status },
            error: failed ? $"HTTP {status}" : null,
            durationMs: ms);
    }

    private static string Template(HttpContext context, string path)
    {
        var route = context.GetEndpoint() as RouteEndpoint;
        var template = route?.RoutePattern.RawText;
        return string.IsNullOrWhiteSpace(template) ? path.TrimStart('/') : template;
    }
}
