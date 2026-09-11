using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Permissions;

// Tool permission matrix: each tool has a domain, a role gate (level) and an
// autonomy tier. The ToolDispatcher enforces both. Unknown tools fall back to
// the most restrictive classification (refused). Extended as tools are added.
//
// Levels:  read | write | execute | dangerous
// Tiers:   autonomous | single_confirm | elevated_confirm | human_only
public sealed class PermissionClassifier
{
    public const string TierAutonomous = "autonomous";
    public const string TierSingleConfirm = "single_confirm";
    public const string TierElevatedConfirm = "elevated_confirm";
    public const string TierHumanOnly = "human_only";

    public static readonly Dictionary<string, ToolPermission> Matrix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whoami"] = new("common", "read", TierAutonomous),
        ["list_ai_providers"] = new("common", "read", TierAutonomous),
        // Dynamic API engine (Phase 3) — read/lookup tools.
        ["list_apis"] = new("api", "read", TierAutonomous),
        ["discover_operations"] = new("api", "read", TierAutonomous),
        ["operation_detail"] = new("api", "read", TierAutonomous),
        // execute_operation covers every method; tier can't be known statically,
        // so default to single_confirm (role gate = operator+). Governance C
        // (Phase 5.5) will refine per-operation via IdempotencyKind.
        ["execute_operation"] = new("api", "execute", TierSingleConfirm),
        // Inventory (Phase 4) — read/diagnostic tools.
        ["query_devices"] = new("device", "read", TierAutonomous),
        ["list_credentials"] = new("credential", "read", TierAutonomous),
        ["device_ping"] = new("device", "read", TierAutonomous),
        // SSH command execution (Phase 4). Tool-level single_confirm for now; per-command
        // show-vs-config risk classification + destructive-command policy is Phase 5.5.
        ["device_connect"] = new("device", "execute", TierSingleConfirm),
        // Add a device to inventory — write mutation (operator+), user-confirmed.
        ["create_device"] = new("device", "write", TierSingleConfirm),
        ["update_device"] = new("device", "write", TierSingleConfirm),
        ["delete_device"] = new("device", "write", TierSingleConfirm),
        // Bulk deletes: single_confirm so the batch ALWAYS confirms in chat (the
        // autonomy resolver never waives these — only spec-backed GETs and trusted
        // read-only MCP calls are waived), and each batch spends ONE budget slot.
        ["bulk_delete_devices"] = new("device", "write", TierSingleConfirm),
        ["bulk_delete_reports"] = new("report", "write", TierSingleConfirm),
        ["bulk_delete_workflows"] = new("workflow", "write", TierSingleConfirm),
        ["bulk_delete_snippets"] = new("workflow", "write", TierSingleConfirm),
        // Git (Phase 4 Slice C) — reads autonomous, mutations single_confirm.
        ["git_list_repositories"] = new("git", "read", TierAutonomous),
        ["git_list_files"] = new("git", "read", TierAutonomous),
        ["git_read_file"] = new("git", "read", TierAutonomous),
        ["git_diff"] = new("git", "read", TierAutonomous),
        ["git_status"] = new("git", "read", TierAutonomous),
        ["git_list_branches"] = new("git", "read", TierAutonomous),
        ["git_pull"] = new("git", "write", TierSingleConfirm),
        // Checkout mutates what every later read/write in the turn sees.
        ["git_checkout"] = new("git", "write", TierSingleConfirm),
        ["git_write_file"] = new("git", "write", TierSingleConfirm),
        ["git_commit_push"] = new("git", "write", TierSingleConfirm),
        // GitHub API: creates/lands things outside this system. Merging a PR is
        // the only one that cannot be undone by deleting an object, so it escalates.
        ["github_create_repo"] = new("git", "write", TierSingleConfirm),
        ["github_create_pr"] = new("git", "write", TierSingleConfirm),
        ["github_merge_pr"] = new("git", "write", TierElevatedConfirm),
        // Inbound webhooks. Listing is metadata — no secret is readable — so it is
        // autonomous. Creating one opens a path from outside the system to a workflow
        // run, which is a decision a person makes.
        ["git_list_webhooks"] = new("git", "read", TierAutonomous),
        ["git_create_webhook"] = new("git", "write", TierSingleConfirm),
        // Integrations + MCP (Phase 9). Listing is metadata only — no credentials —
        // so it is autonomous.
        ["list_integrations"] = new("integration", "read", TierAutonomous),
        ["list_mcp_tools"] = new("mcp", "read", TierAutonomous),
        // mcp_call runs someone else's code against someone else's system. Nothing
        // in the protocol distinguishes a read from a write, so the whole surface is
        // treated as state-changing rather than guessed at per tool.
        ["mcp_call"] = new("mcp", "execute", TierSingleConfirm),
        // Export / files / knowledge / email / inventory (Phase 4 remainder).
        ["export_table"] = new("export", "read", TierAutonomous),
        // Same standing as export_table: it writes a file the caller asked for out of
        // data they were already allowed to see, and nothing leaves the system until
        // they click the link (or send_email is called, which confirms on its own).
        ["export_document"] = new("export", "read", TierAutonomous),
        ["parse_file"] = new("file", "read", TierAutonomous),
        // Reading back a document the platform already produced is a read of something
        // the caller could have downloaded from /reports anyway, so both stay autonomous
        // — a confirmation dialog to re-read your own report is the friction that taught
        // people to approve without looking. Saving one persists a row, so it confirms.
        ["list_reports"] = new("report", "read", TierAutonomous),
        ["read_report"] = new("report", "read", TierAutonomous),
        ["save_report"] = new("report", "write", TierSingleConfirm),
        ["search_knowledge"] = new("knowledge", "read", TierAutonomous),
        ["get_knowledge_article"] = new("knowledge", "read", TierAutonomous),
        ["create_knowledge_article"] = new("knowledge", "write", TierAutonomous),
        ["update_knowledge_article"] = new("knowledge", "write", TierAutonomous),
        ["delete_knowledge_article"] = new("knowledge", "write", TierSingleConfirm),
        ["send_email"] = new("email", "write", TierSingleConfirm),
        // Mailbox (IMAP) family. Reads are autonomous; reading never flips \Seen
        // (the folder is opened read-only), so listing/reading truly mutates nothing.
        ["list_email_folders"] = new("email", "read", TierAutonomous),
        ["list_emails"] = new("email", "read", TierAutonomous),
        ["read_email"] = new("email", "read", TierAutonomous),
        ["mark_email"] = new("email", "write", TierSingleConfirm),
        ["move_email"] = new("email", "write", TierSingleConfirm),
        ["archive_email"] = new("email", "write", TierSingleConfirm),
        // Expunge (permanent=true, or an account with no Trash) cannot be undone
        // by anyone — same standing as github_merge_pr.
        ["delete_email"] = new("email", "write", TierElevatedConfirm),
        ["sync_netbox_inventory"] = new("inventory", "write", TierSingleConfirm),
        ["list_inventory_sources"] = new("inventory", "read", TierAutonomous),
        ["create_inventory_source"] = new("inventory", "write", TierSingleConfirm),
        ["update_inventory_source"] = new("inventory", "write", TierSingleConfirm),
        ["delete_inventory_source"] = new("inventory", "write", TierSingleConfirm),
        // Audit + loader: admin-only reads / dry-runs. "dangerous" is the classifier's admin
        // role gate (there is no admin-only "read" level); the tier stays autonomous.
        ["list_audit_events"] = new("audit", "dangerous", TierAutonomous),
        ["get_audit_event"] = new("audit", "dangerous", TierAutonomous),
        ["verify_audit"] = new("audit", "dangerous", TierAutonomous),
        ["validate_template"] = new("loader", "dangerous", TierAutonomous),
        ["list_validations"] = new("loader", "dangerous", TierAutonomous),
        // Workflows: reads are open (viewer); running one executes real changes → elevated_confirm.
        ["list_workflows"] = new("workflow", "read", TierAutonomous),
        ["get_workflow"] = new("workflow", "read", TierAutonomous),
        ["run_workflow"] = new("workflow", "execute", TierElevatedConfirm),
        // ---- Wave 3/4: config + admin/sensitive CRUD. Admin-only domains use "dangerous"
        // as the role gate; destructive ops (delete_user, set_user_permissions, promote) use
        // elevated_confirm. Secret-bearing writes never return the value (handler-enforced).
        ["list_profiles"] = new("profile", "read", TierAutonomous),
        ["create_profile"] = new("profile", "dangerous", TierSingleConfirm),
        ["update_profile"] = new("profile", "dangerous", TierSingleConfirm),
        ["delete_profile"] = new("profile", "dangerous", TierSingleConfirm),
        ["assign_profile"] = new("profile", "dangerous", TierSingleConfirm),
        ["list_skills"] = new("skill", "dangerous", TierAutonomous),
        ["get_skill"] = new("skill", "dangerous", TierAutonomous),
        // Loading the skill for the system the user is asking about is part of
        // answering, not administration: every role, no confirmation.
        ["load_skill"] = new("skill", "read", TierAutonomous),
        ["create_skill"] = new("skill", "dangerous", TierSingleConfirm),
        ["update_skill"] = new("skill", "dangerous", TierSingleConfirm),
        ["delete_skill"] = new("skill", "dangerous", TierSingleConfirm),
        ["list_specs"] = new("spec", "dangerous", TierAutonomous),
        ["get_spec"] = new("spec", "dangerous", TierAutonomous),
        ["create_spec"] = new("spec", "dangerous", TierSingleConfirm),
        ["update_spec"] = new("spec", "dangerous", TierSingleConfirm),
        ["delete_spec"] = new("spec", "dangerous", TierSingleConfirm),
        ["list_learnings"] = new("learning", "dangerous", TierAutonomous),
        ["create_learning"] = new("learning", "dangerous", TierSingleConfirm),
        ["update_learning"] = new("learning", "dangerous", TierSingleConfirm),
        ["delete_learning"] = new("learning", "dangerous", TierSingleConfirm),
        // The snippet catalogue. Browsing is a read and needs no confirmation — an
        // agent that has to ask before it can even see the catalogue will author
        // around it, which is how a hundred workflows ended up with no work nodes.
        // Creating one is an ordinary authoring write, same tier as create_workflow.
        // network_enabled is not reachable from the tool at all: it is absent from the
        // parameter schema and forced false in the handler.
        ["list_snippets"] = new("workflow", "read", TierAutonomous),
        ["create_snippet"] = new("workflow", "write", TierSingleConfirm),
        ["create_workflow"] = new("workflow", "write", TierSingleConfirm),
        ["update_workflow"] = new("workflow", "write", TierSingleConfirm),
        ["delete_workflow"] = new("workflow", "write", TierSingleConfirm),
        ["simulate_workflow"] = new("workflow", "write", TierAutonomous),
        ["promote_workflow"] = new("workflow", "write", TierElevatedConfirm),
        ["list_users"] = new("user", "dangerous", TierAutonomous),
        ["create_user"] = new("user", "dangerous", TierSingleConfirm),
        ["update_user"] = new("user", "dangerous", TierSingleConfirm),
        ["delete_user"] = new("user", "dangerous", TierElevatedConfirm),
        ["list_permission_domains"] = new("permission", "dangerous", TierAutonomous),
        ["get_user_permissions"] = new("permission", "dangerous", TierAutonomous),
        ["set_user_permissions"] = new("permission", "dangerous", TierElevatedConfirm),
        ["list_secrets"] = new("secret", "dangerous", TierAutonomous),
        ["set_secret"] = new("secret", "dangerous", TierSingleConfirm),
        ["clear_secret"] = new("secret", "dangerous", TierSingleConfirm),
        ["create_credential"] = new("credential", "dangerous", TierSingleConfirm),
        ["update_credential"] = new("credential", "dangerous", TierSingleConfirm),
        ["delete_credential"] = new("credential", "dangerous", TierSingleConfirm),
        ["create_ai_provider"] = new("provider", "dangerous", TierSingleConfirm),
        ["update_ai_provider"] = new("provider", "dangerous", TierSingleConfirm),
        ["delete_ai_provider"] = new("provider", "dangerous", TierSingleConfirm),
    };

    private readonly ILogger<PermissionClassifier> _logger;

    public PermissionClassifier(ILogger<PermissionClassifier> logger) => _logger = logger;

    public ToolPermission GetPermission(string toolName)
    {
        if (Matrix.TryGetValue(toolName, out var perm)) return perm;
        _logger.LogWarning("ai.permission.classify.unknown tool={Tool} -> human_only", toolName);
        return new ToolPermission("unknown", "dangerous", TierHumanOnly);
    }

    public bool IsAllowed(string toolName, string userRole, ICurrentUser? tenant = null)
    {
        var perm = GetPermission(toolName);
        var allowed = perm.Level switch
        {
            "read" => true,
            "write" or "execute" => userRole is "admin" or "operator",
            "dangerous" => userRole == "admin",
            _ => false,
        };
        if (!allowed)
            _logger.LogWarning("ai.permission.denied tool={Tool} role={Role} level={Level}", toolName, userRole, perm.Level);
        return allowed;
    }

    // Reads/simulations are free; every other tier spends a mutation-budget slot.
    public bool CountsAgainstMutationBudget(string toolName) =>
        !string.Equals(GetPermission(toolName).Tier, TierAutonomous, StringComparison.OrdinalIgnoreCase);

    // Reconciles the tool-level level with the workflow.v1 kit's operation-risk taxonomy
    // (read / mutation / high_risk_mutation). Nashira's true risk model is this + IdempotencyKind
    // for workflow nodes; the oracle has no separate op-level classifier (idempotency is
    // snippet-scoped). Used where an op-risk label is needed for a workflow operation.
    public string RiskOf(string toolName) => GetPermission(toolName).Level switch
    {
        "read" => "read",
        "write" or "execute" => "mutation",
        _ => "high_risk_mutation",
    };
}

public sealed record ToolPermission(string Domain, string Level, string Tier);
