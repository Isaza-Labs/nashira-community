using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Services.Workflow;

/// <summary>
/// The portable, self-contained form of a workflow — what one instance hands to another.
/// Shape: workflow-v1-conformance/bundle/SPEC.md (v3).
/// </summary>
/// <remarks>
/// <para>
/// The plain YAML export carries nodes verbatim, which means it carries nothing but
/// per-instance GUIDs. Handed to a second instance none of them resolve, and because
/// <c>WorkflowReferenceChecker</c> rejects an unresolvable reference at write time, the
/// import is refused outright — every FlowWeaver workflow brought here failed that way.
/// </para>
/// <para>
/// A bundle keeps the nodes verbatim, so a re-import into the SAME instance is exact,
/// and adds a <c>dependencies</c> section giving every referenced snippet a portable
/// identity plus its full definition. The importer resolves those identities locally,
/// creates what is missing from the definition that travelled, and rewrites the GUIDs.
/// The node shape never changes, so there is exactly one place where identity is
/// translated.
/// </para>
/// <para>
/// v3 adds what v2 left implicit: a <c>requires</c> block saying what the importer must
/// support (so a bundle is refused precisely, or degraded with a note, instead of
/// failing on the first run), the sub-workflows a <c>subflow</c> node reaches, the
/// credentials and repositories the nodes name, and the workflow's triggers. v2 is still
/// read; <c>requires</c> is inferred for it.
/// </para>
/// <para>
/// <b>Secrets never travel.</b> An integration, credential or repository is named, never
/// described by its material, and a missing one is a hard error rather than something
/// the import invents — the receiving instance must already hold its own credentials for
/// that system.
/// </para>
/// </remarks>
public sealed class WorkflowBundle
{
    /// <summary>The version this build emits.</summary>
    public const string CurrentSchemaVersion = "v3";

    /// <summary>Versions this build reads. See bundle/SPEC.md §1.</summary>
    public static readonly string[] AcceptedSchemaVersions = ["v2", CurrentSchemaVersion];

    /// <summary>
    /// What this instance stamps on a bundle it exports.
    /// </summary>
    /// <remarks>
    /// Deliberately FlowWeaver's marker rather than a Nashira-specific one. The wire
    /// format is FlowWeaver's and this is an adoption of it, not a fork: emitting a
    /// different name would mean a bundle exported here could not be read there, which
    /// throws away half the interoperability the format exists for. <see
    /// cref="AcceptedKinds"/> also takes a Nashira-branded marker so an instance that
    /// starts emitting one later is still readable by this build.
    /// </remarks>
    public const string KindMarker = "flow_weaver.workflow_bundle";

    /// <summary>Markers this build will parse. See <see cref="KindMarker"/>.</summary>
    public static readonly string[] AcceptedKinds =
        [KindMarker, "nashira.workflow_bundle", "netora.workflow_bundle"];

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindMarker;

    [JsonPropertyName("exported_at")]
    public DateTime ExportedAt { get; set; }

    /// <summary>Informational: which product wrote the file. Never dispatched on.</summary>
    [JsonPropertyName("exported_by")]
    public BundleExporter? ExportedBy { get; set; }

    /// <summary>What the importer must support (bundle/SPEC.md §2).</summary>
    [JsonPropertyName("requires")]
    public BundleRequires Requires { get; set; } = new();

    [JsonPropertyName("workflow")]
    public BundleWorkflow Workflow { get; set; } = new();

    [JsonPropertyName("nodes")]
    public JsonElement Nodes { get; set; }

    [JsonPropertyName("edges")]
    public JsonElement Edges { get; set; }

    [JsonPropertyName("dependencies")]
    public BundleDependencies Dependencies { get; set; } = new();

    /// <summary>The workflow's triggers, without secrets, targets or statistics (§6).</summary>
    [JsonPropertyName("triggers")]
    public List<BundleTrigger> Triggers { get; set; } = [];
}

