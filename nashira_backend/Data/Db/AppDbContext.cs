using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Models;

namespace nashira_backend.Data.Db;

// Application DbContext. DbSets and entity configurations are added per phase
// (see nashira_refactor.md). Persisted entities derive from BaseModel, which
// carries the soft-delete flag and timestamps.
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<UserToolPermission> UserToolPermissions => Set<UserToolPermission>();
    public DbSet<NavigationPermission> NavigationPermissions => Set<NavigationPermission>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<AIProvider> AIProviders => Set<AIProvider>();
    public DbSet<AIConversation> AIConversations => Set<AIConversation>();
    public DbSet<ConversationAttachment> ConversationAttachments => Set<ConversationAttachment>();
    public DbSet<AiApiSpec> AiApiSpecs => Set<AiApiSpec>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();
    public DbSet<GitWebhook> GitWebhooks => Set<GitWebhook>();
    public DbSet<GitWebhookDelivery> GitWebhookDeliveries => Set<GitWebhookDelivery>();
    public DbSet<ExportArtifact> ExportArtifacts => Set<ExportArtifact>();
    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();
    public DbSet<InventorySource> InventorySources => Set<InventorySource>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AgentTurn> AgentTurns => Set<AgentTurn>();
    public DbSet<AgentLearning> AgentLearnings => Set<AgentLearning>();
    public DbSet<AiPromptSkill> AiPromptSkills => Set<AiPromptSkill>();
    public DbSet<ValidationRecord> ValidationRecords => Set<ValidationRecord>();
    public DbSet<Snippet> Snippets => Set<Snippet>();
    public DbSet<DevicePool> DevicePools => Set<DevicePool>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowTrigger> WorkflowTriggers => Set<WorkflowTrigger>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<AllowedPythonModule> AllowedPythonModules => Set<AllowedPythonModule>();
    public DbSet<AuthEvent> AuthEvents => Set<AuthEvent>();
    public DbSet<SloTarget> SloTargets => Set<SloTarget>();
    public DbSet<TraceEvent> TraceEvents => Set<TraceEvent>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<MessagingChannel> MessagingChannels => Set<MessagingChannel>();
    public DbSet<MessagingIdentityLink> MessagingIdentityLinks => Set<MessagingIdentityLink>();
    public DbSet<MessagingInboundEvent> MessagingInboundEvents => Set<MessagingInboundEvent>();
    public DbSet<MessagingDelivery> MessagingDeliveries => Set<MessagingDelivery>();
    public DbSet<MessagingLinkToken> MessagingLinkTokens => Set<MessagingLinkToken>();
    public DbSet<EmailChannel> EmailChannels => Set<EmailChannel>();
    public DbSet<ReportArtifact> ReportArtifacts => Set<ReportArtifact>();
    public DbSet<Theme> Themes => Set<Theme>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<VendorCommand> VendorCommands => Set<VendorCommand>();
    public DbSet<WorkflowAcceptanceTest> WorkflowAcceptanceTests => Set<WorkflowAcceptanceTest>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<IntegrationAction> IntegrationActions => Set<IntegrationAction>();
    public DbSet<McpServer> McpServers => Set<McpServer>();
    public DbSet<McpTool> McpTools => Set<McpTool>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<SimulationResult> SimulationResults => Set<SimulationResult>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<StepRun> StepRuns => Set<StepRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.UserId);
            e.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.RefreshTokenId);
            e.HasIndex(x => x.TokenHash);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<Profile>(e =>
        {
            e.ToTable("profiles");
            e.HasKey(x => x.ProfileId);
            // Unique among LIVE rows only, matching the application's duplicate
            // check. Deletes are soft everywhere, so an unfiltered index let a
            // deleted row keep reserving its name and re-creating one died on the
            // index with a 500 instead of the controller's 409. Same reasoning on
            // every other `HasFilter("\"IsActive\"")` name/slug index below.
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
        });

        modelBuilder.Entity<UserToolPermission>(e =>
        {
            e.ToTable("user_tool_permissions");
            e.HasKey(x => x.UserToolPermissionId);
            e.HasIndex(x => new { x.UserId, x.ToolDomain }).IsUnique();
        });

        modelBuilder.Entity<NavigationPermission>(e =>
        {
            e.ToTable("navigation_permissions", table =>
                table.HasCheckConstraint(
                    "CK_navigation_permissions_scope",
                    "(\"Role\" IS NOT NULL AND \"UserId\" IS NULL) OR (\"Role\" IS NULL AND \"UserId\" IS NOT NULL)"));
            e.HasKey(x => x.NavigationPermissionId);
            e.Property(x => x.Role).HasMaxLength(16);
            e.Property(x => x.PageKey).HasMaxLength(200);
            e.HasIndex(x => new { x.Role, x.PageKey })
                .IsUnique()
                .HasFilter("\"Role\" IS NOT NULL");
            e.HasIndex(x => new { x.UserId, x.PageKey })
                .IsUnique()
                .HasFilter("\"UserId\" IS NOT NULL");
        });

        modelBuilder.Entity<Secret>(e =>
        {
            e.ToTable("secrets");
            e.HasKey(x => x.SecretId);
            // Name is the lookup key used by ${secret:secret:<name>:value}; must be
            // unique so templates resolve deterministically. Unfiltered on purpose —
            // a soft-deleted row keeps reserving its name, so a re-create cannot
            // silently repoint every template that already names it at a new value.
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<SystemSetting>(e =>
        {
            e.ToTable("system_settings");
            e.HasKey(x => x.SystemSettingId);
            e.HasIndex(x => new { x.Provider, x.SettingKey }).IsUnique();
        });

        modelBuilder.Entity<AIProvider>(e =>
        {
            e.ToTable("ai_providers");
            e.HasKey(x => x.AIProviderId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
        });

        modelBuilder.Entity<AIConversation>(e =>
        {
            e.ToTable("ai_conversations");
            e.HasKey(x => x.AIConversationId);
            e.HasIndex(x => x.UserId);
            // One conversation per external thread, which is how a Slack thread
            // keeps its history across turns instead of starting fresh each message.
            e.HasIndex(x => new { x.MessagingChannelId, x.ExternalThreadId });
        });

        modelBuilder.Entity<ConversationAttachment>(e =>
        {
            e.ToTable("conversation_attachments");
            e.HasKey(x => x.ConversationAttachmentId);
            // One row per (conversation, filename): re-attaching a name is an
            // overwrite, and parse_file's lookup must be deterministic.
            e.HasIndex(x => new { x.ConversationId, x.Filename }).IsUnique();
        });

        modelBuilder.Entity<AiApiSpec>(e =>
        {
            e.ToTable("ai_api_specs");
            e.HasKey(x => x.AiApiSpecId);
            e.HasIndex(x => x.Api)
                .IsUnique()
                .HasFilter("\"IsActive\" = true");
            // The index reloads and the catalog list both filter on the soft-delete
            // flag; the integration key is how a scoped spec is looked up.
            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => x.IntegrationId);
        });

        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.DeviceId);
            e.HasIndex(x => x.DeviceName).IsUnique().HasFilter("\"IsActive\"");
            e.HasIndex(x => x.IpAddress);
            // Sync identity: one row per (source, external id). Filtered so the
            // many manually-created devices (both columns null) don't collide —
            // Postgres would otherwise treat only one NULL pair as distinct.
            e.HasIndex(x => new { x.SourceId, x.ExternalId })
                .IsUnique()
                .HasFilter("\"SourceId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");
            e.Property(x => x.Properties).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Credential>(e =>
        {
            e.ToTable("credentials");
            e.HasKey(x => x.CredentialId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
        });

        modelBuilder.Entity<GitRepository>(e =>
        {
            e.ToTable("git_repositories");
            e.HasKey(x => x.GitRepositoryId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
        });

        modelBuilder.Entity<GitWebhook>(e =>
        {
            e.ToTable("git_webhooks");
            e.HasKey(x => x.GitWebhookId);
            // The route is looked up on every delivery and is half of what
            // authenticates one, so it has to be unique across repositories.
            e.HasIndex(x => x.Route).IsUnique();
            e.HasIndex(x => x.GitRepositoryId);
        });

        modelBuilder.Entity<GitWebhookDelivery>(e =>
        {
            e.ToTable("git_webhook_deliveries");
            e.HasKey(x => x.GitWebhookDeliveryId);
            // The only query: one webhook's history, newest first.
            e.HasIndex(x => new { x.GitWebhookId, x.At });
            // Deduplication. Two retries of the same delivery can race past the
            // receiver's pre-check, and this is what decides which one wins.
            e.HasIndex(x => new { x.GitWebhookId, x.DeliveryKey })
                .IsUnique()
                .HasFilter("\"DeliveryKey\" IS NOT NULL");
        });

        modelBuilder.Entity<ExportArtifact>(e =>
        {
            e.ToTable("export_artifacts");
            e.HasKey(x => x.ExportArtifactId);
        });

        modelBuilder.Entity<KnowledgeArticle>(e =>
        {
            e.ToTable("knowledge_articles");
            e.HasKey(x => x.KnowledgeArticleId);
            // UniqueSlugAsync only avoids ACTIVE slugs, so the index has to agree —
            // unlike the generated slugs below, which dodge every row ever created.
            e.HasIndex(x => x.Slug).IsUnique().HasFilter("\"IsActive\"");
        });

        modelBuilder.Entity<InventorySource>(e =>
        {
            e.ToTable("inventory_sources");
            e.HasKey(x => x.InventorySourceId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
        });

        // Append-only audit log (not a BaseModel). Unique Sequence enforces the
        // chain ordering and backs the "latest row" lookup.
        // Sign-ins, refreshes, lockouts. Separate table from audit_events: different
        // shape, different question, and no hash chain — see AuthEvent.
        modelBuilder.Entity<AuthEvent>(e =>
        {
            e.ToTable("auth_events");
            e.HasKey(x => x.AuthEventId);
            // Every read of this table is "recently, newest first", usually narrowed by
            // one of the other two.
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Event);
        });

        modelBuilder.Entity<TraceEvent>(e =>
        {
            e.ToTable("trace_events");
            e.HasKey(x => x.TraceEventId);
            // Every read is "recently, newest first", narrowed by one of the filters.
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.Action);
            // The one that makes a stuck operation findable: `status = started` is the
            // whole reason the table records starts at all.
            e.HasIndex(x => x.Status);
            // Reconstructing one HTTP call end to end, across this table and the audit
            // trail, which stores the same value.
            e.HasIndex(x => x.RequestId);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<SloTarget>(e =>
        {
            e.ToTable("slo_targets");
            e.HasKey(x => x.SloTargetId);
            // One override per objective. Unique rather than merely indexed: two rows
            // for the same key would make the effective target depend on row order,
            // and a threshold that changes when nobody edited it is worse than a
            // threshold that is wrong.
            e.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.AuditEventId);
            e.HasIndex(x => x.Sequence).IsUnique();
            e.HasIndex(x => x.EntityType);
            // "What did the scheduled runs change last night" is an actor query, and
            // automation rows are exactly the ones with no UserId to filter on.
            // Declared so the model matches what the migration actually created. Without
            // it the snapshot and the database disagree, and the next `migrations add`
            // silently emits an AlterColumn that drops the default — taking with it the
            // thing that keeps pre-actor rows verifiable.
            e.Property(x => x.HashVersion).HasDefaultValue(AuditHashVersion.WithoutActor);
            e.HasIndex(x => x.Actor);
            // Both filters the audit screen leads with.
            e.HasIndex(x => x.EntityId);
            e.HasIndex(x => x.At);
        });

        // Append-only agent telemetry (not a BaseModel). Indexed by conversation for the
        // per-session view and by time for the global one, which is how both are read.
        modelBuilder.Entity<AgentTurn>(e =>
        {
            e.ToTable("agent_turns");
            e.HasKey(x => x.AgentTurnId);
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.StartedAt);
        });

        modelBuilder.Entity<AgentLearning>(e =>
        {
            e.ToTable("agent_learnings");
            e.HasKey(x => x.AgentLearningId);
            e.HasIndex(x => x.ToolName);
            e.HasIndex(x => new { x.Category, x.Confidence });
        });

        modelBuilder.Entity<AiPromptSkill>(e =>
        {
            e.ToTable("ai_prompt_skills");
            e.HasKey(x => x.AiPromptSkillId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            // Covers SkillPromptLoader's hot query verbatim (active rows ordered by
            // priority then name) — it runs on every prompt rebuild.
            e.HasIndex(x => new { x.IsActive, x.Priority, x.Name });
            e.HasIndex(x => x.IntegrationId);
        });

        modelBuilder.Entity<ValidationRecord>(e =>
        {
            e.ToTable("validation_records");
            e.HasKey(x => x.ValidationRecordId);
            e.HasIndex(x => x.Kind);
        });

        modelBuilder.Entity<DevicePool>(e =>
        {
            e.ToTable("device_pools");
            e.HasKey(x => x.DevicePoolId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            // Slugs stay unique across deleted rows too: Slug.Unique() already
            // suffixes past every row ever created, and a slug is an external
            // identity that must never silently repoint. Same below.
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<VendorCommand>(e =>
        {
            e.ToTable("vendor_commands");
            e.HasKey(x => x.VendorCommandId);
            // (intent, platform) is the lookup key and must be unambiguous: two
            // rows would make which command a device gets nondeterministic.
            e.HasIndex(x => new { x.Intent, x.Platform }).IsUnique().HasFilter("\"IsActive\"");
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.JobId);
            // The claim query: queued rows, oldest first.
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            // Delivery dedup: one job per (trigger, delivery id). Filtered so jobs
            // without a delivery key — every scheduler firing — cannot collide.
            e.HasIndex(x => new { x.WorkflowTriggerId, x.DeliveryKey })
                .IsUnique()
                .HasFilter("\"DeliveryKey\" IS NOT NULL");
            e.Property(x => x.PayloadJson).HasColumnType("text");
        });

        modelBuilder.Entity<NotificationChannel>(e =>
        {
            e.ToTable("notification_channels");
            e.HasKey(x => x.NotificationChannelId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<NotificationDelivery>(e =>
        {
            e.ToTable("notification_deliveries");
            e.HasKey(x => x.NotificationDeliveryId);
            // The only query: one channel's history, newest first.
            e.HasIndex(x => new { x.NotificationChannelId, x.SentAt });
        });

        modelBuilder.Entity<MessagingChannel>(e =>
        {
            e.ToTable("messaging_channels");
            e.HasKey(x => x.MessagingChannelId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            e.HasIndex(x => x.Slug).IsUnique();
            // The socket-mode and relay reconcilers sweep by provider every 30s.
            e.HasIndex(x => new { x.Provider, x.Enabled });
        });

        modelBuilder.Entity<MessagingIdentityLink>(e =>
        {
            e.ToTable("messaging_identity_links");
            e.HasKey(x => x.MessagingIdentityLinkId);
            // One internal user per external identity per channel. Unfiltered by
            // IsActive on purpose: a revoked link must still block a second row for
            // the same identity, so re-linking updates the existing row instead.
            e.HasIndex(x => new { x.MessagingChannelId, x.ExternalWorkspaceId, x.ExternalUserId }).IsUnique();
            e.HasIndex(x => x.LinkedUserId);
        });

        modelBuilder.Entity<MessagingInboundEvent>(e =>
        {
            e.ToTable("messaging_inbound_events");
            e.HasKey(x => x.MessagingInboundEventId);
            // The dedupe gate. Providers re-deliver when a webhook is slow to ack,
            // and this index is what makes a parallel re-delivery lose rather than
            // enqueue a second agent turn.
            e.HasIndex(x => new { x.MessagingChannelId, x.ProviderEventId }).IsUnique();
            e.HasIndex(x => new { x.MessagingChannelId, x.At });
        });

        modelBuilder.Entity<MessagingDelivery>(e =>
        {
            e.ToTable("messaging_deliveries");
            e.HasKey(x => x.MessagingDeliveryId);
            e.HasIndex(x => new { x.MessagingChannelId, x.At });
        });

        modelBuilder.Entity<MessagingLinkToken>(e =>
        {
            e.ToTable("messaging_link_tokens");
            e.HasKey(x => x.MessagingLinkTokenId);
            // Lookups present a cleartext token and search by its hash.
            e.HasIndex(x => x.TokenHash);
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<NotificationChannel>(e =>
        {
            e.ToTable("notification_channels");
            e.HasKey(x => x.NotificationChannelId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<NotificationDelivery>(e =>
        {
            e.ToTable("notification_deliveries");
            e.HasKey(x => x.NotificationDeliveryId);
            // The only query: one channel's history, newest first.
            e.HasIndex(x => new { x.NotificationChannelId, x.SentAt });
        });

        modelBuilder.Entity<EmailChannel>(e =>
        {
            e.ToTable("email_channels");
            e.HasKey(x => x.EmailChannelId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<ReportArtifact>(e =>
        {
            e.ToTable("report_artifacts");
            e.HasKey(x => x.ReportArtifactId);
            e.HasIndex(x => x.CreatedAt);
            // Indexed for a retention sweeper that does not exist yet; the list
            // endpoint filters on it today.
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<Theme>(e =>
        {
            e.ToTable("themes");
            e.HasKey(x => x.ThemeId);
            // The list query is "shared, plus mine".
            e.HasIndex(x => new { x.IsShared, x.OwnerUserId });
            e.Property(x => x.ColorsJson).HasColumnType("text");
            e.Property(x => x.SettingsJson).HasColumnType("text");
        });

        modelBuilder.Entity<AllowedPythonModule>(e =>
        {
            e.ToTable("allowed_python_modules");
            e.HasKey(x => x.AllowedPythonModuleId);
            // One row per module: two would make "is this allowed" depend on which
            // the query happened to return.
            e.HasIndex(x => x.Module).IsUnique().HasFilter("\"IsActive\"");
        });

        modelBuilder.Entity<Policy>(e =>
        {
            e.ToTable("policies");
            e.HasKey(x => x.PolicyId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            // Every run evaluates the enabled set, so that filter leads.
            e.HasIndex(x => new { x.IsActive, x.Enabled });
            e.Property(x => x.RuleJson).HasColumnType("text");
        });

        modelBuilder.Entity<WorkflowTrigger>(e =>
        {
            e.ToTable("workflow_triggers");
            e.HasKey(x => x.WorkflowTriggerId);
            e.HasIndex(x => x.WorkflowId);
            // The public ingest looks a delivery up by route on every request, and
            // two triggers sharing one would make which fires nondeterministic.
            e.HasIndex(x => x.Route).IsUnique().HasFilter("\"Route\" IS NOT NULL");
            // The scheduler's sweep query: due cron triggers, soonest first.
            e.HasIndex(x => new { x.Enabled, x.Type, x.NextRunAt });
        });

        modelBuilder.Entity<WorkflowVersion>(e =>
        {
            e.ToTable("workflow_versions");
            e.HasKey(x => x.WorkflowVersionId);
            // One snapshot per (workflow, version): a second would mean two
            // different definitions claiming the same version number.
            e.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
            e.Property(x => x.NodesJson).HasColumnType("text");
            e.Property(x => x.EdgesJson).HasColumnType("text");
        });

        modelBuilder.Entity<WorkflowAcceptanceTest>(e =>
        {
            e.ToTable("workflow_acceptance_tests");
            e.HasKey(x => x.WorkflowAcceptanceTestId);
            e.HasIndex(x => x.WorkflowId);
        });

        modelBuilder.Entity<Snippet>(e =>
        {
            e.ToTable("snippets");
            e.HasKey(x => x.SnippetId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.Type);
            e.Property(x => x.Code).HasColumnType("text");
        });

        modelBuilder.Entity<Integration>(e =>
        {
            e.ToTable("integrations");
            e.HasKey(x => x.IntegrationId);
            // Unique among LIVE integrations only. A plain unique index disagreed with
            // the application's own check, which has always scoped uniqueness to
            // `IsActive`: deleting an integration and creating another by the same name
            // passed validation and then died on the index, surfacing as a 500 with a
            // stack trace instead of anything an operator could act on. Soft-deleted
            // rows have no business reserving a name nobody can see.
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
            // The slug is the cross-instance identity an exported bundle names, so a
            // collision has to be impossible, not merely unlikely — including against
            // a deleted row, because an import matches on it.
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.Type);
        });

        modelBuilder.Entity<IntegrationAction>(e =>
        {
            e.ToTable("integration_actions");
            e.HasKey(x => x.IntegrationActionId);
            e.HasIndex(x => x.IntegrationId);
            // What makes a spec re-sync an upsert instead of a duplicate. Filtered
            // because hand-written actions have no operation id and would otherwise
            // all collide on NULL.
            e.HasIndex(x => new { x.IntegrationId, x.OperationId })
                .IsUnique()
                .HasFilter("\"OperationId\" IS NOT NULL");
        });

        modelBuilder.Entity<McpServer>(e =>
        {
            e.ToTable("mcp_servers");
            e.HasKey(x => x.McpServerId);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"IsActive\"");
        });

        modelBuilder.Entity<McpTool>(e =>
        {
            e.ToTable("mcp_tools");
            e.HasKey(x => x.McpToolId);
            e.HasIndex(x => x.McpServerId);
            e.HasIndex(x => new { x.McpServerId, x.Name }).IsUnique();
            e.Property(x => x.InputSchemaJson).HasColumnType("text");
        });

        modelBuilder.Entity<Workflow>(e =>
        {
            e.ToTable("workflows");
            e.HasKey(x => x.WorkflowId);
            e.HasIndex(x => x.Environment);
            e.HasIndex(x => x.Name);
        });

        modelBuilder.Entity<SimulationResult>(e =>
        {
            e.ToTable("simulation_results");
            e.HasKey(x => x.SimulationResultId);
            e.HasIndex(x => x.WorkflowId);
        });

        modelBuilder.Entity<WorkflowRun>(e =>
        {
            e.ToTable("workflow_runs");
            e.HasKey(x => x.WorkflowRunId);
            e.HasIndex(x => x.WorkflowId);
            // "show me the children of this run" is the query a run detail screen makes
            // for every subflow step; without the index it is a scan of the whole run
            // history per parent.
            e.HasIndex(x => x.ParentRunId);
        });

        modelBuilder.Entity<StepRun>(e =>
        {
            e.ToTable("step_runs");
            e.HasKey(x => x.StepRunId);
            e.HasIndex(x => x.WorkflowRunId);
        });
    }
}
