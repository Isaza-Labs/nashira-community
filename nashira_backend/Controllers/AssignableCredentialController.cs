using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Credential;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// The credentials an Operator may link to a device: the device form's picker.
//
// Its own controller on purpose. CredentialController is [Authorize(Policy = "Admin")]
// at class level, and ASP.NET Core ANDs class- and action-level [Authorize], so an
// Operator policy on one of its actions would still require Admin. Keeping this the
// only action here means an Operator reaches exactly this and nothing else under
// /api/credential.
//
// Returns only active credentials an Admin made assignable AND scoped (the same
// condition DeviceTrustPolicy applies), and only id, name, auth method and scope: no
// username, no has_* flags, nothing secret.
[ApiController]
[Route("api/credential")]
[Authorize(Policy = "Operator")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AssignableCredentialController : ControllerBase
{
    private readonly AppDbContext _db;

    public AssignableCredentialController(AppDbContext db) => _db = db;

    [HttpGet("assignable")]
    public async Task<ActionResult<List<AssignableCredentialResponse>>> Assignable(CancellationToken ct)
    {
        var rows = await _db.Credentials.AsNoTracking()
            .Where(c => c.IsActive && c.OperatorAssignable && c.AllowedHosts != null && c.AllowedHosts != "")
            .OrderBy(c => c.Name)
            .Select(c => new AssignableCredentialResponse
            {
                CredentialId = c.CredentialId,
                Name = c.Name,
                AuthMethod = c.AuthMethod,
                AllowedHosts = c.AllowedHosts!,
            })
            .ToListAsync(ct);
        return rows;
    }
}
