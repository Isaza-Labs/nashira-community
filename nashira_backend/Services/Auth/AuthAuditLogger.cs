using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Auth;

public sealed class AuthAuditLogger : IAuthAuditLogger
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuthAuditLogger> _logger;

    public AuthAuditLogger(AppDbContext db, ILogger<AuthAuditLogger> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(
        AuthEventKind kind,
        Guid? userId,
        string ip,
        string userAgent,
        object? metadata = null,
        CancellationToken ct = default)
    {
        var row = new AuthEvent
        {
            AuthEventId = Guid.NewGuid(),
            UserId = userId,
            Event = kind.ToWireString(),
            Ip = ip ?? string.Empty,
            // Capped: a User-Agent is attacker-controlled input on an endpoint that
            // accepts unauthenticated requests, and this table is written once per
            // sign-in attempt.
            UserAgent = Truncate(userAgent, 512),
            At = DateTime.UtcNow,
            // Capped for the same reason as the User-Agent, and it matters more: the
            // failed-sign-in path serialises the username that was ATTEMPTED, and that
            // is a request body field on an anonymous endpoint. Uncapped, one POST with
            // a multi-megabyte username writes a multi-megabyte row, unauthenticated,
            // into a table nothing prunes.
            MetadataJson = metadata is null
                ? null
                : Truncate(JsonSerializer.Serialize(metadata), MaxMetadataChars),
        };

        try
        {
            _db.AuthEvents.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Swallowed, deliberately. Every call site writes this AFTER deciding the
            // outcome of the request, so rethrowing would turn a correct 401 into a 500
            // — telling a user their password was wrong becomes telling them the server
            // is broken, and a failed sign-in that should have been recorded is instead
            // an incident. Losing the row is bad; changing the answer is worse.
            //
            // Logged at Error precisely so it can be alerted on: it means the auth trail
            // has a hole.
            _logger.LogError(ex,
                "auth.audit.write_failed event={Event} user_id={UserId}", row.Event, userId);
        }
    }

    // Generous enough for every legitimate payload here (a reason, a username, a
    // lockout timestamp) and far below anything worth storing from a stranger.
    private const int MaxMetadataChars = 2048;

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max];
    }
}
