using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace nashira_backend.Services.Errors;

// Turns a Postgres unique-index violation (23505) into the 409 the pre-insert
// duplicate checks already produce. Those checks can never fully close the
// race between the SELECT and the INSERT, so without this the losing request
// surfaced as a 500 with a stack trace instead of a conflict the client can
// handle the same way as the ordinary "name already exists" path.
public sealed class UniqueViolationExceptionHandler : IExceptionHandler
{
    private readonly ILogger<UniqueViolationExceptionHandler> _logger;

    public UniqueViolationExceptionHandler(ILogger<UniqueViolationExceptionHandler> logger)
        => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not DbUpdateException
            {
                InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            })
            return false;

        _logger.LogWarning(
            "db.unique_violation constraint={Constraint} table={Table} method={Method} path={Path}",
            pg.ConstraintName, pg.TableName, context.Request.Method, context.Request.Path.Value);

        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type = "https://httpstatuses.io/409",
            title = "conflict",
            status = StatusCodes.Status409Conflict,
            detail = "another record with the same unique value already exists",
            error = "another record with the same unique value already exists",
            code = "conflict",
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem), ct);
        return true;
    }
}
