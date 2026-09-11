using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Ai.Tools.Handlers.Git;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;

namespace nashira_backend.Tests;

// Agent tools and snippet handlers against the deployment's capabilities: everything
// that exists is classified, and only what the deployment can actually run is registered.
public class ModuleComponentCatalogTests
{
    private static IReadOnlyList<Type> Implementations<TComponent>() =>
        [.. typeof(ToolHandlerCatalog).Assembly.GetTypes()
            .Where(type => typeof(TComponent).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false })];

    private static IReadOnlyList<string> Names(IEnumerable<Type> types) =>
        [.. types.Select(type => type.Name).Order(StringComparer.Ordinal)];

    // The check that has to fail when somebody writes a handler: it is classified, or it
    // is listed as deliberately unwired. Nothing is allowed to be neither.
    [Fact]
    public void Every_tool_handler_is_classified()
    {
        var unclassified = Implementations<IToolHandler>()
            .Except(ToolHandlerCatalog.All)
            .Except(ToolHandlerCatalog.Unregistered)
            .ToList();

        Assert.Empty(Names(unclassified));
    }

    [Fact]
    public void Every_snippet_handler_is_classified()
    {
        var unclassified = Implementations<ISnippetHandler>()
            .Except(SnippetHandlerCatalog.All)
            .ToList();

        Assert.Empty(Names(unclassified));
    }

    // Handlers written but never wired into the agent stay out of the registry. They are
    // named in the catalog so that "nobody classified this" and "this is not part of the
    // agent's surface" remain different statements.
    [Fact]
    public void Handlers_that_were_never_wired_stay_unregistered()
    {
        Assert.Equal(
            ["GitCreateWebhookHandler", "GitListWebhooksHandler"],
            Names(ToolHandlerCatalog.Unregistered));
        Assert.Empty(ToolHandlerCatalog.Unregistered.Intersect(ToolHandlerCatalog.All));
        Assert.Empty(ToolHandlerCatalog.Enabled(ModuleSelection.Parse(null))
            .Intersect(ToolHandlerCatalog.Unregistered));
    }

    // All-enabled is the compatibility case: a deployment that configures nothing keeps
    // exactly the surface it had before any of this existed.
    [Fact]
    public void An_absent_variable_registers_every_classified_handler()
    {
        var selection = ModuleSelection.Parse(null);

        Assert.Equal(Names(ToolHandlerCatalog.All), Names(ToolHandlerCatalog.Enabled(selection)));
        Assert.Equal(Names(SnippetHandlerCatalog.All), Names(SnippetHandlerCatalog.Enabled(selection)));
    }

    // The reference deployment from the specification. What the agent may be told about
    // is chat's own surface, ai-studio's, integrations', secrets', and what core owns.
    [Fact]
    public void Chat_ai_studio_integrations_and_secrets_register_only_those_capabilities()
    {
        var selection = ModuleSelection.Parse("chat,ai-studio,integrations,secrets");
        var enabled = ToolHandlerCatalog.Enabled(selection).ToHashSet();

        Assert.Contains(typeof(WhoAmIHandler), enabled);
        Assert.Contains(typeof(ListSkillsHandler), enabled);
        Assert.Contains(typeof(ExecuteOperationHandler), enabled);
        Assert.Contains(typeof(ListSecretsHandler), enabled);
        Assert.Contains(typeof(ListIntegrationsHandler), enabled);

        Assert.DoesNotContain(typeof(QueryDevicesHandler), enabled);
        Assert.DoesNotContain(typeof(GitDiffHandler), enabled);
        Assert.DoesNotContain(typeof(SendEmailHandler), enabled);
        Assert.DoesNotContain(typeof(ListWorkflowsHandler), enabled);
        Assert.DoesNotContain(typeof(SearchKnowledgeHandler), enabled);
        Assert.DoesNotContain(typeof(ListAuditEventsHandler), enabled);
        Assert.DoesNotContain(typeof(SaveReportHandler), enabled);

        // Every registered tool is one this deployment can actually run.
        Assert.All(
            ToolHandlerCatalog.Enabled(selection),
            handler => Assert.True(selection.AreEnabled(ToolHandlerCatalog.RequirementsFor(handler))));
    }

    // Without automation there is no engine, so no snippet type is runnable — including
    // the ones whose other capability is enabled.
    [Fact]
    public void Snippets_need_the_engine_before_anything_else()
    {
        Assert.Empty(SnippetHandlerCatalog.Enabled(
            ModuleSelection.Parse("chat,ai-studio,integrations,secrets")));
        Assert.Empty(SnippetHandlerCatalog.Enabled(ModuleSelection.Parse("fleet,git")));
    }

    // Without the agent there is nothing to announce a tool to, and the registry is
    // never read. Registering a hundred scoped handlers to populate it would be work
    // done for nobody.
    [Fact]
    public void Tools_need_the_agent_before_anything_else()
    {
        Assert.Empty(ToolHandlerCatalog.Enabled(ModuleSelection.Parse("automation,fleet,secrets")));
    }

    // The intersections the specification names, each one missing one half.
    [Theory]
    [InlineData("automation", typeof(SshSnippetHandler), false)]
    [InlineData("automation,fleet", typeof(SshSnippetHandler), true)]
    [InlineData("automation", typeof(McpCallSnippetHandler), false)]
    [InlineData("automation,integrations", typeof(McpCallSnippetHandler), true)]
    [InlineData("automation", typeof(GitSnippetHandler), false)]
    [InlineData("automation,git", typeof(GitSnippetHandler), true)]
    [InlineData("automation", typeof(ReportSnippetHandler), false)]
    [InlineData("automation,artifacts", typeof(ReportSnippetHandler), true)]
    [InlineData("automation", typeof(EmailSendSnippetHandler), false)]
    [InlineData(
        "automation,communications,chat,ai-studio,integrations",
        typeof(EmailSendSnippetHandler),
        true)]
    public void A_combined_snippet_needs_both_of_its_capabilities(
        string configuredModules, Type handler, bool expected)
    {
        var enabled = SnippetHandlerCatalog.Enabled(ModuleSelection.Parse(configuredModules));

        Assert.Equal(expected, enabled.Contains(handler));
    }

    // An automation-only deployment is still a usable one: the engine's own vocabulary
    // stays, and only the capability-bound types drop out.
    [Fact]
    public void An_automation_only_deployment_keeps_the_engines_own_vocabulary()
    {
        var enabled = SnippetHandlerCatalog.Enabled(ModuleSelection.Parse("automation")).ToHashSet();

        Assert.Contains(typeof(TransformSnippetHandler), enabled);
        Assert.Contains(typeof(RestCallSnippetHandler), enabled);
        Assert.Contains(typeof(PythonSnippetHandler), enabled);
        Assert.Contains(typeof(PingSnippetHandler), enabled);
        Assert.DoesNotContain(typeof(SshSnippetHandler), enabled);
        Assert.DoesNotContain(typeof(SlackMessageSnippetHandler), enabled);
    }

    // Registration order is the order the model is shown its tools, so filtering must
    // not reshuffle it.
    [Fact]
    public void Filtering_preserves_registration_order()
    {
        var all = ToolHandlerCatalog.Enabled(ModuleSelection.Parse(null));
        var some = ToolHandlerCatalog.Enabled(
            ModuleSelection.Parse("chat,ai-studio,integrations,secrets"));

        Assert.Equal([.. all.Where(some.Contains)], some);
    }

    // Every capability that owns tools or snippets can be turned off on its own: if a
    // module were only ever reachable together with another, the catalog would be
    // describing a dependency it never declared.
    [Fact]
    public void Every_classified_capability_can_be_switched_off_by_itself()
    {
        var everything = ModuleSelection.Parse(null);

        foreach (var definition in ModuleCatalog.All.Where(module => module.Configurable))
        {
            var others = string.Join(
                ",",
                ModuleCatalog.All
                    .Where(module => module.Configurable && module.Id != definition.Id)
                    .Select(module => module.Key));

            // Dependencies make some combinations unbuildable (dropping ai-studio drops
            // chat with it); those are the parser's business, not this one's.
            ModuleSelection without;
            try
            {
                without = ModuleSelection.Parse(others);
            }
            catch (ModuleConfigurationException)
            {
                continue;
            }

            var droppedTools = ToolHandlerCatalog.Enabled(everything)
                .Except(ToolHandlerCatalog.Enabled(without));
            Assert.All(
                droppedTools,
                handler => Assert.Contains(definition.Id, ToolHandlerCatalog.RequirementsFor(handler)));
        }
    }
}
