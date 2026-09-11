using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace nashira_backend.Services.Errors;

// Last-resort handler for bugs/infrastructure failures. DomainExceptionHandler owns
// expected API errors; this one exists so unexpected 500s always leave a stack trace.
public sealed class UnhandledExceptionHandler : IExceptionHandler
{
    private readonly ILogger<UnhandledExceptionHandler> _logger;

    public UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        _logger.LogError(
            exception,
            "unhandled.exception method={Method} path={Path} trace_id={TraceId}",
            context.Request.Method,
            context.Request.Path.Value,
            context.TraceIdentifier);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type = "https://httpstatuses.io/500",
            title = "internal_server_error",
            status = StatusCodes.Status500InternalServerError,
            detail = "An unexpected error occurred.",
            error = "An unexpected error occurred.",
            code = "internal_server_error",
            trace_id = context.TraceIdentifier,
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem), ct);
        return true;
    }
}
