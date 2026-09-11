using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;

namespace nashira_backend.Tests;

// The global mutation-audit filter: audits successful POST/PUT/PATCH/DELETE, skips reads,
// failures, anonymous requests, and [SkipAudit] targets.
public class AuditMutationFilterTests
{
    private sealed class CapturingAudit : IAuditLogger
    {
        public (string EntityType, Guid? EntityId, string Action)? Captured { get; private set; }

        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            Captured = (entityType, entityId, action);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTenant(bool authed) : ICurrentUser
    {
        public Guid CompanyId => Guid.NewGuid();
        public Guid UserId => Guid.NewGuid();
        public string? Username => "u";
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => authed;
    }

    private static async Task<CapturingAudit> Run(
        string method, IActionResult result, bool authed = true, Guid? routeId = null,
        object[]? metadata = null, string controllerName = "Device")
    {
        var audit = new CapturingAudit();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLogger>(audit);
        services.AddSingleton<ICurrentUser>(new FakeTenant(authed));

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = method;

        var descriptor = new ControllerActionDescriptor { ControllerName = controllerName, EndpointMetadata = metadata ?? [] };
        var routeData = new RouteData();
        if (routeId is { } id) routeData.Values["id"] = id.ToString();

        var actionContext = new ActionContext(http, routeData, descriptor);
        var executing = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: null!);

        await new AuditMutationFilter().OnActionExecutionAsync(executing,
            () => Task.FromResult(new ActionExecutedContext(actionContext, [], controller: null!) { Result = result }));

        return audit;
    }

    [Fact]
    public async Task Audits_successful_post_as_create()
    {
        var audit = await Run("POST", new ObjectResult(new { id = Guid.NewGuid() }) { StatusCode = 201 });
        Assert.NotNull(audit.Captured);
        Assert.Equal("device", audit.Captured!.Value.EntityType);
        Assert.Equal("create", audit.Captured.Value.Action);
    }

    [Fact]
    public async Task Delete_with_route_id_is_captured()
    {
        var id = Guid.NewGuid();
        var audit = await Run("DELETE", new OkObjectResult(new { }), routeId: id);
        Assert.NotNull(audit.Captured);
        Assert.Equal("delete", audit.Captured!.Value.Action);
        Assert.Equal(id, audit.Captured.Value.EntityId);
    }

    [Fact]
    public async Task Skips_reads() =>
        Assert.Null((await Run("GET", new OkObjectResult(new { }))).Captured);

    [Fact]
    public async Task Skips_failed_status() =>
        Assert.Null((await Run("DELETE", new StatusCodeResult(404))).Captured);

    // Anonymous requests used to be dropped outright, which meant automation could
    // mutate state and leave nothing behind. They are recorded now — but only when
    // something can say who acted. A background path binds an ambient actor, and that
    // is what makes the row worth writing.
    [Fact]
    public async Task Records_an_anonymous_mutation_that_has_an_actor()
    {
        using var _ = AuditActor.Use(AuditActor.WorkflowRunner);

        var audit = await Run("POST", new ObjectResult(new { }) { StatusCode = 201 }, authed: false);

        Assert.NotNull(audit.Captured);
        Assert.Equal("create", audit.Captured!.Value.Action);
    }

    // With neither a signed-in user nor an ambient actor there is nobody to attribute
    // the change to. Writing it would produce the indistinguishable `user: null` row the
    // actor field exists to abolish — straight into a hash-chained table, behind a
    // process-wide semaphore, from an unauthenticated caller.
    [Fact]
    public async Task Skips_a_mutation_nobody_can_be_attributed_for() =>
        Assert.Null((await Run("POST", new ObjectResult(new { }) { StatusCode = 201 }, authed: false)).Captured);

    // Controller names are PascalCase and inconsistently plural. Left raw they produced
    // "aiapispec" / "users" beside the hand-written "ai_api_spec" / "user" in the same
    // column, which makes filtering by entity_type unreliable.
    [Theory]
    [InlineData("Users", "user")]
    [InlineData("Secrets", "secret")]
    [InlineData("AiApiSpec", "ai_api_spec")]
    [InlineData("McpServer", "mcp_server")]
    [InlineData("AIProvider", "ai_provider")]
    [InlineData("Policy", "policy")]
    public async Task Entity_type_is_normalised(string controller, string expected)
    {
        var audit = await Run("POST", new ObjectResult(new { }) { StatusCode = 201 }, controllerName: controller);
        Assert.Equal(expected, audit.Captured!.Value.EntityType);
    }

    // An unmapped controller still lands in the same shape rather than as one run-on
    // word, so a new one does not need this table edited to be filterable.
    [Fact]
    public async Task An_unmapped_controller_falls_back_to_snake_case()
    {
        var audit = await Run("POST", new ObjectResult(new { }) { StatusCode = 201 }, controllerName: "SomeNewThing");
        Assert.Equal("some_new_thing", audit.Captured!.Value.EntityType);
    }

    [Fact]
    public async Task Skips_when_marked_skipaudit() =>
        Assert.Null((await Run("POST", new ObjectResult(new { }) { StatusCode = 201 }, metadata: [new SkipAuditAttribute()])).Captured);
}
