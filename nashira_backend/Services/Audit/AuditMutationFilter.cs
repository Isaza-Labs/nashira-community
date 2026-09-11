using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Audit;

// Global MVC filter that audits every successful mutating controller action (POST / PUT /
// PATCH / DELETE) as one AuditEvent — so human CRUD is audited without touching each
// controller (§7.1-bis). Reads, failed actions, and anonymous requests are skipped, as are
// actions/controllers marked [SkipAudit] (auth, chat, dry-runs, and ops that self-audit).
// The audited artifact is the API mutation: entity type (controller), action (HTTP method),
// the route id, and the response payload as `after`.
public sealed class AuditMutationFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> Mutating =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();

        var http = context.HttpContext;
        if (!Mutating.Contains(http.Request.Method)) return;
        if (context.ActionDescriptor.EndpointMetadata.OfType<SkipAuditAttribute>().Any()) return;
        if (executed.Exception is not null && !executed.ExceptionHandled) return;

        var (status, value) = ReadResult(executed.Result);
        if (status is < 200 or >= 300) return;

        // Anonymous requests used to be skipped outright, which meant an automation
        // path could mutate state and leave no row at all — precisely the changes nobody
        // can otherwise account for. They are recorded now, but only when something can
        // say who acted: either a signed-in principal, or an ambient actor bound by the
        // background work. A request with neither would write the indistinguishable
        // `user: null` row the actor field exists to abolish, straight into a
        // hash-chained table behind a process-wide semaphore.
        var user = http.RequestServices.GetService<ICurrentUser>();
        if (user?.IsAuthenticated != true && AuditActor.Current is null) return;

        var audit = http.RequestServices.GetService<IAuditLogger>();
        if (audit is null) return;

        try
        {
            await audit.LogAsync(EntityType(context), RouteId(context), ActionName(http.Request.Method),
                before: null, after: value, http.RequestAborted);
        }
        catch
        {
            // Audit must never break the response; AuditLogger logs its own failures.
        }
    }

    // Controller names are PascalCase and inconsistently plural, so lowercasing them
    // gave entity types like "aiapispec", "mcpserver" and "users" — sitting next to the
    // hand-written "ai_api_spec", "mcp_server" and "user" in the same column, which
    // makes a filter on entity_type a guessing game.
    //
    // An explicit map rather than inflection: singularising English by rule gets
    // "policie" out of "Policies" and nobody notices until the filter returns nothing.
    private static readonly Dictionary<string, string> EntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Credential"] = "credential",
        ["Secrets"] = "secret",
        ["Users"] = "user",
        ["Permissions"] = "permission",
        ["Profiles"] = "profile",
        ["AIProvider"] = "ai_provider",
        ["Integration"] = "integration",
        ["IntegrationAction"] = "integration_action",
        ["McpServer"] = "mcp_server",
        ["AiApiSpec"] = "ai_api_spec",
        ["AiPromptSkill"] = "ai_prompt_skill",
        ["Policy"] = "policy",
        ["Device"] = "device",
        ["DevicePool"] = "device_pool",
        ["Inventory"] = "inventory_source",
        ["Workflow"] = "workflow",
        ["WorkflowTrigger"] = "workflow_trigger",
        ["WorkflowTest"] = "workflow_test",
        ["Snippet"] = "snippet",
        ["VendorCommand"] = "vendor_command",
        ["AllowedPythonModule"] = "allowed_python_module",
        ["EmailChannel"] = "email_channel",
        ["NotificationChannel"] = "notification_channel",
        ["NotificationChannel"] = "notification_channel",
        ["Git"] = "git_repository",
        ["AdminSlo"] = "slo_target",
        ["AdminSettings"] = "app_setting",
        ["Knowledge"] = "knowledge_article",
        ["Learning"] = "agent_learning",
        ["ReportsAndThemes"] = "report",
        ["AiConversations"] = "ai_conversation",
    };

    private static string EntityType(ActionExecutingContext context)
    {
        var controller = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;
        if (string.IsNullOrWhiteSpace(controller)) return "unknown";
        return EntityTypes.TryGetValue(controller, out var mapped) ? mapped : ToSnakeCase(controller);
    }

    // Fallback for a controller nobody mapped: PascalCase to snake_case, so a new one
    // lands as "foo_bar" rather than "foobar" and stays consistent with the rest.
    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string ActionName(string method) => method.ToUpperInvariant() switch
    {
        "POST" => "create",
        "PUT" or "PATCH" => "update",
        "DELETE" => "delete",
        _ => method.ToLowerInvariant(),
    };

    private static Guid? RouteId(ActionExecutingContext context) =>
        context.RouteData.Values.TryGetValue("id", out var raw) && Guid.TryParse(raw?.ToString(), out var id)
            ? id : null;

    private static (int Status, object? Value) ReadResult(IActionResult? result) => result switch
    {
        ObjectResult obj => (obj.StatusCode ?? StatusCodes.Status200OK, obj.Value),
        StatusCodeResult scr => (scr.StatusCode, null),
        _ => (StatusCodes.Status200OK, null),
    };
}
