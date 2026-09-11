using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using nashira_backend.Configuration.Modules;
using nashira_backend.Controllers;
using nashira_backend.Services.Modules;

namespace nashira_backend.Tests;

// Endpoint ownership, checked against the real action descriptors rather than against a
// second copy of the table: the convention runs, every action comes out classified, and
// the gate and the OpenAPI document both read that classification off the endpoint.
public class EndpointModuleCatalogTests
{
    // MVC's own action-model pipeline with the convention installed. Building it here is
    // what makes the rest of this file worth anything — asserting on the catalog alone
    // would pass just as happily if the requirement never reached an endpoint.
    private static readonly IReadOnlyList<ControllerActionDescriptor> Descriptors = Build();

    private static IReadOnlyList<ControllerActionDescriptor> Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddControllers(options => options.Conventions.Add(new ModuleControllerConvention()))
            .AddApplicationPart(typeof(AuthController).Assembly);

        var descriptors = services.BuildServiceProvider()
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToList();

        Assert.NotEmpty(descriptors);
        return descriptors;
    }

    private static ControllerActionDescriptor Action<TController>(string method) =>
        Descriptors.Single(descriptor =>
            descriptor.ControllerTypeInfo.AsType() == typeof(TController)
            && descriptor.MethodInfo.Name == method);

    // What the caller would get: null when the action runs, otherwise the module the
    // 503 blames.
    private static string? BlockedBy(ControllerActionDescriptor descriptor, string? configuredModules)
    {
        var selection = ModuleSelection.Parse(configuredModules);
        var module = ModuleEndpointRequirements.FirstDisabled(descriptor.EndpointMetadata, selection);
        return module is null ? null : ModuleCatalog.Get(module.Value).Key;
    }

    // The check that has to fail when somebody adds a controller: not "did you write an
    // attribute" but "does this endpoint belong to a capability at all".
    [Fact]
    public void Every_controller_is_classified()
    {
        var missing = typeof(AuthController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsPublic: true })
            .Except(EndpointModuleCatalog.ClassifiedControllers)
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);

        // And the pipeline this file asserts against really did see all of them: a
        // partially built descriptor set would make every other test here vacuous.
        var described = Descriptors
            .Select(descriptor => descriptor.ControllerTypeInfo.AsType())
            .Distinct()
            .ToHashSet();
        Assert.Empty(EndpointModuleCatalog.ClassifiedControllers
            .Where(controller => !described.Contains(controller))
            .Select(controller => controller.Name)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_action_carries_its_capability_as_endpoint_metadata()
    {
        var unclassified = Descriptors
            .Where(descriptor => ModuleEndpointRequirements.For(descriptor.EndpointMetadata).Count == 0)
            .Select(descriptor => $"{descriptor.ControllerTypeInfo.Name}.{descriptor.MethodInfo.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unclassified);
    }

    // A deployment that enables nothing still has to be operable: sign in, see who you
    // are, read what this deployment runs, administer users, settings, themes and SLOs.
    [Theory]
    [InlineData(typeof(AuthController), nameof(AuthController.Login))]
    [InlineData(typeof(AuthController), nameof(AuthController.Refresh))]
    [InlineData(typeof(AuthController), nameof(AuthController.Me))]
    [InlineData(typeof(AuthController), nameof(AuthController.ChangePassword))]
    [InlineData(typeof(ModulesController), nameof(ModulesController.Get))]
    [InlineData(typeof(UsersController), nameof(UsersController.Get))]
    [InlineData(typeof(AdminSettingsController), nameof(AdminSettingsController.Get))]
    [InlineData(typeof(AdminSloController), nameof(AdminSloController.Get))]
    [InlineData(typeof(ThemeController), nameof(ThemeController.Get))]
    public void Core_endpoints_answer_in_a_deployment_that_enables_nothing_else(
        Type controller, string method)
    {
        var descriptor = Descriptors.Single(d =>
            d.ControllerTypeInfo.AsType() == controller && d.MethodInfo.Name == method);

        Assert.Null(BlockedBy(descriptor, "core"));
    }

    // The other half of the matrix: with only core enabled, every endpoint owned by a
    // configurable capability is blocked, and blamed on its own module.
    [Theory]
    [InlineData(typeof(AiChatController), nameof(AiChatController.Chat), "chat")]
    [InlineData(typeof(AIProviderController), nameof(AIProviderController.Get), "ai-studio")]
    [InlineData(typeof(WorkflowController), nameof(WorkflowController.Get), "automation")]
    [InlineData(typeof(DeviceController), nameof(DeviceController.Get), "fleet")]
    [InlineData(typeof(IntegrationController), nameof(IntegrationController.Get), "integrations")]
    [InlineData(typeof(SecretsController), nameof(SecretsController.Get), "secrets")]
    [InlineData(typeof(KnowledgeController), nameof(KnowledgeController.Get), "knowledge")]
    [InlineData(typeof(AuditController), nameof(AuditController.Get), "governance")]
    [InlineData(typeof(SessionsController), nameof(SessionsController.Get), "observability")]
    public void A_disabled_capability_blames_its_own_module(
        Type controller, string method, string expectedModule)
    {
        var descriptor = Descriptors.Single(d =>
            d.ControllerTypeInfo.AsType() == controller && d.MethodInfo.Name == method);

        Assert.Equal(expectedModule, BlockedBy(descriptor, "core"));
    }

    // Administering navigation visibility is governance; asking for your own is not.
    // A shell that cannot read its own menu cannot render anything at all.
    [Fact]
    public void Navigation_administration_is_governance_but_reading_your_own_menu_is_core()
    {
        Assert.Null(BlockedBy(Action<NavigationPermissionsController>(
            nameof(NavigationPermissionsController.GetMine)), "core"));
        Assert.Equal("governance", BlockedBy(Action<NavigationPermissionsController>(
            nameof(NavigationPermissionsController.GetRole)), "core"));
    }

    // Login is core; the administrative sign-in trail is part of Audit under governance.
    [Fact]
    public void Auth_signs_in_under_core_and_reports_history_under_governance()
    {
        Assert.Null(BlockedBy(Action<AuthController>(nameof(AuthController.Login)), "core"));
        Assert.Equal(
            "governance",
            BlockedBy(Action<AuthController>(nameof(AuthController.Events)), "core"));
    }

    // Enabling a capability enables its endpoints — including the ones its dependencies
    // pull in, which is what makes "chat" a usable answer instead of a broken one.
    [Fact]
    public void An_enabled_capability_and_its_dependencies_answer()
    {
        Assert.Null(BlockedBy(
            Action<AiChatController>(nameof(AiChatController.Chat)),
            "chat,ai-studio,integrations"));
        Assert.Null(BlockedBy(
            Action<AIProviderController>(nameof(AIProviderController.Get)),
            "chat,ai-studio,integrations"));
        Assert.Equal(
            "automation",
            BlockedBy(
                Action<WorkflowController>(nameof(WorkflowController.Get)),
                "chat,ai-studio,integrations"));
    }

    [Fact]
    public void Nothing_is_blocked_when_the_variable_is_absent()
    {
        Assert.All(Descriptors, descriptor => Assert.Null(BlockedBy(descriptor, null)));
    }

    // OpenAPI reads the same metadata as the gate, so the document cannot drift from
    // what the deployment will actually answer.
    [Fact]
    public void Openapi_publishes_the_operations_that_can_be_called_and_no_others()
    {
        var coreOnly = ModuleSelection.Parse("core");
        var login = new ApiDescription { ActionDescriptor = Action<AuthController>(nameof(AuthController.Login)) };
        var workflows = new ApiDescription { ActionDescriptor = Action<WorkflowController>(nameof(WorkflowController.Get)) };

        Assert.True(EnabledModulesOpenApi.IsAvailable(login, coreOnly));
        Assert.False(EnabledModulesOpenApi.IsAvailable(workflows, coreOnly));
        Assert.True(EnabledModulesOpenApi.IsAvailable(workflows, ModuleSelection.Parse(null)));
    }

    // Through the registration rather than the predicate: the filter has to reach the
    // options the document generator reads, and it has to compose with the built-in
    // predicate instead of replacing it.
    [Fact]
    public void The_openapi_registration_installs_the_filter_without_losing_the_default()
    {
        var options = new ServiceCollection()
            .AddModuleAwareOpenApi(ModuleSelection.Parse("core"))
            .BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<OpenApiOptions>>()
            .Get("v1");

        var login = new ApiDescription { ActionDescriptor = Action<AuthController>(nameof(AuthController.Login)) };
        var workflows = new ApiDescription { ActionDescriptor = Action<WorkflowController>(nameof(WorkflowController.Get)) };
        var otherDocument = new ApiDescription
        {
            ActionDescriptor = Action<AuthController>(nameof(AuthController.Login)),
            GroupName = "internal",
        };

        Assert.True(options.ShouldInclude(login));
        Assert.False(options.ShouldInclude(workflows));
        Assert.False(options.ShouldInclude(otherDocument));
    }

    // End to end through the filter itself: the metadata the convention produced is what
    // the gate reads, and a blocked action is never entered.
    [Fact]
    public async Task The_gate_blocks_a_real_disabled_action_without_running_it()
    {
        var descriptor = Action<WorkflowController>(nameof(WorkflowController.Get));
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        var context = new ResourceExecutingContext(
            actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());

        var executed = false;
        await new ModuleAvailabilityFilter(ModuleSelection.Parse("core"))
            .OnResourceExecutionAsync(context, () =>
            {
                executed = true;
                return Task.FromResult(new ResourceExecutedContext(actionContext, new List<IFilterMetadata>()));
            });

        Assert.False(executed);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }
}