public sealed class BundleExporter
{
    [JsonPropertyName("product")]
    public string Product { get; set; } = "nashira";

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// The closed capability vocabulary of bundle/SPEC.md §2.2, and which of them this build
/// implements or can degrade with a note.
/// </summary>
public static class BundleCapabilities
{
    public const string Subflow = "subflow";
    public const string TemplateFilters = "template_filters";
    public const string RunNamespace = "run_namespace";
    public const string PerDeviceScope = "per_device_scope";
    public const string MaxParallel = "max_parallel";
    public const string PerPool = "per_pool";
    public const string ConditionalEdges = "conditional_edges";
    public const string PythonNetwork = "python_network";
    public const string Triggers = "triggers";

    /// <summary>Every name the spec defines. Anything else is refused.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Subflow, TemplateFilters, RunNamespace, PerDeviceScope, MaxParallel, PerPool,
        ConditionalEdges, PythonNetwork, Triggers,
    };

    /// <summary>Implemented here: a bundle that lists one of these imports as written.</summary>
    /// <remarks>
    /// <c>subflow</c> was deliberately absent until execution/SPEC.md §5 landed, and the
    /// reason is worth keeping: the import half of §5.4 has always worked — the child
    /// workflows are created and every <c>subflow_workflow_id</c> is remapped — but a
    /// build that cannot EXECUTE a subflow node reported the step <c>skipped</c>, and
    /// because a <c>success</c> edge needs <c>changed|no_change</c>, the whole downstream
    /// half of the graph silently never ran while the run reported <c>completed</c>.
    /// §2.2 marks the capability non-degradable precisely so an importer in that
    /// position refuses instead of importing clean and without even a note. The node now
    /// starts a real child run (<see cref="SubflowRunner"/>), so the refusal has nothing
    /// left to protect and the capability is claimed.
    /// </remarks>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Subflow, TemplateFilters, RunNamespace, PerDeviceScope, ConditionalEdges, Triggers,
    };

    /// <summary>
    /// Not implemented, but the spec allows an importer to read past them with a note:
    /// fan-out width is walked serially, a pool becomes per_device, python network
    /// access is never granted.
    /// </summary>
    public static readonly IReadOnlySet<string> Degradable = new HashSet<string>(StringComparer.Ordinal)
    {
        MaxParallel, PerPool, PythonNetwork,
    };
}

public sealed class BundleRequires
{
    [JsonPropertyName("snippet_types")]
    public List<string> SnippetTypes { get; set; } = [];

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("secrets")]
    public List<BundleSecretRequirement> Secrets { get; set; } = [];
}

/// <summary>
/// A <c>${secret:&lt;source&gt;:&lt;name&gt;:&lt;field&gt;}</c> reference the workflow
/// resolves at run time. Only the reference travels; the importer reports the ones that
/// do not exist locally as notes.
/// </summary>
public sealed class BundleSecretRequirement
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("used_by")]
    public List<string> UsedBy { get; set; } = [];
}

public sealed class BundleWorkflow
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // Exported for information only. An import always lands as a draft — a bundle must
    // never be able to place a workflow straight into production on someone else's
    // instance, and it has never been simulated here.
    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }
}

public sealed class BundleDependencies
{
    [JsonPropertyName("snippets")]
    public List<BundleSnippet> Snippets { get; set; } = [];

    [JsonPropertyName("integrations")]
    public List<BundleIntegration> Integrations { get; set; } = [];

    [JsonPropertyName("mcp_servers")]
    public List<BundleMcpServer> McpServers { get; set; } = [];

    [JsonPropertyName("credentials")]
    public List<BundleCredential> Credentials { get; set; } = [];

    [JsonPropertyName("repositories")]
    public List<BundleRepository> Repositories { get; set; } = [];

    /// <summary>Sub-workflows reachable through <c>subflow</c> nodes, flattened (§5.4).</summary>
    [JsonPropertyName("workflows")]
    public List<BundleSubWorkflow> Workflows { get; set; } = [];
}

/// <summary>
/// A referenced snippet, complete. The definition travels so the receiving instance can
/// recreate it faithfully instead of stubbing it — that is the difference between "the
/// workflow you sent me works" and "here is a placeholder that returns nothing".
/// </summary>
public sealed class BundleSnippet
{
    /// <summary>Source-instance GUID. Used only to remap nodes.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("script_language")]
    public string? ScriptLanguage { get; set; }

