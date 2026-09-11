using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Modules;

namespace nashira_backend.Tests;

// The central gate: endpoints owned by a disabled module answer 503 with the agreed
// ProblemDetails and the action is never reached; everything else passes through.
public class ModuleAvailabilityFilterTests
{
    private sealed record Run(bool ActionExecuted, IActionResult? Result);

    private static async Task<Run> Execute(string? configuredModules, params object[] metadata)
    {
        var selection = ModuleSelection.Parse(configuredModules);
        var descriptor = new ControllerActionDescriptor { EndpointMetadata = metadata };
        var actionContext = new ActionContext(
            new DefaultHttpContext(), new RouteData(), descriptor);
        var context = new ResourceExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());

        var executed = false;
        await new ModuleAvailabilityFilter(selection).OnResourceExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ResourceExecutedContext(actionContext, new List<IFilterMetadata>()));
        });

        return new Run(executed, context.Result);
    }

    private static JsonElement Body(IActionResult? result)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        return JsonSerializer.SerializeToElement(
            obj.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task An_enabled_module_reaches_the_action()
    {
        var run = await Execute(
            "chat,ai-studio,integrations",
            new RequiresModuleAttribute(ModuleId.Chat));

        Assert.True(run.ActionExecuted);
        Assert.Null(run.Result);
    }

    // The gate exists to stop the action, not to relabel its response: a blocked request
    // must not bind a body, touch module data or produce a mutation audit.
    [Fact]
    public async Task A_disabled_module_never_reaches_the_action()
    {
        var run = await Execute(
            "chat,ai-studio,integrations",
            new RequiresModuleAttribute(ModuleId.Automation));

        Assert.False(run.ActionExecuted);
        var obj = Assert.IsType<ObjectResult>(run.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, obj.StatusCode);
        Assert.Contains("application/problem+json", obj.ContentTypes);
    }

    [Fact]
    public async Task The_problem_body_matches_the_agreed_contract()
    {
        var run = await Execute(
            "chat,ai-studio,integrations",
            new RequiresModuleAttribute(ModuleId.Automation));

        var body = Body(run.Result);
        Assert.Equal("https://httpstatuses.io/503", body.GetProperty("type").GetString());
        Assert.Equal("module_disabled", body.GetProperty("title").GetString());
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Equal(
            "module 'automation' is disabled for this deployment",
            body.GetProperty("detail").GetString());
        Assert.Equal(
            body.GetProperty("detail").GetString(),
            body.GetProperty("error").GetString());
        Assert.Equal("module_disabled", body.GetProperty("code").GetString());
        Assert.Equal("automation", body.GetProperty("module").GetString());
    }

    // Enabling a module is an explicit recreation of the deployment, so there is no
    // delay to advertise and nothing for a client to wait out.
    [Fact]
    public async Task The_response_carries_no_retry_hint()
    {
        var run = await Execute(
            "chat,ai-studio,integrations",
            new RequiresModuleAttribute(ModuleId.Automation));

        var body = Body(run.Result);
        Assert.False(body.TryGetProperty("retryAfter", out _));
        Assert.False(body.TryGetProperty("retry_after", out _));
    }

    // Controller-level and action-level attributes both land in EndpointMetadata, and an
    // endpoint is available only when every one of them is enabled.
    [Fact]
    public async Task Requirements_from_controller_and_action_are_combined()
    {
        var run = await Execute(
            "chat,ai-studio,integrations",
            new RequiresModuleAttribute(ModuleId.Chat),
            new RequiresModuleAttribute(ModuleId.Communications));

        Assert.False(run.ActionExecuted);
        Assert.Equal("communications", Body(run.Result).GetProperty("module").GetString());
    }

    // Catalog order, not attribute order: the same request must not report a different
    // module between two deployments that only differ in how the attributes were written.
    [Fact]
    public async Task Several_disabled_modules_report_the_first_in_catalog_order()
    {
        var run = await Execute(
            "core",
            new RequiresModuleAttribute(ModuleId.Observability, ModuleId.Chat));

        Assert.Equal("chat", Body(run.Result).GetProperty("module").GetString());
    }

    [Fact]
    public async Task Core_endpoints_answer_in_a_minimal_deployment()
    {
        var run = await Execute("core", new RequiresModuleAttribute(ModuleId.Core));

        Assert.True(run.ActionExecuted);
    }

    [Fact]
    public async Task Every_module_is_reachable_when_the_variable_is_absent()
    {
        foreach (var definition in ModuleCatalog.All)
        {
            var run = await Execute(null, new RequiresModuleAttribute(definition.Id));
            Assert.True(run.ActionExecuted);
        }
    }

    // The stage is the guarantee, so it is worth pinning: resource filters run after
    // authorization and before model binding. Demoted to an action filter the gate would
    // bind the body first — turning an unparseable payload into 400 instead of 503 — and
    // would sit alongside the audit filter rather than in front of it.
    [Fact]
    public void The_gate_runs_as_a_resource_filter()
    {
        Assert.True(typeof(IAsyncResourceFilter).IsAssignableFrom(typeof(ModuleAvailabilityFilter)));
        Assert.False(typeof(IActionFilter).IsAssignableFrom(typeof(ModuleAvailabilityFilter)));
        Assert.False(typeof(IAsyncActionFilter).IsAssignableFrom(typeof(ModuleAvailabilityFilter)));
    }

    // Until every controller is classified an unannotated endpoint keeps answering.
    // The exhaustive-classification test is what closes this gap, not a silent 503.
    [Fact]
    public async Task An_endpoint_without_requirements_passes_through()
    {
        var run = await Execute("core");

        Assert.True(run.ActionExecuted);
        Assert.Null(run.Result);
    }
}
