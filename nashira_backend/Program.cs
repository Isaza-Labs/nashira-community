using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using nashira_backend.Configuration;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Ai.RestExecutor;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.SelfCorrection;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Email;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Errors;
using nashira_backend.Services.Export;
using nashira_backend.Services.Files;
using nashira_backend.Services.Git;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Inventory;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Modules;
using nashira_backend.Services.Net;
using nashira_backend.BackgroundServices;
using nashira_backend.Services.Scheduler;
using nashira_backend.Services.Security;
using nashira_backend.Services.Ssh;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using nashira_backend.Services.Observability;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.SystemConsole.Themes;

// Bootstrap logger: captures failures that happen before the host is built.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var moduleSelection = ModuleSelection.Parse(
        Environment.GetEnvironmentVariable(ModuleSelection.EnvironmentVariableName));
    builder.Services.AddSingleton(moduleSelection);
    Log.Information(
        "deployment.modules.resolved mode={Mode} enabled={EnabledModules}",
        moduleSelection.Mode,
        string.Join(", ", moduleSelection.Enabled.Select(module => ModuleCatalog.Get(module).Key)));

    // Serilog - console sink format depends on LOG_FORMAT env var:
    //   pretty -> colored, human-readable lines (default in Development)
    //   json   -> CLEF one-line-per-event (default in Production; ideal for
    //             aggregators like Loki/Elastic that parse structured fields)
    //
    // Verbosity knobs (all env vars, parsed as Serilog LogEventLevel -
    // values: Verbose, Debug, Information, Warning, Error, Fatal):
    //
    //   LOG_EF_SQL       -> verbosity of Microsoft.EntityFrameworkCore.Database.Command.
    //                       Default Warning (silent in steady state). Set to
    //                       Information to see every SQL query with timing;
    //                       useful for debugging N+1, timeouts, or row counts.
    //                       Use sparingly - a single API call produces dozens
    //                       of SELECT lines at Information.
    //
    //   LOG_ACTIONS      -> verbosity of the application services tree
    //                       nashira_backend.Services.* as a whole. Covers
    //                       Engine, Worker, Workflow, WorkflowRun, StepRun, Job,
    //                       Snippet, Device, DevicePool, Credential, Integration,
    //                       InventorySource, Skill, AIAgent, AIProvider, Ssh,
    //                       Scheduler, Auth, Policy, Secret, Git, Messaging...
    //                       The "what is the platform doing right now" logs.
    //                       Default Information; set to Debug for deep traces
    //                       (per-device loops, policy decisions, secret-hit
    //                       bookkeeping), Warning to silence almost everything
    //                       except state changes + errors. Does NOT affect
    //                       `Services.Ai` - that subtree is pinned to Debug and
    //                       wins by longest-prefix match.
    //
    //   LOG_FORMAT       -> pretty | json (see above).
    //   AI_LOG_PAYLOADS  -> true to dump raw SSE chunks from the LLM providers
    //                       (OpenAiProvider). Very noisy, triage-only - and it
    //                       logs at Debug, so it only takes effect because the
    //                       `Services.Ai` override below sits at Debug.
    //
    // Levels are built here in code rather than read from the `Logging:LogLevel`
    // section: UseSerilog replaces the Microsoft.Extensions.Logging factory, so
    // that section never reaches the pipeline. Keeping the knobs in one place
    // avoids config that silently does nothing.
    builder.Host.UseSerilog((context, services, cfg) =>
    {
        var logFormat = Environment.GetEnvironmentVariable("LOG_FORMAT")?.Trim().ToLowerInvariant();
        var pretty = logFormat switch
        {
            "pretty" or "text" or "console" => true,
            "json" or "clef" or "compact" => false,
            _ => context.HostingEnvironment.IsDevelopment(),
        };

        // EF SQL commands - loud at Information, near-silent at Warning.
        // Default Warning because the steady-state noise drowns the action
        // logs the operator actually cares about. Flip to Information when
        // hunting query bugs.
        var efSqlLevel = ParseLogLevel(
            Environment.GetEnvironmentVariable("LOG_EF_SQL"), LogEventLevel.Warning);

        // Application action subtree - default Information gives you the
        // "workflow X started", "step Y dispatched", "handler Z finished"
        // timeline. Bumping to Debug adds per-iteration / per-device lines
        // from the orchestrator + worker; useful when a step misbehaves.
        var actionsLevel = ParseLogLevel(
            Environment.GetEnvironmentVariable("LOG_ACTIONS"), LogEventLevel.Information);

        cfg
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", efSqlLevel)
            .MinimumLevel.Override("nashira_backend", LogEventLevel.Information)
            // LOG_ACTIONS governs the whole application-services tree
            // (workflow/worker/engine/integration/device/credential/auth/policy/...).
            // Serilog resolves overrides by longest prefix match, so the
            // more-specific `.Services.Ai` Debug override below still wins
            // for the AI chat subtree even if LOG_ACTIONS is set to Warning.
            .MinimumLevel.Override("nashira_backend.Services", actionsLevel)
            .MinimumLevel.Override("nashira_backend.Services.Ai", LogEventLevel.Debug)
            .MinimumLevel.Override("nashira_backend.Controllers.AiChatController", LogEventLevel.Debug)
            .MinimumLevel.Override("nashira_backend.BackgroundServices", LogEventLevel.Debug)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("service", "nashira_backend")
            .Enrich.WithProperty("env", context.HostingEnvironment.EnvironmentName);

        if (pretty)
        {
            // {ShortContext} is the last segment of {SourceContext} only, so long
            // namespaces don't drown the line. See LogEnrichers for how it and
            // {CallerTag} are produced.
            const string template =
                "[{Timestamp:HH:mm:ss} {Level:u3}] {ShortContext}{CallerTag}{Message:lj}{NewLine}{Exception}";
            cfg.Enrich.With<ShortContextEnricher>()
               .Enrich.With<CallerTagEnricher>()
               .WriteTo.Console(
                    outputTemplate: template,
                    theme: AnsiConsoleTheme.Code,
                    applyThemeToRedirectedOutput: true);
        }
        else
        {
            cfg.WriteTo.Console(new CompactJsonFormatter());
        }
    });

    // ── Configuration ─────────────────────────────────────────────
    builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
    builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
    var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>();
    JwtOptions.ValidateForBoot(jwt, builder.Environment.EnvironmentName);

    // ── Data ──────────────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

    // Data Protection: encrypts Secret values at rest. Keyring persists to disk
    // (mount a volume in prod; wrap with a KMS provider later if required).
    var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "keyring");
    Directory.CreateDirectory(keyRingPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
        .SetApplicationName("nashira-backend");
    builder.Services.AddSingleton<ISecretProtector, SecretProtector>();

    // ── Auth / tenancy ────────────────────────────────────────────
    builder.Services.AddHttpContextAccessor();
    // ICurrentUser is context-dependent: an HTTP request reads the JwtBearer claims;
    // a background scope (job worker, scheduler) has no HttpContext and gets the
    // scope's MutableCurrentUser, which whoever created the scope may Bind() to run
    // the work as a specific user. Unbound, it reports unauthenticated — the same
    // answers CurrentUser gives outside a request — so scopes that never bind are
    // unaffected.
    builder.Services.AddScoped<CurrentUser>();
    builder.Services.AddScoped<MutableCurrentUser>();
    builder.Services.AddScoped<ICurrentUser>(sp =>
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext is null
            ? sp.GetRequiredService<MutableCurrentUser>()
            : sp.GetRequiredService<CurrentUser>());
    builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
    builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
    builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
    builder.Services.AddScoped<IPasswordPolicy, PasswordPolicy>();
    builder.Services.AddScoped<IAuthService, AuthService>();
    // The authentication trail: sign-ins, refreshes, lockouts. Separate from the audit
    // logger because it answers a different question and takes no hash-chain lock.
    builder.Services.AddScoped<IAuthAuditLogger, AuthAuditLogger>();

    // ── AI (Phase 2) ──────────────────────────────────────────────
    builder.Services.Configure<AiChatOptions>(
        builder.Configuration.GetSection(AiChatOptions.SectionName));
    // A backstop, not the budget. The turn deadline in AiChatOptions is what should
    // end a long turn, and it can only do that if the transport outlives it: on the
    // default 100s this timeout fired first and every over-long turn surfaced as a
    // raw TaskCanceledException instead of the runner's own timeout path.
    builder.Services.AddHttpClient("llm", c => c.Timeout = AiChatOptions.TransportTimeout);
    builder.Services.AddScoped<LlmProviderFactory>();

    // Tool system: registry (singleton metadata) + dispatcher (scoped, per turn)
    // + permission classifier + handlers (scoped, one per dispatch).
    builder.Services.AddSingleton<PermissionClassifier>();
    builder.Services.AddSingleton<ToolRegistry>();
    builder.Services.AddScoped<ToolDispatcher>();
    // Per-user resource grants: scoped, because both read the current user and the
    // database rows that describe what is registered right now.
    builder.Services.AddScoped<IToolResourceGuard, ToolResourceGuard>();
    builder.Services.AddScoped<IPermissionCatalog, PermissionCatalog>();
    builder.Services.AddScoped<nashira_backend.Services.Navigation.NavigationPermissionService>();
    // Decides whether a normally-confirmed call is a read, so a GET is not gated like
    // a DELETE.
    builder.Services.AddScoped<IToolAutonomyResolver, ToolAutonomyResolver>();

    // Audit trail (Phase 5): hash-chained, append-only mutation log.
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();
    // Undo for soft deletes, driven from a delete audit event (/api/audit/{id}/restore).
    builder.Services.AddScoped<EntityRestoreService>();

    // Self-correction (Phase 5): learnings store + engine (scoped per chat turn).
    builder.Services.AddScoped<ILearningsStore, LearningsStore>();
    builder.Services.AddScoped<SelfCorrectionEngine>();

    // Loader (Phase 5): template security validation + validation history.
    builder.Services.AddSingleton<ITemplateSecurityValidator, TemplateSecurityValidator>();
    builder.Services.AddScoped<IValidationRecorder, ValidationRecorder>();

    // Canonical workflow engine (Phase 5.5): schema validator (from the shipped schema) + validator.
    builder.Services.AddSingleton(sp =>
    {
        var contentRoot = sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        var schemaPath = Path.Combine(contentRoot, "Data", "Schemas", "workflow.v1.schema.json");
        return new WorkflowSchemaValidator(File.ReadAllText(schemaPath));
    });
    builder.Services.AddSingleton<WorkflowValidator>();
    // Scoped, not singleton: it asks the database which snippets exist.
    builder.Services.AddScoped<WorkflowReferenceChecker>();
    builder.Services.AddSingleton<WorkflowYamlCompiler>();
    builder.Services.AddSingleton<WorkflowYamlParser>();
    builder.Services.AddScoped<WorkflowSimulationService>();
    builder.Services.AddScoped<PromotionService>();
    // The workflow engine (Phase 9 / bloque B). Nodes now dispatch by `snippet_id`
    // to an ISnippetHandler, which is what workflow.v1 has always described;
    // ToolNodeExecutor's config_overrides.tool binding was a stopgap and is gone.
    // Which snippet types this deployment can run lives in SnippetHandlerCatalog: a
    // handler enters the registry only when its capability, and the engine itself, are
    // enabled. The reference checker then refuses a workflow naming a type this
    // deployment cannot run, and DefaultSnippetSeeder seeds no snippet for it.
    var snippetHandlerTypes = SnippetHandlerCatalog.Enabled(moduleSelection);
    Log.Information(
        "deployment.modules.snippets registered={Registered} of={Total}",
        snippetHandlerTypes.Count, SnippetHandlerCatalog.All.Count);
    foreach (var t in snippetHandlerTypes) builder.Services.AddScoped(t);
    builder.Services.AddSingleton<ISnippetHandlerRegistry>(sp =>
        new SnippetHandlerRegistry(snippetHandlerTypes, sp));

    builder.Services.AddSingleton<nashira_backend.Services.Worker.Python.IPythonSandbox,
        nashira_backend.Services.Worker.Python.PythonSandbox>();
    builder.Services.AddSingleton<IVariableResolver, VariableResolver>();
    builder.Services.AddSingleton<IConditionEvaluator, ConditionEvaluator>();
    // Shared by the REST controller, the agent's create_snippet tool and bundle
    // import, so the three write paths cannot diverge on what they accept.
    builder.Services.AddScoped<SnippetCatalog>();
    // The portable cross-instance format: export builds it, import resolves it.
    builder.Services.AddScoped<IWorkflowBundleService, WorkflowBundleService>();
    // One answer for "what tier is this node", shared by the plan endpoint and the
    // step executor (execution/SPEC.md §2).
    builder.Services.AddScoped<NodeTierResolver>();
    builder.Services.AddScoped<SnippetNodeExecutor>();
    // A `subflow` node runs another workflow as a real run of its own; the runner opens
    // the child its own scope, because the child needs its own DbContext, its own step
    // outputs and its own run context (execution/SPEC.md §5).
    builder.Services.AddScoped<ISubflowRunner, SubflowRunner>();
    builder.Services.AddScoped<INodeExecutor>(sp => sp.GetRequiredService<SnippetNodeExecutor>());
    builder.Services.AddScoped<WorkflowRunService>();
    builder.Services.AddScoped<IAcceptanceTestRunner, AcceptanceTestRunner>();
    // Corporate guardrails, evaluated before a run executes anything.
    builder.Services.AddScoped<nashira_backend.Services.Policy.IPolicyEvaluator,
        nashira_backend.Services.Policy.PolicyEvaluator>();
    // Gates are the promotion-time half: a deny asks "is this forbidden?", a gate
    // asks "has this earned it yet?". Fully qualified because `Policy` is also a
    // model type and an [Authorize(Policy = ...)] name.
    builder.Services.AddScoped<nashira_backend.Services.Policy.IPolicyGate,
        nashira_backend.Services.Policy.PolicyGate>();
    // The job queue: webhook deliveries and cron firings enqueue; the worker
    // claims with SKIP LOCKED and executes. Both hosted services are replica-safe:
    // the scheduler's re-base and the worker's claim are each one atomic UPDATE.
    builder.Services.AddScoped<nashira_backend.Services.Jobs.IJobQueue,
        nashira_backend.Services.Jobs.JobQueue>();
    // Service-level objectives: one computation shared by the screen and the daily
    // sweep, so an alert can never disagree with the dashboard it came from.
    builder.Services.AddScoped<nashira_backend.Services.Slo.SloComputeService>();
    // The operational trail. A singleton because the queue must outlive the request
    // that filled it; the writer is the only thing allowed to drain it.
    builder.Services.AddSingleton<nashira_backend.Services.Trace.ITraceLogger,
        nashira_backend.Services.Trace.TraceLogger>();
    // Settings an admin may change without a redeploy. A singleton holding a snapshot:
    // the read sites are synchronous and hot, and the values change perhaps twice a year.
    builder.Services.AddSingleton<nashira_backend.Services.Settings.AppSettingsProvider>();
    // Which background workers this process starts lives in HostedServiceCatalog. A
    // worker is the one component that keeps acting with nobody asking, so a disabled
    // capability must not leave one registered.
    builder.Services.AddModuleHostedServices(moduleSelection);
    Log.Information(
        "deployment.modules.hosted_services started={Started} of={Total}",
        HostedServiceCatalog.Enabled(moduleSelection).Count, HostedServiceCatalog.All.Count);
    builder.Services.AddScoped<nashira_backend.Services.DevicePools.IDevicePoolResolver,
        nashira_backend.Services.DevicePools.DevicePoolResolver>();
    // Ambient "which environment is executing right now", read by the device-targeting
    // tools to enforce each device's allow_draft/allow_qa/allow_production trio.
    builder.Services.AddScoped<WorkflowExecutionScope>();

    // Dynamic API engine (Phase 3): in-memory index of OpenAPI specs,
    // secret resolver, SSRF guard, and the REST executor behind execute_operation.
    builder.Services.AddSingleton<IApiSpecIndex, YamlSpecIndex>();
    builder.Services.AddScoped<ISecretResolver, SecretResolver>();
    builder.Services.AddSingleton<IUrlGuard, UrlGuard>();
    builder.Services.AddScoped<IRestOperationExecutor, RestOperationExecutor>();
    builder.Services.AddHttpClient("rest_call");
    builder.Services.AddHttpClient("rest_call_insecure")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // For on-prem integrations with self-signed certs (AiApiSpec.verify_ssl=false).
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

    // Integrations (Phase 9): base URL + credentials for an external system, plus the
    // action catalog projected from the OpenAPI specs linked to it.
    builder.Services.AddSingleton<IIntegrationOAuthTokenService, IntegrationOAuthTokenService>();
    builder.Services.AddScoped<IIntegrationAuthApplier, IntegrationAuthApplier>();
    builder.Services.AddScoped<IIntegrationHealthChecker, IntegrationHealthChecker>();
    builder.Services.AddScoped<IIntegrationActionSync, IntegrationActionSync>();
    // Attaching skills and specs from the integration itself, so one upload updates
    // the spec, the agent's index and the action catalog together.
    builder.Services.AddScoped<IIntegrationCatalog, IntegrationCatalog>();
    builder.Services.AddHttpClient(IntegrationHttpClients.Secure);
    builder.Services.AddHttpClient(IntegrationHttpClients.Insecure)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // Opt-in per integration (verify_ssl=false) for appliances behind a private CA.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

    // MCP (Phase 9): Nashira as an MCP *client* — connects to registered servers over
    // Streamable HTTP, caches their tool catalogs, and calls them.
    builder.Services.AddSingleton<IMcpTokenService, McpTokenService>();
    builder.Services.AddScoped<IMcpConnectionFactory, McpConnectionFactory>();
    builder.Services.AddScoped<IMcpClient, McpClient>();
    builder.Services.AddScoped<IMcpServerService, McpServerService>();
    // The OAuth authorization-code flow, ported from FlowWeaver: the stateless
    // 2.1 client mechanics (singleton) and the redirect flow that persists the
    // PKCE verifier and tokens (scoped — it writes McpServer rows).
    builder.Services.AddSingleton<IMcpOAuthService, McpOAuthService>();
    builder.Services.AddScoped<IMcpOAuthFlowService, McpOAuthFlowService>();
    // Infinite client timeout on purpose: SSE streams must not be cut mid-flight.
    // The real budgets are the per-operation linked CTS deadlines inside McpClient.
    builder.Services.AddHttpClient(McpHttpClients.Secure)
        .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
    builder.Services.AddHttpClient(McpHttpClients.Insecure)
        .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

    // SSH command execution (Phase 4): spawns the Python netmiko runner as a subprocess.
    builder.Services.AddSingleton<ISshCommandRunner, SshCommandRunner>();
    // Per-command SSH governance (Phase 5.5): classify + block destructive commands.
    builder.Services.AddSingleton<ISshCommandPolicy, SshCommandPolicy>();

    // Git integration (Phase 4): LibGit2Sharp working copies + git tools.
    builder.Services.AddScoped<IGitService, GitService>();
    builder.Services.AddScoped<IGitHubService, GitHubService>();
    builder.Services.AddScoped<IGitWebhookService, GitWebhookService>();
    // Scoped, not singleton: it pulls through IGitService and enqueues through
    // IJobQueue, both of which want the request's DbContext.
    builder.Services.AddScoped<GitWebhookReceiver>();

    // Export / file parsing / email / NetBox sync (Phase 4 remainder).
    builder.Services.AddScoped<IExportService, ExportService>();
    // QuestPDF community licence — registered once globally. Doing it per export is
    // redundant and surprises any test that builds a document without going through
    // startup. The library refuses to render until this is set.
    QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    builder.Services.AddSingleton<IFileParsingService, FileParsingService>();
    builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
    builder.Services.AddSingleton<IEmailService, EmailService>();
    // Credentials mail on user creation, with an admin-facing warning when the
    // relay is not configured.
    builder.Services.AddSingleton<IUserCredentialsNotifier, UserCredentialsNotifier>();
    // Per-channel SMTP, alongside the config-based default path.
    builder.Services.AddSingleton<IEmailChannelSender, EmailChannelSender>();
    // The inbound (IMAP) side of the same channels: list/read/mark/move/archive/delete.
    builder.Services.AddSingleton<IEmailChannelMailbox, EmailChannelMailbox>();
    // System mail (e.g. credentials) falls back to a channel when Smtp:* is absent.
    builder.Services.AddSingleton<IEmailChannelFallback, EmailChannelFallback>();
    builder.Services.AddScoped<INetBoxSyncService, NetBoxSyncService>();
    // Outbound notification channels (Slack / Teams / generic webhook).
    builder.Services.AddScoped<nashira_backend.Services.Notifications.INotificationDispatcher,
        nashira_backend.Services.Notifications.NotificationDispatcher>();
    builder.Services.AddHttpClient(nashira_backend.Services.Notifications.NotificationHttpClients.Name)
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20));

    // Bidirectional messaging channels (Slack / Telegram / WhatsApp / Teams).
    builder.Services.Configure<nashira_backend.Services.Messaging.MessagingOptions>(
        builder.Configuration.GetSection(nashira_backend.Services.Messaging.MessagingOptions.SectionName));
    // Providers are stateless strategies resolved by name, so one instance each.
    builder.Services.AddSingleton<nashira_backend.Services.Messaging.IMessagingProvider,
        nashira_backend.Services.Messaging.Providers.SlackProvider>();
    builder.Services.AddSingleton<nashira_backend.Services.Messaging.IMessagingProvider,
        nashira_backend.Services.Messaging.Providers.TelegramProvider>();
    builder.Services.AddSingleton<nashira_backend.Services.Messaging.IMessagingProvider,
        nashira_backend.Services.Messaging.Providers.WhatsAppProvider>();
    builder.Services.AddSingleton<nashira_backend.Services.Messaging.IMessagingProvider,
        nashira_backend.Services.Messaging.Providers.TeamsProvider>();
    builder.Services.AddSingleton<nashira_backend.Services.Messaging.IMessagingProviderResolver,
        nashira_backend.Services.Messaging.MessagingProviderResolver>();
    // The ingest opens its own scope per delivery, so it is safe as a singleton.
    builder.Services.AddSingleton<nashira_backend.Services.Messaging.IMessagingIngestService,
        nashira_backend.Services.Messaging.MessagingIngestService>();
    builder.Services.AddScoped<nashira_backend.Services.Messaging.IMessagingLinkService,
        nashira_backend.Services.Messaging.MessagingLinkService>();
    builder.Services.AddScoped<nashira_backend.Services.Messaging.IMessagingJobProcessor,
        nashira_backend.Services.Messaging.MessagingJobProcessor>();
    builder.Services.AddHttpClient(nashira_backend.Services.Messaging.MessagingHttpClients.Name)
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

    // Which tools the agent is even told about lives in ToolHandlerCatalog: a handler
    // enters the registry only when its capability, and the agent surface itself, are
    // enabled. A tool the deployment cannot run must never reach the model — told a
    // tool exists, it keeps reaching for it.
    var toolHandlerTypes = ToolHandlerCatalog.Enabled(moduleSelection);
    Log.Information(
        "deployment.modules.tools registered={Registered} of={Total}",
        toolHandlerTypes.Count, ToolHandlerCatalog.All.Count);
    foreach (var handlerType in toolHandlerTypes)
        builder.Services.AddScoped(handlerType);

    // Chat loop: system-prompt loader + the conversation runner (per turn).
    builder.Services.AddSingleton<ISkillPromptLoader, SkillPromptLoader>();
    builder.Services.AddScoped<ScopedSkillCatalog>();
    builder.Services.AddScoped<UserProfileContextLoader>();
    builder.Services.AddScoped<AgentTurnScope>();
    builder.Services.AddScoped<AgentConversationRunner>();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt!.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("Admin", p => p.RequireRole("admin"));
        options.AddPolicy("Operator", p => p.RequireRole("admin", "operator"));
        options.AddPolicy("Viewer", p => p.RequireAuthenticatedUser());
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    });

    // ── HTTP surface ──────────────────────────────────────────────
    // Filter order is not registration order: the module gate is a resource filter, so
    // it always runs before model binding and before the action filter that audits
    // mutations — a request to a disabled module is neither bound nor audited.
    builder.Services.AddControllers(options =>
    {
        // Stamps each action with the capability that owns it, from
        // EndpointModuleCatalog. Runs while the action model is built, so an
        // unclassified action fails here rather than answering in a deployment that
        // never meant to run it.
        options.Conventions.Add(new ModuleControllerConvention());
        options.Filters.Add<ModuleAvailabilityFilter>();
        options.Filters.Add<AuditMutationFilter>();
    });

    // CORS: let the SPA dev origin(s) call the API cross-origin. In production the
    // SPA is served same-origin from wwwroot, so this matters only for dev (Vite on
    // :5173) or split deployments. Origins are read from Cors:AllowedOrigins.
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options => options.AddPolicy("spa", policy =>
    {
        if (corsOrigins.Length > 0)
            policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
    }));
    // Restores the client address from X-Forwarded-For for proxies the operator
    // declared trusted. Without it every browser request looks like it came from
    // the frontend container. See ForwardedHeadersConfiguration.
    builder.Services.AddForwardedHeadersFromConfig(builder.Configuration);

    // Per-endpoint request throttling; policies in RateLimitingConfiguration.
    builder.Services.AddNashiraRateLimiter();

    builder.Services.AddModuleAwareOpenApi(moduleSelection);
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<DomainExceptionHandler>();
    builder.Services.AddExceptionHandler<UniqueViolationExceptionHandler>();
    builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>("db", tags: ["ready"]);

    var app = builder.Build();

    // FIRST in the pipeline: everything downstream that reads the caller's
    // address — the login rate limiter, AuditEvent.Ip, AuthEvent.Ip — must see
    // the rewritten value, not the proxy's. Only rewrites for trusted proxies;
    // see ForwardedHeadersConfiguration.
    app.UseForwardedHeaders();
    app.Logger.LogInformation(
        "forwarded-headers trusted_proxies={TrustedProxies}",
        ForwardedHeadersConfiguration.Describe(builder.Configuration));

    app.UseExceptionHandler();
    // Serilog's own request logging - one structured line per HTTP request with
    // method, path, status and elapsed_ms. Health and OpenAPI polling drop to
    // Debug: the container healthcheck hits /health/live every few seconds and
    // would otherwise be most of the log.
    app.UseSerilogRequestLogging(o =>
    {
        o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} in {Elapsed:0.00}ms";
        o.GetLevel = (httpCtx, elapsed, ex) =>
        {
            if (ex is not null) return LogEventLevel.Error;
            if (httpCtx.Response.StatusCode >= 500) return LogEventLevel.Error;
            if (httpCtx.Response.StatusCode >= 400) return LogEventLevel.Warning;
            var path = httpCtx.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/health", StringComparison.Ordinal)) return LogEventLevel.Debug;
            if (path.StartsWith("/openapi", StringComparison.Ordinal)) return LogEventLevel.Debug;
            return LogEventLevel.Information;
        };
    });

    // Serve the built SPA (wwwroot) same-origin: default files + static assets,
    // placed before auth so the shell and assets load without a token. No-op in
    // dev (no wwwroot build) — the Vite dev server serves the SPA there instead.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
    }

    // CORS for the SPA dev origin(s) (Cors:AllowedOrigins). Production is
    // same-origin from wwwroot, so this only applies to dev / split deployments.
    app.UseCors("spa");

    app.UseAuthentication();

    // Between authentication and authorization, and the order is the whole point:
    // after authentication so a traced request can say who made it, but BEFORE
    // authorization, which short-circuits a 401 or a 403 without ever reaching the
    // rest of the pipeline. Placed after it, every refusal went unrecorded — and
    // "why was I denied" is the question this trail gets opened for.
    app.UseMiddleware<nashira_backend.Services.Trace.TraceRequestMiddleware>();

    app.UseAuthorization();

    // After authentication so the per-user policies can partition on the JWT's
    // user id claim; anonymous surfaces (login, webhooks) partition by IP.
    app.UseRateLimiter();

    app.MapControllers();

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    app.MapGet("/", () => Results.Ok(new { name = "nashira", status = "ok", version })).AllowAnonymous();

    // SPA deep-link fallback: unmatched routes return index.html so the client
    // router handles them. Anonymous — the shell loads before login. Lowest
    // priority, so it never shadows API/health endpoints. No-op if wwwroot absent.
    app.MapFallbackToFile("index.html").AllowAnonymous();

    // Apply EF migrations on boot (retry while Postgres warms up), then seed.
    // Migrations stay global: the schema is one thing, its contents another, and a
    // table left uncreated would turn re-enabling a module into a redeploy.
    await ApplyMigrationsWithRetryAsync(app, TimeSpan.FromSeconds(60));

    // Which baselines this deployment installs lives in SeederModuleCatalog. Every one
    // is idempotent, and none deletes: turning a module off leaves its rows alone, and
    // turning it back on restores the baseline over what was kept.
    await SeederModuleCatalog.RunEnabledAsync(
        app.Services, moduleSelection, app.Environment.IsDevelopment(), app.Logger);
    Log.Information(
        "deployment.modules.seeders ran={Ran} of={Total}",
        SeederModuleCatalog.Enabled(moduleSelection, app.Environment.IsDevelopment()).Count,
        SeederModuleCatalog.All.Count);

    // Populate the tool registry (metadata) from the registered handlers.
    using (var toolScope = app.Services.CreateScope())
    {
        var registry = toolScope.ServiceProvider.GetRequiredService<ToolRegistry>();
        foreach (var handlerType in toolHandlerTypes)
            registry.Register((IToolHandler)toolScope.ServiceProvider.GetRequiredService(handlerType));
    }

    Log.Information("nashira backend starting (env={Environment})", app.Environment.EnvironmentName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "nashira backend terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

static async Task ApplyMigrationsWithRetryAsync(WebApplication app, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            return;
        }
        // Only a database that is not answering yet is worth waiting for. A broken
        // migration, or a model that has drifted from its snapshot, fails identically on
        // every attempt: retrying it for the whole timeout buries the real error under a
        // wall of "database not ready", which is untrue and sends whoever reads the log
        // to go and inspect a database that was fine all along.
        catch (Exception ex) when (IsDatabaseUnavailable(ex) && DateTime.UtcNow < deadline)
        {
            app.Logger.LogWarning("db.migrate.retry — database not answering yet ({Reason}), retrying in 3s",
                ex.GetBaseException().Message);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "db.migrate.failed — this will not resolve by waiting");
            throw;
        }
    }
}

// Parse a Serilog LogEventLevel from an env var with a safe default.
// Case-insensitive; empty / whitespace / unrecognised value falls through to
// `fallback` (never throws), so a typo in LOG_ACTIONS cannot silence the logs.
// Consumed by the UseSerilog block at the top of this file.
static LogEventLevel ParseLogLevel(string? raw, LogEventLevel fallback)
{
    if (string.IsNullOrWhiteSpace(raw)) return fallback;
    return Enum.TryParse<LogEventLevel>(raw.Trim(), ignoreCase: true, out var parsed)
        ? parsed
        : fallback;
}

// Connection-level failure, at any depth: Npgsql wraps a refused socket, and a
// container that has not finished starting looks exactly like one that is down.
static bool IsDatabaseUnavailable(Exception ex)
{
    for (Exception? e = ex; e is not null; e = e.InnerException)
    {
        if (e is SocketException or TimeoutException) return true;
        if (e is Npgsql.NpgsqlException { IsTransient: true }) return true;
    }
    return false;
}