    // Carried as JSON values because that is what FlowWeaver emits; Nashira stores them
    // as text, so the reader flattens them on the way in.
    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("output_schema")]
    public JsonElement? OutputSchema { get; set; }

    [JsonPropertyName("target_mode")]
    public string TargetMode { get; set; } = string.Empty;

    /// <summary>
    /// FlowWeaver's per-step fan-out width. Nashira has no such column — a per_device
    /// step walks its targets in order — so this is accepted and dropped rather than
    /// rejected, and the resolution notes say so.
    /// </summary>
    [JsonPropertyName("max_parallel")]
    public int MaxParallel { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; }

    [JsonPropertyName("idempotency")]
    public string? Idempotency { get; set; }

    /// <summary>Canonical shape on the wire (execution/SPEC.md §3). Legacy accepted on read.</summary>
    [JsonPropertyName("retry_policy")]
    public JsonElement? RetryPolicy { get; set; }

    [JsonPropertyName("logic_diagram_mermaid")]
    public string? LogicDiagramMermaid { get; set; }

    // Carried so the importer can REFUSE it rather than silently grant it:
    // network_enabled lifts the python sandbox's network isolation and is admin-only to
    // set locally. A file from another instance must not hand itself that capability.
    [JsonPropertyName("network_enabled")]
    public bool NetworkEnabled { get; set; }
}

/// <summary>
/// A referenced integration, described by identity only — never by credential.
/// </summary>
/// <remarks>
/// Nashira's nodes name their integration as a string, so this section is a manifest
/// rather than a remapping table: the importer checks each entry exists locally and
/// refuses the bundle when one does not. <c>base_url</c> is informational, so a human
/// can confirm both sides mean the same system. The <c>id</c> is what lets a legacy
/// <c>integration_id</c> in a foreign node be translated to the slug (bundle/SPEC.md §4).
/// </remarks>
public sealed class BundleIntegration
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("actions")]
    public List<BundleAction> Actions { get; set; } = [];
}

/// <summary>An action, identified by its owning integration plus its name.</summary>
public sealed class BundleAction
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>An MCP server an mcp_call step names. Identity only; no transport config.</summary>
public sealed class BundleMcpServer
{
    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("transport")]
    public string? Transport { get; set; }
}

/// <summary>
/// A credential a node names. Identity only: name, context type, auth method and the
/// username — never a password, key, passphrase or token.
/// </summary>
public sealed class BundleCredential
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("auth_method")]
    public string? AuthMethod { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

/// <summary>A git repository a git step names. Identity only; no auth credential.</summary>
public sealed class BundleRepository
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; set; }
}

/// <summary>
/// A sub-workflow, complete: what a <c>subflow</c> node runs. Keyed by its source id so
/// <c>subflow_workflow_id</c> can be remapped after the row is created here.
/// </summary>
public sealed class BundleSubWorkflow
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }

    [JsonPropertyName("nodes")]
    public JsonElement Nodes { get; set; }

    [JsonPropertyName("edges")]
    public JsonElement Edges { get; set; }
}

/// <summary>
/// A trigger as it travels (bundle/SPEC.md §6): schedule or route, never the HMAC
/// secret, the target devices (local ids) or the run statistics.
/// </summary>
public sealed class BundleTrigger
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("cron_expression")]
    public string? CronExpression { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("route")]
    public string? Route { get; set; }

    [JsonPropertyName("allow_unsigned")]
    public bool? AllowUnsigned { get; set; }

    [JsonPropertyName("allow_target_override")]
    public bool? AllowTargetOverride { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("input_defaults")]
    public JsonElement? InputDefaults { get; set; }

    // Informational: every trigger is created disabled on import regardless.
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("notify_on")]
    public List<string>? NotifyOn { get; set; }

    [JsonPropertyName("notification_webhook_url")]
    public string? NotificationWebhookUrl { get; set; }
}
