using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using nashira_backend.BackgroundServices;
using nashira_backend.Configuration.Modules;
using nashira_backend.Controllers;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Modules;
using nashira_backend.Services.Settings;
using nashira_backend.Services.Worker;

namespace nashira_backend.Tests;

// The deployment as a whole, rather than one catalog at a time.
//
// Each catalog has its own tests and each passes on its own; what those cannot show is
// that the same selection produces a coherent process — that all-enabled is still the
// product it was, that the reference minimal deployment exposes what it claims and
// nothing else, and that a blocked request leaves no trace in the places a mutation
// normally would.
public class ModuleCompositionTests
{
    private static readonly IReadOnlyList<ControllerActionDescriptor> Descriptors = BuildDescriptors();

    private static IReadOnlyList<ControllerActionDescriptor> BuildDescriptors()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddControllers(options => options.Conventions.Add(new ModuleControllerConvention()))
            .AddApplicationPart(typeof(AuthController).Assembly);

        return [.. services.BuildServiceProvider()
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()];
    }

    private static IReadOnlyList<ControllerActionDescriptor> Reachable(ModuleSelection selection) =>
        [.. Descriptors.Where(descriptor =>
            ModuleEndpointRequirements.FirstDisabled(descriptor.EndpointMetadata, selection) is null)];

    // Compatibility, stated as one assertion per activatable surface: a deployment that
    // configures nothing keeps every endpoint, tool, snippet, worker, seeder, setting
    // and page of documentation it had before any of this existed.
    [Fact]
    public void All_enabled_activates_the_entire_product()
    {
        var everything = ModuleSelection.Parse(null);

        Assert.Equal(Descriptors.Count, Reachable(everything).Count);
        Assert.Equal(ToolHandlerCatalog.All.Count, ToolHandlerCatalog.Enabled(everything).Count);
        Assert.Equal(SnippetHandlerCatalog.All.Count, SnippetHandlerCatalog.Enabled(everything).Count);
        Assert.Equal(HostedServiceCatalog.All.Count, HostedServiceCatalog.Enabled(everything).Count);
        Assert.Equal(
            SeederModuleCatalog.All.Count,
            SeederModuleCatalog.Enabled(everything, isDevelopment: true).Count);
        Assert.Equal(AppSettingDefinition.All.Count, AppSettingDefinition.For(everything).Count);
        Assert.All(
            ModuleContentCatalog.AllSpecs,
            api => Assert.True(ModuleContentCatalog.IsSpecAvailable(api, everything)));
        Assert.True(ModuleContentCatalog.IsSkillAvailable("git.md", everything));
        Assert.True(ModuleContentCatalog.IsSkillAvailable("snippets.md", everything));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("git-direct.md", everything));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("snippets-automation.md", everything));
    }

    // The reference deployment from the specification. Every activatable component is
    // owned by core, chat, ai-studio, integrations or secrets — the assertion is on what is left, not
    // on a list of what was removed, so a component that escapes classification later
    // fails here rather than passing unnoticed.
    [Fact]
    public void The_minimal_deployment_exposes_only_the_capabilities_it_selected()
    {
        var selection = ModuleSelection.Parse("chat,ai-studio,integrations,secrets");
        ModuleId[] expected = [
            ModuleId.Core, ModuleId.Chat, ModuleId.AiStudio, ModuleId.Integrations, ModuleId.Secrets];

        Assert.All(
            Reachable(selection),
            descriptor => Assert.All(
                ModuleEndpointRequirements.For(descriptor.EndpointMetadata),
                module => Assert.Contains(module, expected)));

        Assert.All(
            ToolHandlerCatalog.Enabled(selection),
            handler => Assert.All(
                ToolHandlerCatalog.RequirementsFor(handler),
                module => Assert.Contains(module, expected)));

        Assert.All(
            HostedServiceCatalog.Enabled(selection),
            service => Assert.All(
                HostedServiceCatalog.RequirementsFor(service),
                module => Assert.Contains(module, expected)));

        Assert.All(
            SeederModuleCatalog.Enabled(selection, isDevelopment: true),
            seeder => Assert.All(
                SeederModuleCatalog.RequirementsFor(seeder),
                module => Assert.Contains(module, expected)));

        // No engine, so no snippet type is runnable, and no setting has a read site
        // this deployment reaches.
        Assert.Empty(SnippetHandlerCatalog.Enabled(selection));
        Assert.Empty(AppSettingDefinition.For(selection));
    }

    // And the other half of the same claim: what it does keep is enough to be the
    // product it says it is. A "minimal deployment" that cannot sign in, read its own
    // manifest or hold a conversation is not smaller, it is broken.
    [Fact]
    public void The_minimal_deployment_can_still_do_what_it_selected()
    {
        var selection = ModuleSelection.Parse("chat,ai-studio,integrations,secrets");
        var reachable = Reachable(selection)
            .Select(descriptor => $"{descriptor.ControllerTypeInfo.Name}.{descriptor.MethodInfo.Name}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("AuthController.Login", reachable);
        Assert.Contains("ModulesController.Get", reachable);
        Assert.Contains("AiChatController.Chat", reachable);
        Assert.Contains("SecretsController.Get", reachable);
        Assert.DoesNotContain("WorkflowController.Get", reachable);

        Assert.Contains(typeof(WhoAmIHandler), ToolHandlerCatalog.Enabled(selection));
        Assert.Contains(typeof(TraceWriterHostedService), HostedServiceCatalog.Enabled(selection));
    }

    // A blocked call is not a call that fails late: it must not run the action, and it
    // must not leave the row a mutation normally leaves. The two filters are composed
    // the way MVC composes them — the resource filter outside the action filter — so
    // this is the real ordering rather than an assertion about one of them.
    [Fact]
    public async Task A_blocked_mutation_neither_executes_nor_audits()
    {
        var audit = new RecordingAudit();
        var descriptor = Descriptors.Single(d =>
            d.ControllerTypeInfo.AsType() == typeof(WorkflowController)
            && d.MethodInfo.Name == nameof(WorkflowController.Post));

        var services = new ServiceCollection();
        services.AddSingleton<IAuditLogger>(audit);
        services.AddSingleton<ICurrentUser>(new SignedInUser());

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = "POST";

        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        var resourceContext = new ResourceExecutingContext(
            actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());

        var actionExecuted = false;

        await new ModuleAvailabilityFilter(ModuleSelection.Parse("core"))
            .OnResourceExecutionAsync(resourceContext, async () =>
            {
                // Everything MVC would do after the resource filter: model binding, the
                // action filters, the action itself.
                var executing = new ActionExecutingContext(
                    actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(),
                    controller: null!);

                await new AuditMutationFilter().OnActionExecutionAsync(executing, () =>
                {
                    actionExecuted = true;
                    return Task.FromResult(new ActionExecutedContext(
                        actionContext, new List<IFilterMetadata>(), controller: null!)
                    {
                        Result = new ObjectResult(new { id = Guid.NewGuid() }) { StatusCode = 201 }
                    });
                });

                return new ResourceExecutedContext(actionContext, new List<IFilterMetadata>());
            });

        Assert.False(actionExecuted);
        Assert.Null(audit.Captured);
        var result = Assert.IsType<ObjectResult>(resourceContext.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    // The same request in a deployment that runs automation: the action executes and
    // the mutation is audited. Without this the test above would pass just as happily
    // against a filter that blocks everything.
    [Fact]
    public async Task The_same_mutation_runs_and_audits_where_the_module_is_enabled()
    {
        var audit = new RecordingAudit();
        var descriptor = Descriptors.Single(d =>
            d.ControllerTypeInfo.AsType() == typeof(WorkflowController)
            && d.MethodInfo.Name == nameof(WorkflowController.Post));

        var services = new ServiceCollection();
        services.AddSingleton<IAuditLogger>(audit);
        services.AddSingleton<ICurrentUser>(new SignedInUser());

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = "POST";

        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        var resourceContext = new ResourceExecutingContext(
            actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());

        var actionExecuted = false;

        await new ModuleAvailabilityFilter(ModuleSelection.Parse("automation"))
            .OnResourceExecutionAsync(resourceContext, async () =>
            {
                var executing = new ActionExecutingContext(
                    actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(),
                    controller: null!);

                await new AuditMutationFilter().OnActionExecutionAsync(executing, () =>
                {
                    actionExecuted = true;
                    return Task.FromResult(new ActionExecutedContext(
                        actionContext, new List<IFilterMetadata>(), controller: null!)
                    {
                        Result = new ObjectResult(new { id = Guid.NewGuid() }) { StatusCode = 201 }
                    });
                });

                return new ResourceExecutedContext(actionContext, new List<IFilterMetadata>());
            });

        Assert.True(actionExecuted);
        Assert.NotNull(audit.Captured);
        Assert.Null(resourceContext.Result);
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public (string EntityType, string Action)? Captured { get; private set; }

        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            Captured = (entityType, action);
            return Task.CompletedTask;
        }
    }

    private sealed class SignedInUser : ICurrentUser
    {
        public Guid CompanyId => Guid.NewGuid();
        public Guid UserId => Guid.NewGuid();
        public string? Username => "admin";
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => true;
    }
}
