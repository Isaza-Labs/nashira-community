using Microsoft.AspNetCore.Mvc.ApplicationModels;
using nashira_backend.Configuration.Modules;

namespace nashira_backend.Services.Modules;

// Turns the ownership table into endpoint metadata, once, while MVC builds its action
// model — so the availability gate and the OpenAPI document both read the requirement
// off the endpoint instead of each consulting the catalog their own way.
//
// An action the catalog does not classify stops the process. The alternative — treating
// it as always available — is how a module silently leaks: the endpoint keeps answering
// in a deployment that was never meant to run it, and nobody finds out from a test that
// still passes. Startup is where this is cheap to notice.
public sealed class ModuleControllerConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        var unclassified = new List<string>();

        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                var modules = EndpointModuleCatalog.For(
                    controller.ControllerType.AsType(),
                    action.ActionMethod.Name);

                if (modules is null)
                {
                    unclassified.Add($"{controller.ControllerType.Name}.{action.ActionMethod.Name}");
                    continue;
                }

                // One instance shared by the selectors of the same action: they describe
                // the same action reached through different routes, not different work.
                var requirement = new RequiresModuleAttribute([.. modules]);
                foreach (var selector in action.Selectors)
                    selector.EndpointMetadata.Add(requirement);
            }
        }

        if (unclassified.Count > 0)
            throw new InvalidOperationException(
                "Every controller action must declare the capability that owns it. "
                + "Add these to EndpointModuleCatalog:\n- "
                + string.Join("\n- ", unclassified.Order(StringComparer.Ordinal)));
    }
}
