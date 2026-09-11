using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Errors;

// Maps DomainException subtypes to RFC 7807 problem+json with the right status.
// A top-level `error` field is included for simple clients. Non-domain
// exceptions are left for the framework's default handler.
public sealed class DomainExceptionHandler : IExceptionHandler
{
    private readonly ILogger<DomainExceptionHandler> _logger;

    public DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not DomainException dex)
            return false;

        _logger.LogWarning(
            "domain.exception code={Code} status={Status} message={Message}",
            dex.Code, dex.Status, dex.Message);

        context.Response.StatusCode = dex.Status;
        context.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type = $"https://httpstatuses.io/{dex.Status}",
            title = dex.Code,
            status = dex.Status,
            detail = dex.Message,
            error = dex.Message,
            code = dex.Code,
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem), ct);
        return true;
    }
}
