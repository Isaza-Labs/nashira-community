using System.Collections.Frozen;
using nashira_backend.Controllers;

namespace nashira_backend.Configuration.Modules;

// Which capability owns each HTTP action. One table rather than an attribute on every
// controller: exhaustiveness is the property that matters here, and it can only be read
// off a single list. ModuleControllerConvention refuses to start a process with an
// action missing from it, so a new controller is classified or nothing boots.
//
// Types and nameof, never strings: renaming a controller or an action breaks the build
// instead of silently dropping an endpoint out of its module. Ownership is also never
// derived from the URL prefix or the namespace — /api/ai holds chat and ai-studio
// surfaces alike, and the difference is a deployment decision, not a spelling.
public static class EndpointModuleCatalog
{
    // Whole-controller ownership. A controller whose actions do not all belong to the
    // same capability is listed here with its dominant module and corrected below.
    private static readonly FrozenDictionary<Type, IReadOnlyList<ModuleId>> ByController =
        new Dictionary<Type, IReadOnlyList<ModuleId>>
        {
            // ── core ──────────────────────────────────────────────────────────
            // Identity, the deployment's own manifest, and the always-present admin
            // surfaces. Every one of these has to answer in a core-only deployment:
            // without them there is no way in and no way to configure what is there.
            [typeof(AuthController)] = [ModuleId.Core],
            [typeof(ModulesController)] = [ModuleId.Core],
            [typeof(UsersController)] = [ModuleId.Core],
            [typeof(AdminSettingsController)] = [ModuleId.Core],
            [typeof(AdminSloController)] = [ModuleId.Core],
            [typeof(ThemeController)] = [ModuleId.Core],
            // Navigation visibility is administered under governance; the caller's own
            // effective navigation is core (see the per-action table below).
            [typeof(NavigationPermissionsController)] = [ModuleId.Governance],

            // ── chat ──────────────────────────────────────────────────────────
            [typeof(AiChatController)] = [ModuleId.Chat],
            [typeof(AiConversationsController)] = [ModuleId.Chat],

            // ── ai-studio ─────────────────────────────────────────────────────
            // Everything that configures the agent rather than talking to it.
            [typeof(AIProviderController)] = [ModuleId.AiStudio],
            [typeof(AiModelsController)] = [ModuleId.AiStudio],
            [typeof(AiApiSpecController)] = [ModuleId.AiStudio],
            [typeof(AiPromptSkillController)] = [ModuleId.AiStudio],
            [typeof(ProfilesController)] = [ModuleId.AiStudio],
            [typeof(LearningController)] = [ModuleId.AiStudio],
            [typeof(LoaderController)] = [ModuleId.AiStudio],

            // ── automation ────────────────────────────────────────────────────
            [typeof(WorkflowController)] = [ModuleId.Automation],
            [typeof(WorkflowTestController)] = [ModuleId.Automation],
            [typeof(WorkflowTriggerController)] = [ModuleId.Automation],
            [typeof(WorkflowWebhookController)] = [ModuleId.Automation],
            [typeof(SnippetController)] = [ModuleId.Automation],
            [typeof(RunsController)] = [ModuleId.Automation],
            [typeof(AllowedPythonModuleController)] = [ModuleId.Automation],

            // ── fleet ─────────────────────────────────────────────────────────
            [typeof(DeviceController)] = [ModuleId.Fleet],
            [typeof(DevicePoolController)] = [ModuleId.Fleet],
            [typeof(InventoryController)] = [ModuleId.Fleet],
            [typeof(VendorCommandController)] = [ModuleId.Fleet],

            // ── integrations ──────────────────────────────────────────────────
            [typeof(IntegrationController)] = [ModuleId.Integrations],
            [typeof(IntegrationActionController)] = [ModuleId.Integrations],
            [typeof(McpServerController)] = [ModuleId.Integrations],
            [typeof(McpOAuthCallbackController)] = [ModuleId.Integrations],

            // ── communications ────────────────────────────────────────────────
            [typeof(EmailChannelController)] = [ModuleId.Communications],
            [typeof(NotificationChannelController)] = [ModuleId.Communications],
            [typeof(MessagingChannelController)] = [ModuleId.Communications],
            [typeof(MessagingLinkController)] = [ModuleId.Communications],
            [typeof(MessagingWebhookController)] = [ModuleId.Communications],

            // ── secrets ───────────────────────────────────────────────────────
            [typeof(SecretsController)] = [ModuleId.Secrets],
            [typeof(CredentialController)] = [ModuleId.Secrets],

            // ── git ───────────────────────────────────────────────────────────
            [typeof(GitController)] = [ModuleId.Git],
            [typeof(GitWebhookController)] = [ModuleId.Git],
            [typeof(GitWebhookIngestController)] = [ModuleId.Git],

            // ── knowledge ─────────────────────────────────────────────────────
            [typeof(KnowledgeController)] = [ModuleId.Knowledge],

            // ── artifacts ─────────────────────────────────────────────────────
            [typeof(ReportsController)] = [ModuleId.Artifacts],
            [typeof(ExportController)] = [ModuleId.Artifacts],

            // ── governance ────────────────────────────────────────────────────
            [typeof(PolicyController)] = [ModuleId.Governance],
            [typeof(PermissionsController)] = [ModuleId.Governance],
            [typeof(AuditController)] = [ModuleId.Governance],

            // ── observability ─────────────────────────────────────────────────
            [typeof(AdminMetricsController)] = [ModuleId.Observability],
            [typeof(TraceEventsController)] = [ModuleId.Observability],
            [typeof(SessionsController)] = [ModuleId.Observability],
        }.ToFrozenDictionary();

    // Actions that do not belong to their controller's capability. Each one is a
    // deliberate exception with a reason, not a convenience.
    private static readonly FrozenDictionary<(Type Controller, string Action), IReadOnlyList<ModuleId>>
        ByAction = new Dictionary<(Type, string), IReadOnlyList<ModuleId>>
        {
            // The signed-in user's own effective navigation. The shell asks for this on
            // every load to know what to render, so blocking it would leave a deployment
            // without governance unable to draw its own menu.
            [(typeof(NavigationPermissionsController), nameof(NavigationPermissionsController.GetMine))] =
                [ModuleId.Core],

            // Audit owns the administrative sign-in trail together with mutation history.
            // Core keeps writing the events either way; governance controls reading them.
            [(typeof(AuthController), nameof(AuthController.Events))] =
                [ModuleId.Governance],
        }.ToFrozenDictionary();

    // Null when nothing classifies the action. That is a bug rather than a default, so
    // callers decide how loudly to say so.
    public static IReadOnlyList<ModuleId>? For(Type controller, string action) =>
        ByAction.TryGetValue((controller, action), out var overridden)
            ? overridden
            : ByController.TryGetValue(controller, out var owned)
                ? owned
                : null;

    public static IReadOnlyCollection<Type> ClassifiedControllers => ByController.Keys;
}
