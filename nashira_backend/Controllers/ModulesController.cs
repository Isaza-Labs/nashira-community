using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.DTos.Modules;
using nashira_backend.Services.Modules;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// The deployment's own capability manifest. Part of core, so it answers in every
// deployment — the SPA bootstraps from it and cannot be asked to guess. Authenticated:
// which capabilities an installation runs is operational detail, not a public banner.
[ApiController]
[Route("api/modules")]
[Authorize]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class ModulesController : ControllerBase
{
    private readonly ModuleSelection _selection;

    public ModulesController(ModuleSelection selection) => _selection = selection;

    [HttpGet]
    public ActionResult<ModuleManifestResponse> Get() => ModuleManifest.Build(_selection);
}
