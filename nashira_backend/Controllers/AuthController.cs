using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Observability;
using nashira_backend.Services.Security;
using nashira_backend.Data.DTos.Auth;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Auth;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Audit;
using nashira_backend.Services.Common;

namespace nashira_backend.Controllers;

// Auth endpoints are security events, not business mutations, so they are kept out of
// the CRUD audit trail: they write to the dedicated AuthEvent log instead, which this
// controller also exposes for reading.
[ApiController]
[Route("api/[controller]")]
[SkipAudit]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly IWebHostEnvironment _env;
    private readonly AppDbContext _db;

    public AuthController(IAuthService auth, IWebHostEnvironment env, AppDbContext db)
    {
        _auth = auth;
        _env = env;
        _db = db;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConfiguration.AuthLogin)]
    public Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
        => _auth.LoginAsync(request, Ip(), UserAgent(), ct);

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConfiguration.AuthLogin)]
    public Task<ActionResult<LoginResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
        => _auth.RefreshAsync(request, Ip(), UserAgent(), ct);

    [HttpPost("logout")]
    [Authorize(Policy = "Viewer")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult> Logout([FromBody] RefreshRequest request, CancellationToken ct)
        => _auth.LogoutAsync(request, CurrentUserId(), Ip(), UserAgent(), ct);

    [HttpPost("change-password")]
    [Authorize(Policy = "Viewer")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
        => _auth.ChangePasswordAsync(request, CurrentUserId(), Ip(), UserAgent(), ct);

    [HttpGet("me")]
    [Authorize(Policy = "Viewer")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult<MeResponse>> Me(CancellationToken ct)
        => _auth.MeAsync(CurrentUserId(), ct);

    // Development-only: creates a company + admin + tokens so you can curl the API.
    // In Production this 404s so there's no leak surface.
    [HttpPost("bootstrap")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConfiguration.AuthLogin)]
    public async Task<ActionResult<BootstrapResponse>> Bootstrap(CancellationToken ct)
    {
        if (!_env.IsDevelopment()) return NotFound();
        return await _auth.BootstrapAsync(Ip(), UserAgent(), ct);
    }

    // The authentication trail. Admin-only, and the one read on this controller.
    [HttpGet("events")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<ListResponse<AuthEventSummary>>> Events(
        Guid? userId = null, string? @event = null,
        DateTime? from = null, DateTime? to = null,
        bool includeUnattributed = false,
        int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var q = _db.AuthEvents.AsNoTracking();

        // Sign-in failures for a username that does not exist never resolve to a user.
        // They are kept — a burst of them from one address is account enumeration — but
        // hidden by default, because on a public endpoint they are also the noisiest
        // rows in the table and would bury failures on real accounts.
        if (!includeUnattributed) q = q.Where(e => e.UserId != null);
        if (userId is { } uid) q = q.Where(e => e.UserId == uid);
        if (!string.IsNullOrWhiteSpace(@event)) q = q.Where(e => e.Event == @event);
        if (from is { } f) q = q.Where(e => e.At >= f.ToUniversalTime());
        if (to is { } t) q = q.Where(e => e.At <= t.ToUniversalTime());

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(e => e.At).Skip(offset).Take(limit).ToListAsync(ct);

        var ids = rows.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).Distinct().ToList();
        var usernames = ids.Count == 0
            ? []
            : await _db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.UserId))
                .ToDictionaryAsync(u => u.UserId, u => u.Username, ct);

        return new OkObjectResult(new ListResponse<AuthEventSummary>
        {
            Items = rows.Select(e => new AuthEventSummary
            {
                AuthEventId = e.AuthEventId,
                At = e.At,
                UserId = e.UserId,
                Username = e.UserId is { } id ? usernames.GetValueOrDefault(id) : null,
                Event = e.Event,
                Ip = e.Ip,
                UserAgent = e.UserAgent,
                Metadata = ParseMetadata(e.MetadataJson),
            }).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    // A row whose metadata will not parse is still a row worth showing; dropping the
    // whole event over its context would hide the event itself.
    private static JsonElement? ParseMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string Ip() => ClientIp.Resolve(HttpContext) ?? string.Empty;
    private string UserAgent() => Request.Headers.UserAgent.ToString();
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
