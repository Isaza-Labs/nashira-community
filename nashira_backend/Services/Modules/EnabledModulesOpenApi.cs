using Microsoft.AspNetCore.Mvc.ApiExplorer;
using nashira_backend.Configuration.Modules;

namespace nashira_backend.Services.Modules;

// Keeps the published API description honest: an operation whose capability is off in
// this deployment answers 503 and therefore has no business appearing in OpenAPI or in
// the Scalar reference. The router still holds the route — that is what produces the
// uniform 503 instead of a 404 — but the document describes what can actually be called.
//
// A filter over ApiDescription rather than a document transformer: dropping the
// operation before the document is built also drops the schemas only it referenced,
// where a transformer would have to prune paths and then chase the orphans.
public static class EnabledModulesOpenApi
{
    public static IServiceCollection AddModuleAwareOpenApi(
        this IServiceCollection services,
        ModuleSelection selection) =>
        services.AddOpenApi(options =>
        {
            // Compose rather than replace: the default predicate is what keeps an
            // operation in the document it declared a group name for.
            var includedByDefault = options.ShouldInclude;
            options.ShouldInclude = description =>
                includedByDefault(description) && IsAvailable(description, selection);
        });

    public static bool IsAvailable(ApiDescription description, ModuleSelection selection) =>
        ModuleEndpointRequirements.FirstDisabled(
            description.ActionDescriptor.EndpointMetadata,
            selection) is null;
}
