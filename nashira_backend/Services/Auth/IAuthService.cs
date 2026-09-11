using Microsoft.AspNetCore.Mvc;
using nashira_backend.Data.DTos.Auth;

namespace nashira_backend.Services.Auth;

// Auth surface for the AuthController. Methods return ActionResult so the
// controller stays a passthrough — the service owns the HTTP shape. `ip` and
// `userAgent` are captured by the controller and passed in so the service layer
// doesn't reach into IHttpContextAccessor.
public interface IAuthService
{
    Task<ActionResult<LoginResponse>> LoginAsync(
        LoginRequest request, string ip, string userAgent, CancellationToken ct);

    Task<ActionResult<LoginResponse>> RefreshAsync(
        RefreshRequest request, string ip, string userAgent, CancellationToken ct);

    Task<ActionResult> LogoutAsync(
        RefreshRequest request, Guid userId,
        string ip, string userAgent, CancellationToken ct);

    Task<ActionResult> ChangePasswordAsync(
        ChangePasswordRequest request, Guid userId,
        string ip, string userAgent, CancellationToken ct);

    Task<ActionResult<MeResponse>> MeAsync(Guid userId, CancellationToken ct);

    // Development-only: creates the seed admin + returns tokens.
    Task<ActionResult<BootstrapResponse>> BootstrapAsync(
        string ip, string userAgent, CancellationToken ct);
}
