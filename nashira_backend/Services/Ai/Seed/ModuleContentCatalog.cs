using System.Collections.Frozen;
using nashira_backend.Configuration.Modules;

namespace nashira_backend.Services.Ai.Seed;

// Which capabilities the shipped AI content describes: the Skills/*.md that make up the
// system prompt, and the Specs/*.yaml that become the agent's REST catalogue.
//
// Content is filtered, never deleted. A document of a disabled capability is simply not
// offered — to the prompt, to the catalogue, or to the seeder — and rows an earlier
// all-enabled deployment already wrote stay exactly where they are, visible again the
// moment the capability comes back.
//
// Skills are atomic prompt documents, so every capability they describe must be enabled.
// Mixed specs keep their stable `api` id and stored row, but their operations are filtered
// individually. This matters more than the HTTP gate: a disabled operation must never be
// advertised to the model in the first place.
public static class ModuleContentCatalog
{
    private static readonly IReadOnlyList<ModuleId> AllModules =
        [.. ModuleCatalog.All.Select(definition => definition.Id)];

    // Skills/*.md, by the name SkillPromptLoader uses: the path relative to Skills/,
    // with forward slashes.
    private static readonly FrozenDictionary<string, IReadOnlyList<ModuleId>> Skills =
        new Dictionary<string, IReadOnlyList<ModuleId>>(StringComparer.OrdinalIgnoreCase)
        {
            // Base supplies the agent identity whenever AI Studio can construct a prompt. The
            // platform-wide map below is richer but only truthful with every capability.
            ["base.md"] = [ModuleId.Core, ModuleId.AiStudio],
            // The platform-wide object map names every capability. Partial deployments use
            // their focused skills instead; exposing this map would advertise disabled tools.
            ["nashira.md"] = AllModules,
            // This diagnostic map crosses several capability boundaries and is atomic, so all
            // of those capabilities must be available before it enters the prompt.
            ["troubleshooting.md"] = [
                ModuleId.Core, ModuleId.Fleet, ModuleId.Integrations,
                ModuleId.Automation, ModuleId.AiStudio, ModuleId.Governance, ModuleId.Secrets],

            ["administration.md"] = [ModuleId.AiStudio],
            ["knowledge.md"] = [ModuleId.Knowledge, ModuleId.AiStudio],
            ["integrations.md"] = [ModuleId.Integrations, ModuleId.AiStudio],
            ["mcp.md"] = [ModuleId.Integrations],
            ["inventory.md"] = [ModuleId.Fleet],
            // The full Git guide also covers workflow nodes and credential discovery.
            // A Git-only deployment gets the focused direct-operations guide instead.
            ["git.md"] = [ModuleId.Git, ModuleId.Automation, ModuleId.Secrets],
            ["git-direct.md"] = [ModuleId.Git],
            ["governance.md"] = [ModuleId.Governance],
            ["email.md"] = [ModuleId.Communications],
            ["secrets.md"] = [ModuleId.Secrets],
            ["exports.md"] = [ModuleId.Artifacts],
            ["workflows.md"] = [ModuleId.Automation],
            // The full guide documents handlers contributed by other capabilities.
            // Keep a focused engine guide for deployments that only run Automation.
            ["snippets.md"] = [
                ModuleId.Automation, ModuleId.Fleet, ModuleId.Integrations,
                ModuleId.Git, ModuleId.Artifacts, ModuleId.Communications, ModuleId.Secrets],
            ["snippets-automation.md"] = [ModuleId.Automation],
            ["runs.md"] = [ModuleId.Automation],
            // How to author a workflow's logic_diagram_mermaid field.
            ["mermaid.md"] = [ModuleId.Automation],
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Focused guides are fallbacks for a rich guide, not extra prompt weight. When the
    // deployment can truthfully load the rich document, the focused one is superseded.
    private static readonly FrozenDictionary<string, string> FocusedSkillReplacements =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["git-direct.md"] = "git.md",
            ["snippets-automation.md"] = "snippets.md",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Operation ownership for built-in specs that intentionally combine capabilities.
    // Missing operations in one of these specs fail closed; the exhaustive test makes a
    // newly shipped operation require a conscious classification.
    private static readonly FrozenDictionary<(string Api, string Operation), ModuleId>
        OperationModules = new Dictionary<(string, string), ModuleId>
        {
            [("na_ai_meta", "aimeta_listSkills")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_createSkill")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_listBuiltinSkills")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_getSkill")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_updateSkill")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_deleteSkill")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_listSpecs")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_createSpec")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_getSpec")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_deleteSpec")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_listProviders")] = ModuleId.AiStudio,
            [("na_ai_meta", "aimeta_listConversations")] = ModuleId.Chat,
            [("na_ai_meta", "aimeta_getConversation")] = ModuleId.Chat,
            [("na_ai_meta", "aimeta_deleteConversation")] = ModuleId.Chat,

            [("na_inventory", "inventory_listDevices")] = ModuleId.Fleet,
            [("na_inventory", "inventory_createDevice")] = ModuleId.Fleet,
            [("na_inventory", "inventory_getDevice")] = ModuleId.Fleet,
            [("na_inventory", "inventory_updateDevice")] = ModuleId.Fleet,
            [("na_inventory", "inventory_deleteDevice")] = ModuleId.Fleet,
            [("na_inventory", "inventory_listCredentials")] = ModuleId.Secrets,
            [("na_inventory", "inventory_getCredential")] = ModuleId.Secrets,
            [("na_inventory", "inventory_listSources")] = ModuleId.Fleet,
            [("na_inventory", "inventory_createSource")] = ModuleId.Fleet,
            [("na_inventory", "inventory_syncSource")] = ModuleId.Fleet,
            [("na_inventory", "inventory_listDevicePools")] = ModuleId.Fleet,
            [("na_inventory", "inventory_createDevicePool")] = ModuleId.Fleet,
            [("na_inventory", "inventory_getDevicePool")] = ModuleId.Fleet,
            [("na_inventory", "inventory_updateDevicePool")] = ModuleId.Fleet,
            [("na_inventory", "inventory_deleteDevicePool")] = ModuleId.Fleet,
            [("na_inventory", "inventory_devicePoolMembers")] = ModuleId.Fleet,

            [("na_snippets", "snippets_listTypes")] = ModuleId.Automation,
            [("na_snippets", "snippets_list")] = ModuleId.Automation,
            [("na_snippets", "snippets_create")] = ModuleId.Automation,
            [("na_snippets", "snippets_get")] = ModuleId.Automation,
            [("na_snippets", "snippets_update")] = ModuleId.Automation,
            [("na_snippets", "snippets_delete")] = ModuleId.Automation,
            [("na_snippets", "snippets_listVendorCommands")] = ModuleId.Fleet,
            [("na_snippets", "snippets_createVendorCommand")] = ModuleId.Fleet,
            [("na_snippets", "snippets_resolveVendorCommand")] = ModuleId.Fleet,
            [("na_snippets", "snippets_updateVendorCommand")] = ModuleId.Fleet,
            [("na_snippets", "snippets_deleteVendorCommand")] = ModuleId.Fleet,
            [("na_snippets", "snippets_listPythonModules")] = ModuleId.Automation,
            [("na_snippets", "snippets_addPythonModule")] = ModuleId.Automation,
            [("na_snippets", "snippets_updatePythonModule")] = ModuleId.Automation,
            [("na_snippets", "snippets_removePythonModule")] = ModuleId.Automation,
            [("na_snippets", "snippets_retryPythonModule")] = ModuleId.Automation,

            [("na_governance", "governance_listUsers")] = ModuleId.Core,
            [("na_governance", "governance_createUser")] = ModuleId.Core,
            [("na_governance", "governance_getUser")] = ModuleId.Core,
            [("na_governance", "governance_updateUser")] = ModuleId.Core,
            [("na_governance", "governance_deleteUser")] = ModuleId.Core,
            [("na_governance", "governance_listProfiles")] = ModuleId.AiStudio,
            [("na_governance", "governance_createProfile")] = ModuleId.AiStudio,
            [("na_governance", "governance_getProfile")] = ModuleId.AiStudio,
            [("na_governance", "governance_updateProfile")] = ModuleId.AiStudio,
            [("na_governance", "governance_deleteProfile")] = ModuleId.AiStudio,
            [("na_governance", "governance_assignProfile")] = ModuleId.AiStudio,
            [("na_governance", "governance_setUserPermissions")] = ModuleId.Governance,
            [("na_governance", "governance_listPolicies")] = ModuleId.Governance,
            [("na_governance", "governance_createPolicy")] = ModuleId.Governance,
            [("na_governance", "governance_getPolicy")] = ModuleId.Governance,
            [("na_governance", "governance_updatePolicy")] = ModuleId.Governance,
            [("na_governance", "governance_deletePolicy")] = ModuleId.Governance,
            [("na_governance", "governance_evaluatePolicy")] = ModuleId.Governance,
            [("na_governance", "governance_createCredential")] = ModuleId.Secrets,
            [("na_governance", "governance_updateCredential")] = ModuleId.Secrets,
            [("na_governance", "governance_deleteCredential")] = ModuleId.Secrets,
            [("na_governance", "governance_listSecrets")] = ModuleId.Secrets,
            [("na_governance", "governance_createSecret")] = ModuleId.Secrets,
            [("na_governance", "governance_getSecret")] = ModuleId.Secrets,
            [("na_governance", "governance_updateSecret")] = ModuleId.Secrets,
            [("na_governance", "governance_deleteSecret")] = ModuleId.Secrets,

            [("na_knowledge", "knowledge_search")] = ModuleId.Knowledge,
            [("na_knowledge", "knowledge_create")] = ModuleId.Knowledge,
            [("na_knowledge", "knowledge_get")] = ModuleId.Knowledge,
            [("na_knowledge", "knowledge_update")] = ModuleId.Knowledge,
            [("na_knowledge", "knowledge_delete")] = ModuleId.Knowledge,
            [("na_knowledge", "learnings_list")] = ModuleId.AiStudio,
            [("na_knowledge", "learnings_create")] = ModuleId.AiStudio,
            [("na_knowledge", "learnings_get")] = ModuleId.AiStudio,
            [("na_knowledge", "learnings_delete")] = ModuleId.AiStudio,

            [("na_notifications", "notifications_listEmailChannels")] = ModuleId.Communications,
            [("na_notifications", "notifications_createEmailChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_getEmailChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_updateEmailChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_deleteEmailChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_testEmailChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_listNotificationChannels")] = ModuleId.Communications,
            [("na_notifications", "notifications_createNotificationChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_getNotificationChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_updateNotificationChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_deleteNotificationChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_sendMessage")] = ModuleId.Communications,
            [("na_notifications", "notifications_checkNotificationChannel")] = ModuleId.Communications,
            [("na_notifications", "notifications_listDeliveries")] = ModuleId.Communications,
            [("na_notifications", "notifications_listReports")] = ModuleId.Artifacts,
            [("na_notifications", "notifications_createReport")] = ModuleId.Artifacts,
            [("na_notifications", "notifications_downloadReport")] = ModuleId.Artifacts,
            [("na_notifications", "notifications_deleteReport")] = ModuleId.Artifacts,

            [("na_admin_readonly", "admin_listAuditEvents")] = ModuleId.Governance,
            [("na_admin_readonly", "admin_getAuditEvent")] = ModuleId.Governance,
            [("na_admin_readonly", "admin_verifyAuditChain")] = ModuleId.Governance,
            [("na_admin_readonly", "admin_listPermissionDomains")] = ModuleId.Governance,
            [("na_admin_readonly", "admin_getUserPermissions")] = ModuleId.Governance,
            [("na_admin_readonly", "admin_listValidations")] = ModuleId.AiStudio,
            [("na_admin_readonly", "admin_listExports")] = ModuleId.Artifacts,
            [("na_admin_readonly", "admin_downloadExport")] = ModuleId.Artifacts,
            [("na_admin_readonly", "admin_listAuthEvents")] = ModuleId.Governance,
        }.ToFrozenDictionary();

    // Specs/*.yaml, by `api` id — the filename without its extension, which is what the
    // agent passes to discover_operations and what the ai_api_specs row is keyed on.
    private static readonly FrozenDictionary<string, IReadOnlyList<ModuleId>> Specs =
        new Dictionary<string, IReadOnlyList<ModuleId>>(StringComparer.OrdinalIgnoreCase)
        {
            ["na_ai_meta"] = [ModuleId.AiStudio, ModuleId.Chat],
            ["na_workflows"] = [ModuleId.Automation],
            ["na_snippets"] = [ModuleId.Automation, ModuleId.Fleet],
            ["na_runs"] = [ModuleId.Automation],
            ["na_triggers"] = [ModuleId.Automation],
            ["na_inventory"] = [ModuleId.Fleet, ModuleId.Secrets],
            ["na_integrations"] = [ModuleId.Integrations],
            ["na_mcp"] = [ModuleId.Integrations],
            ["na_git"] = [ModuleId.Git],
            ["na_knowledge"] = [ModuleId.Knowledge, ModuleId.AiStudio],
            ["na_governance"] = [
                ModuleId.Core, ModuleId.AiStudio, ModuleId.Governance, ModuleId.Secrets],
            // Email and notification channels, plus reports.
            ["na_notifications"] = [ModuleId.Communications, ModuleId.Artifacts],
            // The read-only admin corner: audit and permissions, template validations,
            // exports, and the authentication trail.
            ["na_admin_readonly"] = [
                ModuleId.Governance, ModuleId.AiStudio, ModuleId.Artifacts],
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> AllSkills => Skills.Keys;

    public static IReadOnlyCollection<string> AllSpecs => Specs.Keys;

    public static bool IsSkillAvailable(string name, ModuleSelection selection)
    {
        var normalized = Normalize(name);
        if (!Skills.TryGetValue(normalized, out var modules)) return true;
        if (!modules.All(selection.IsEnabled)) return false;

        return !FocusedSkillReplacements.TryGetValue(normalized, out var richer)
            || !Skills[richer].All(selection.IsEnabled);
    }

    public static bool IsSpecAvailable(string api, ModuleSelection selection) =>
        !Specs.TryGetValue(api, out var modules)
        || modules.Any(selection.IsEnabled);

    public static bool IsBuiltinSpec(string api) => Specs.ContainsKey(api);

    public static bool IsOperationAvailable(
        string api, string operationId, ModuleSelection selection)
    {
        // Operator-authored specs are not deployment-owned content.
        if (!Specs.TryGetValue(api, out var specModules)) return true;
        if (specModules.Count == 1) return selection.IsEnabled(specModules[0]);

        // A mixed built-in spec must classify every operation explicitly. Missing entries
        // fail closed, while the exhaustive test reports exactly what needs ownership.
        return OperationModules.TryGetValue((api, operationId), out var module)
            && selection.IsEnabled(module);
    }

    public static IReadOnlyList<ModuleId>? SkillCoverage(string name) =>
        Skills.TryGetValue(Normalize(name), out var modules) ? modules : null;

    public static IReadOnlyList<ModuleId>? SpecCoverage(string api) =>
        Specs.TryGetValue(api, out var modules) ? modules : null;

    public static ModuleId? OperationCoverage(string api, string operationId) =>
        OperationModules.TryGetValue((api, operationId), out var module) ? module : null;
    private static string Normalize(string name) => name.Replace('\\', '/');
}
