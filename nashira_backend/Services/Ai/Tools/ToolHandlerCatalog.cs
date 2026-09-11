using System.Collections.Frozen;
using System.Collections.ObjectModel;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Ai.Tools.Handlers.Git;

namespace nashira_backend.Services.Ai.Tools;

// Which capability each agent tool belongs to, and therefore which tools a deployment
// registers at all. A tool the deployment cannot run must not reach the registry: the
// registry is what the chat loop turns into the tool list the model is shown, so an
// unfiltered one advertises work that can only fail — and the model, told a tool exists,
// will keep reaching for it.
//
// The declared module is the capability that owns the tool. ai-studio is required on top
// of it for every tool, because a tool without the agent that dispatches it is nothing;
// the list stays readable by not repeating that on 100 lines.
//
// Order is the registration order, and registration order is the order the model sees.
public static class ToolHandlerCatalog
{
    // The agent surface itself. Nothing here is reachable without it.
    private const ModuleId Agent = ModuleId.AiStudio;

    private static readonly ReadOnlyCollection<ToolHandlerRegistration> Registrations =
        Array.AsReadOnly<ToolHandlerRegistration>(
        [
            // ── core ──────────────────────────────────────────────────────────
            Own<WhoAmIHandler>(ModuleId.Core),
            // Reads a file the caller supplies; it belongs to no capability in
            // particular, and an agent that cannot read an attachment is diminished in
            // every deployment.
            Own<ParseFileHandler>(ModuleId.Core),
            Own<ListUsersHandler>(ModuleId.Core),
            Own<CreateUserHandler>(ModuleId.Core),
            Own<UpdateUserHandler>(ModuleId.Core),
            Own<DeleteUserHandler>(ModuleId.Core),

            // ── ai-studio ─────────────────────────────────────────────────────
            Own<ListAiProvidersHandler>(ModuleId.AiStudio),
            Own<CreateAiProviderHandler>(ModuleId.AiStudio),
            Own<UpdateAiProviderHandler>(ModuleId.AiStudio),
            Own<DeleteAiProviderHandler>(ModuleId.AiStudio),
            // The REST spec catalogue. A catalogued operation may belong to an
            // integration, but a spec stands on its own, so these do not require it.
            Own<ListApisHandler>(ModuleId.AiStudio),
            Own<DiscoverOperationsHandler>(ModuleId.AiStudio),
            Own<OperationDetailHandler>(ModuleId.AiStudio),
            Own<ExecuteOperationHandler>(ModuleId.AiStudio),
            Own<ListProfilesHandler>(ModuleId.AiStudio),
            Own<CreateProfileHandler>(ModuleId.AiStudio),
            Own<UpdateProfileHandler>(ModuleId.AiStudio),
            Own<DeleteProfileHandler>(ModuleId.AiStudio),
            Own<AssignProfileHandler>(ModuleId.AiStudio),
            Own<ListSkillsHandler>(ModuleId.AiStudio),
            Own<GetSkillHandler>(ModuleId.AiStudio),
            Own<LoadSkillHandler>(ModuleId.AiStudio),
            Own<CreateSkillHandler>(ModuleId.AiStudio),
            Own<UpdateSkillHandler>(ModuleId.AiStudio),
            Own<DeleteSkillHandler>(ModuleId.AiStudio),
            Own<ListSpecsHandler>(ModuleId.AiStudio),
            Own<GetSpecHandler>(ModuleId.AiStudio),
            Own<CreateSpecHandler>(ModuleId.AiStudio),
            Own<UpdateSpecHandler>(ModuleId.AiStudio),
            Own<DeleteSpecHandler>(ModuleId.AiStudio),
            Own<ListLearningsHandler>(ModuleId.AiStudio),
            Own<CreateLearningHandler>(ModuleId.AiStudio),
            Own<UpdateLearningHandler>(ModuleId.AiStudio),
            Own<DeleteLearningHandler>(ModuleId.AiStudio),
            Own<ValidateTemplateHandler>(ModuleId.AiStudio),
            Own<ListValidationsHandler>(ModuleId.AiStudio),

            // ── automation ────────────────────────────────────────────────────
            Own<ListWorkflowsHandler>(ModuleId.Automation),
            Own<GetWorkflowHandler>(ModuleId.Automation),
            Own<CreateWorkflowHandler>(ModuleId.Automation),
            Own<UpdateWorkflowHandler>(ModuleId.Automation),
            Own<DeleteWorkflowHandler>(ModuleId.Automation),
            Own<BulkDeleteWorkflowsHandler>(ModuleId.Automation),
            Own<RunWorkflowHandler>(ModuleId.Automation),
            Own<SimulateWorkflowHandler>(ModuleId.Automation),
            Own<PromoteWorkflowHandler>(ModuleId.Automation),
            Own<ListSnippetsHandler>(ModuleId.Automation),
            Own<CreateSnippetHandler>(ModuleId.Automation),
            Own<BulkDeleteSnippetsHandler>(ModuleId.Automation),

            // ── fleet ─────────────────────────────────────────────────────────
            Own<QueryDevicesHandler>(ModuleId.Fleet),
            Own<DevicePingHandler>(ModuleId.Fleet),
            Own<DeviceConnectHandler>(ModuleId.Fleet),
            Own<CreateDeviceHandler>(ModuleId.Fleet),
            Own<UpdateDeviceHandler>(ModuleId.Fleet),
            Own<DeleteDeviceHandler>(ModuleId.Fleet),
            Own<BulkDeleteDevicesHandler>(ModuleId.Fleet),
            Own<SyncNetBoxInventoryHandler>(ModuleId.Fleet),
            Own<ListInventorySourcesHandler>(ModuleId.Fleet),
            Own<CreateInventorySourceHandler>(ModuleId.Fleet),
            Own<UpdateInventorySourceHandler>(ModuleId.Fleet),
            Own<DeleteInventorySourceHandler>(ModuleId.Fleet),

            // ── integrations ──────────────────────────────────────────────────
            Own<ListIntegrationsHandler>(ModuleId.Integrations),
            Own<ListMcpToolsHandler>(ModuleId.Integrations),
            Own<McpCallHandler>(ModuleId.Integrations),

            // ── communications ────────────────────────────────────────────────
            Own<SendEmailHandler>(ModuleId.Communications),
            Own<ListEmailFoldersHandler>(ModuleId.Communications),
            Own<ListEmailsHandler>(ModuleId.Communications),
            Own<ReadEmailHandler>(ModuleId.Communications),
            Own<MarkEmailHandler>(ModuleId.Communications),
            Own<MoveEmailHandler>(ModuleId.Communications),
            Own<ArchiveEmailHandler>(ModuleId.Communications),
            Own<DeleteEmailHandler>(ModuleId.Communications),

            // ── secrets ───────────────────────────────────────────────────────
            Own<ListSecretsHandler>(ModuleId.Secrets),
            Own<SetSecretHandler>(ModuleId.Secrets),
            Own<ClearSecretHandler>(ModuleId.Secrets),
            Own<ListCredentialsHandler>(ModuleId.Secrets),
            Own<CreateCredentialHandler>(ModuleId.Secrets),
            Own<UpdateCredentialHandler>(ModuleId.Secrets),
            Own<DeleteCredentialHandler>(ModuleId.Secrets),

            // ── git ───────────────────────────────────────────────────────────
            Own<GitListRepositoriesHandler>(ModuleId.Git),
            Own<GitListFilesHandler>(ModuleId.Git),
            Own<GitReadFileHandler>(ModuleId.Git),
            Own<GitDiffHandler>(ModuleId.Git),
            Own<GitPullHandler>(ModuleId.Git),
            Own<GitStatusHandler>(ModuleId.Git),
            Own<GitListBranchesHandler>(ModuleId.Git),
            Own<GitCheckoutHandler>(ModuleId.Git),
            Own<GitWriteFileHandler>(ModuleId.Git),
            Own<GitCommitPushHandler>(ModuleId.Git),
            Own<GitHubCreateRepoHandler>(ModuleId.Git),
            Own<GitHubCreatePrHandler>(ModuleId.Git),
            Own<GitHubMergePrHandler>(ModuleId.Git),

            // ── knowledge ─────────────────────────────────────────────────────
            Own<SearchKnowledgeHandler>(ModuleId.Knowledge),
            Own<GetKnowledgeArticleHandler>(ModuleId.Knowledge),
            Own<CreateKnowledgeArticleHandler>(ModuleId.Knowledge),
            Own<UpdateKnowledgeArticleHandler>(ModuleId.Knowledge),
            Own<DeleteKnowledgeArticleHandler>(ModuleId.Knowledge),

            // ── artifacts ─────────────────────────────────────────────────────
            Own<ExportTableHandler>(ModuleId.Artifacts),
            Own<ExportDocumentHandler>(ModuleId.Artifacts),
            Own<ListReportsHandler>(ModuleId.Artifacts),
            Own<ReadReportHandler>(ModuleId.Artifacts),
            Own<SaveReportHandler>(ModuleId.Artifacts),
            Own<BulkDeleteReportsHandler>(ModuleId.Artifacts),

            // ── governance ────────────────────────────────────────────────────
            Own<ListAuditEventsHandler>(ModuleId.Governance),
            Own<GetAuditEventHandler>(ModuleId.Governance),
            Own<VerifyAuditHandler>(ModuleId.Governance),
            Own<ListPermissionDomainsHandler>(ModuleId.Governance),
            Own<GetUserPermissionsHandler>(ModuleId.Governance),
            Own<SetUserPermissionsHandler>(ModuleId.Governance),
        ]);

