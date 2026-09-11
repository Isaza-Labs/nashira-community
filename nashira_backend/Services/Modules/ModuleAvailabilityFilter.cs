using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.DTos.Modules;

namespace nashira_backend.Services.Modules;

// Central gate for endpoints owned by a disabled module. A resource filter — not an
// action filter — because it has to answer before model binding runs: binding a body
// for an action that will never execute is work the deployment did not ask for, and it
// turns a 503 into a 400 whenever the payload does not parse.
//
// Authentication and authorization already ran (UseAuthorization, then MVC's
// authorization filters), so an unauthorized caller keeps getting 401/403 and never
// learns which modules this deployment enabled.
//
// Short-circuiting here also means the action, its action filters and the mutation
// audit never run: a blocked request leaves no trace beyond the request log.
public sealed class ModuleAvailabilityFilter : IAsyncResourceFilter
{
    private readonly ModuleSelection _selection;

    public ModuleAvailabilityFilter(ModuleSelection selection) => _selection = selection;

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        var disabled = ModuleEndpointRequirements.FirstDisabled(
            context.ActionDescriptor.EndpointMetadata,
            _selection);

        if (disabled is not { } module)
        {
            await next();
            return;
        }

        context.Result = ModuleDisabledResult(module);
    }

    // No Retry-After: enabling a module is an explicit recreation of the deployment,
    // not a wait. Advertising a delay would invite clients to retry forever.
    public static ObjectResult ModuleDisabledResult(ModuleId module)
    {
        var key = ModuleCatalog.Get(module).Key;
        var detail = $"module '{key}' is disabled for this deployment";
        var problem = new ModuleDisabledProblem(
            Type: $"https://httpstatuses.io/{StatusCodes.Status503ServiceUnavailable}",
            Title: ModuleDisabledCode,
            Status: StatusCodes.Status503ServiceUnavailable,
            Detail: detail,
            Error: detail,
            Code: ModuleDisabledCode,
            Module: key);

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentTypes = { "application/problem+json" },
        };
    }

    public const string ModuleDisabledCode = "module_disabled";
}