    // Handlers that exist but have never been wired into the agent. Listed here so the
    // exhaustiveness check keeps its meaning — an unclassified handler is a bug, and a
    // deliberately unwired one should not be indistinguishable from it. Enabling them is
    // a product decision about the agent's surface, not a deployment one.
    private static readonly FrozenSet<Type> Unwired = new[]
    {
        typeof(GitCreateWebhookHandler),
        typeof(GitListWebhooksHandler),
    }.ToFrozenSet();

    private static readonly FrozenDictionary<Type, IReadOnlyList<ModuleId>> Owners =
        Registrations.ToFrozenDictionary(
            registration => registration.Handler,
            registration => registration.Modules);

    public static IReadOnlyCollection<Type> All => Owners.Keys;

    public static IReadOnlyCollection<Type> Unregistered => Unwired;

    // Everything the tool needs, the agent surface included.
    public static IReadOnlyList<ModuleId> RequirementsFor(Type handler) =>
        [Agent, .. Owners[handler]];

    public static IReadOnlyList<Type> Enabled(ModuleSelection selection) =>
        selection.IsEnabled(Agent)
            ? [.. Registrations
                .Where(registration => selection.AreEnabled(registration.Modules))
                .Select(registration => registration.Handler)]
            : [];

    private static ToolHandlerRegistration Own<THandler>(params ModuleId[] modules)
        where THandler : class, IToolHandler =>
        new(typeof(THandler), Array.AsReadOnly(modules));
}

public sealed record ToolHandlerRegistration(Type Handler, IReadOnlyList<ModuleId> Modules);
