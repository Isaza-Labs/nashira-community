using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Scheduler;
using nashira_backend.Services.Security;
using nashira_backend.Services.Worker;
using SnippetEntity = nashira_backend.Data.Models.Snippet;
using TriggerEntity = nashira_backend.Data.Models.WorkflowTrigger;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Workflow;

public interface IWorkflowBundleService
{
    /// <summary>Builds the portable bundle for a workflow (v3).</summary>
    Task<WorkflowBundle> BuildAsync(Guid workflowId, CancellationToken ct);

    /// <summary>
    /// Resolves a bundle against THIS instance: what every reference maps to, the
    /// snippet rows that have to be created, and the notes an operator needs to read.
    /// Throws <see cref="ValidationException"/> listing everything that cannot be
    /// resolved — at once — rather than substituting anything.
    /// </summary>
    Task<BundleResolution> ResolveAsync(WorkflowBundle bundle, CancellationToken ct);

    /// <summary>
    /// Resolves and then creates everything the bundle carries — snippets,
    /// sub-workflows, the workflow, its triggers — in one transaction. The workflow
    /// always lands in draft, version 1.
    /// </summary>
    Task<BundleImportResult> ImportAsync(
        WorkflowBundle bundle, string? nameOverride, string? changeSummary, CancellationToken ct);
}

/// <summary>
/// What a bundle's dependencies map to locally: every source snippet GUID paired with
/// the local GUID that replaces it, the snippets that do not exist here yet, the node
/// rewrite plan (names → local rows), the nodes with legacy keys already translated,
/// and the things an operator should be told about the translation.
/// </summary>
public sealed record BundleResolution(
    IReadOnlyDictionary<Guid, Guid> IdMap,
    IReadOnlyList<SnippetEntity> SnippetsToCreate,
    IReadOnlyList<string> Notes,
    NodeReferences.RewritePlan Plan,
    JsonElement RootNodes,
    IReadOnlyDictionary<Guid, JsonElement> SubWorkflowNodes,
    IReadOnlyList<BundleSubWorkflow> CreationOrder,
    BundleRequires Requires);

public sealed record BundleImportResult(
    WorkflowEntity Workflow,
    IReadOnlyList<string> Notes,
    IReadOnlyList<SnippetEntity> CreatedSnippets,
    IReadOnlyList<WorkflowEntity> CreatedWorkflows,
    IReadOnlyList<TriggerEntity> CreatedTriggers);

/// <summary>
/// Export, dependency resolution and import for the portable bundle format
/// (workflow-v1-conformance/bundle/SPEC.md).
/// </summary>
/// <remarks>
/// A bundle from another workflow.v1 implementation is not a foreign format to be
/// guessed at: it resolves deterministically or it fails with a list. Nothing here
/// scores name similarity and nothing fabricates a placeholder — a stub that returns
/// nothing turns "the workflow my colleague sent me" into a run that goes green having
/// done no work, which is the failure mode this whole format exists to remove.
/// </remarks>
public sealed class WorkflowBundleService : IWorkflowBundleService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISnippetHandlerRegistry _registry;
    private readonly WorkflowValidator _validator;
    private readonly WorkflowReferenceChecker _references;
    private readonly ISecretProtector _protector;
    private readonly ILogger<WorkflowBundleService> _logger;

    public WorkflowBundleService(
        AppDbContext db, ICurrentUser user, ISnippetHandlerRegistry registry,
        WorkflowValidator validator, WorkflowReferenceChecker references, ISecretProtector protector,
        ILogger<WorkflowBundleService> logger)
    {
        _db = db;
        _user = user;
        _registry = registry;
        _validator = validator;
        _references = references;
        _protector = protector;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // export
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<WorkflowBundle> BuildAsync(Guid workflowId, CancellationToken ct)
    {
        var wf = await _db.Workflows.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkflowId == workflowId && w.IsActive, ct)
            ?? throw new NotFoundException("workflow not found");

        // ── flatten the sub-workflows (§5.4) ─────────────────────────────
        // Breadth-first over `subflow` nodes, each workflow once. A cycle is detected
        // on the assembled bundle by the same helper the importer uses, so both sides
        // refuse the same shape.
        var graphs = new List<(Guid Id, JsonElement Nodes, JsonElement Edges)>();
        var rootNodes = ParseArray(wf.NodesJson);
        var rootEdges = ParseArray(wf.EdgesJson);
        graphs.Add((wf.WorkflowId, rootNodes, rootEdges));

        var subWorkflows = new List<BundleSubWorkflow>();
        var seen = new HashSet<Guid> { wf.WorkflowId };
        var queue = new Queue<Guid>(NodeReferences.Extract(rootNodes).SubflowWorkflowIds);
        while (queue.Count > 0)
        {
            var childId = queue.Dequeue();
            if (!seen.Add(childId)) continue;
            var child = await _db.Workflows.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkflowId == childId && w.IsActive, ct);
            // Not carried → BundleSubWorkflows.CreationOrder refuses below, naming it.
            if (child is null) continue;

            var childNodes = ParseArray(child.NodesJson);
            var childEdges = ParseArray(child.EdgesJson);
            graphs.Add((child.WorkflowId, childNodes, childEdges));
            subWorkflows.Add(new BundleSubWorkflow
            {
                Id = child.WorkflowId,
                Name = child.Name,
                Description = child.Description,
                InputSchema = ParseOrNull(child.InputSchemaJson),
                Metadata = ParseOrNull(child.MetadataJson),
                Nodes = childNodes,
                Edges = childEdges,
            });
            foreach (var grandchild in NodeReferences.Extract(childNodes).SubflowWorkflowIds)
                queue.Enqueue(grandchild);
        }

        // ── everything the graphs point at, merged ────────────────────────
        var refs = graphs.Select(g => NodeReferences.Extract(g.Nodes)).ToList();
        var snippetIds = refs.SelectMany(r => r.SnippetIds).Distinct().ToList();
        var integrationNames = refs.SelectMany(r => r.IntegrationNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var serverNames = refs.SelectMany(r => r.McpServerNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var credentialIds = refs.SelectMany(r => r.CredentialIds).Distinct().ToList();
        var credentialNames = refs.SelectMany(r => r.CredentialNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var repositoryIds = refs.SelectMany(r => r.RepositoryIds).Distinct().ToList();
        var repositoryNames = refs.SelectMany(r => r.RepositoryNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var snippets = await _db.Snippets.AsNoTracking()
            .Where(s => snippetIds.Contains(s.SnippetId))
            .ToListAsync(ct);

        // Name or slug: a node authored here names its integration, one that arrived in
        // a bundle carries the slug the exporter wrote (§4). Both have to find the row.
        var integrations = await _db.Integrations.AsNoTracking()
            .Where(i => i.IsActive && (integrationNames.Contains(i.Name) || integrationNames.Contains(i.Slug)))
            .ToListAsync(ct);

        var bundleIntegrations = new List<BundleIntegration>();
        foreach (var integration in integrations)
        {
            // Only the actions this integration owns, named. Shipping the whole
            // catalogue would bloat the file and describe systems the recipient has no
            // business knowing the shape of.
            var actions = await _db.IntegrationActions.AsNoTracking()
                .Where(a => a.IntegrationId == integration.IntegrationId && a.Enabled)
                .Select(a => new BundleAction
                {
                    Id = a.IntegrationActionId,
                    Name = a.Name,
                    Method = a.Method,
                    Path = a.Path,
                })
                .ToListAsync(ct);

            bundleIntegrations.Add(new BundleIntegration
            {
                Id = integration.IntegrationId,
                Slug = integration.Slug,
                Name = integration.Name,
                Type = integration.Type,
                BaseUrl = integration.BaseUrl,
                Actions = actions,
                // AuthConfig / HeadersJson / AuthCredentialId are deliberately absent.
                // Credentials do not travel.
            });
        }

        var mcpServers = await _db.McpServers.AsNoTracking()
            .Where(m => m.IsActive && serverNames.Contains(m.Name))
            .Select(m => new BundleMcpServer { Id = m.McpServerId, Name = m.Name, Transport = m.Transport })
            .ToListAsync(ct);

        // Identity only (§5.3): the name a node uses to find it, plus what a human
        // needs to recognise which one to create. No material.
        var credentials = await _db.Credentials.AsNoTracking()
            .Where(c => c.IsActive && (credentialIds.Contains(c.CredentialId) || credentialNames.Contains(c.Name)))
            .Select(c => new BundleCredential
            {
                Id = c.CredentialId, Name = c.Name, Type = c.Type, AuthMethod = c.AuthMethod, Username = c.Username,
            })
            .ToListAsync(ct);

        var repositories = await _db.GitRepositories.AsNoTracking()
            .Where(r => r.IsActive && (repositoryIds.Contains(r.GitRepositoryId) || repositoryNames.Contains(r.Name)))
            .Select(r => new BundleRepository { Id = r.GitRepositoryId, Name = r.Name, RemoteUrl = r.Url })
            .ToListAsync(ct);

        // §4: the canonical key is the name, and it travels ALONE — the local id is
        // dropped on the way out. Node objects are hashed, and a GUID that means
        // something only here would make that fingerprint instance-specific.
        var credentialNamesById = credentials.ToDictionary(c => c.Id, c => c.Name);
        var repositoryNamesById = repositories.ToDictionary(r => r.Id, r => r.Name);
        // Whatever a node currently calls an integration → its slug, the canonical value.
        var integrationSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in integrations)
        {
            if (string.IsNullOrWhiteSpace(i.Slug)) continue;
            integrationSlugs[i.Name] = i.Slug;
            integrationSlugs[i.Slug] = i.Slug;
        }
        rootNodes = NodeReferences.WithPortableKeys(rootNodes, credentialNamesById, repositoryNamesById, integrationSlugs);
        foreach (var sub in subWorkflows)
            sub.Nodes = NodeReferences.WithPortableKeys(
                sub.Nodes, credentialNamesById, repositoryNamesById, integrationSlugs);

        // ── triggers (§6): schedule or route, never the secret or the targets ──
        var triggers = await _db.WorkflowTriggers.AsNoTracking()
            .Where(t => t.WorkflowId == workflowId && t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var bundle = new WorkflowBundle
        {
            ExportedAt = DateTime.UtcNow,
            ExportedBy = new BundleExporter
            {
                Product = "nashira",
                Version = typeof(WorkflowBundleService).Assembly.GetName().Version?.ToString(3),
            },
            Workflow = new BundleWorkflow
            {
                Name = wf.Name,
                Description = wf.Description,
                Environment = wf.Environment,
                InputSchema = ParseOrNull(wf.InputSchemaJson),
                Metadata = ParseOrNull(wf.MetadataJson),
            },
            Nodes = rootNodes,
            Edges = rootEdges,
            Dependencies = new BundleDependencies
            {
                Snippets = snippets.Select(ToBundleSnippet).ToList(),
                Integrations = bundleIntegrations,
                McpServers = mcpServers,
                Credentials = credentials,
                Repositories = repositories,
                Workflows = subWorkflows,
            },
            Triggers = triggers.Select(ToBundleTrigger).ToList(),
        };

        // A bundle gets committed to git repos and reviewed in pull requests, so the
        // plain-secret gate the importer applies has to run on the way OUT as well —
        // nothing rejects a plain `config_overrides.password` at write time, and
        // without this the export is where it escapes. Same rule, same key list, same
        // refusal as the import side (snippets/SPEC.md, `ssh`).
        var typesBySnippetId = snippets.ToDictionary(sn => sn.SnippetId, sn => sn.Type ?? string.Empty);
        var plain = new List<string>();
        foreach (var g in graphs) CheckPlainSecrets(g.Nodes, typesBySnippetId, plain);
        if (plain.Count > 0)
        {
            _logger.LogWarning(
                "workflow.bundle.export_refused workflow_id={WorkflowId} nodes={Count}", workflowId, plain.Count);
            throw new ValidationException(
                "this workflow cannot be exported: " + string.Join("; ", plain)
                + ". Move the value into a secret, reference it as "
                + "${secret:<source>:<name>:<field>}, and export again.",
                "bundle_reference_untranslatable");
        }

        // Refuses a cycle or a dangling subflow_workflow_id before the file exists —
        // an incomplete bundle must never leave this instance. The root's own id is
        // passed so a child that reaches back to it is a cycle, not a missing workflow.
        BundleSubWorkflows.CreationOrder(bundle, wf.WorkflowId);
        bundle.Requires = BundleRequirements.Compute(bundle);

        _logger.LogInformation(
            "workflow.bundle.built workflow_id={WorkflowId} snippets={Snippets} integrations={Integrations} "
            + "subflows={Subflows} triggers={Triggers} capabilities={Capabilities}",
            workflowId, snippets.Count, bundleIntegrations.Count, subWorkflows.Count, triggers.Count,
            string.Join(",", bundle.Requires.Capabilities));

        return bundle;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // resolution
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<BundleResolution> ResolveAsync(WorkflowBundle bundle, CancellationToken ct)
    {
        var idMap = new Dictionary<Guid, Guid>();
        var toCreate = new List<SnippetEntity>();
        var notes = new List<string>();
        var missing = new List<string>();
        var unsupported = new List<string>();
        var untranslatable = new List<string>();

        // Declared ∪ visible. A v2 bundle declared nothing and got everything inferred
        // by the reader; a v3 one is cross-checked against what it carries.
        var visible = BundleRequirements.Compute(bundle);
        var requires = BundleRequirements.Merge(bundle.Requires, visible);

        // ── capabilities (§2.2) ─────────────────────────────────────────
        foreach (var capability in requires.Capabilities)
        {
            if (BundleCapabilities.Supported.Contains(capability)) continue;
            if (BundleCapabilities.Degradable.Contains(capability))
            {
                // The snippet-level notes below say precisely what was degraded and
                // where; this one only covers a declaration nothing visible backs.
                if (!visible.Capabilities.Contains(capability))
                    notes.Add(
                        $"the bundle declares capability '{capability}', which this build does not implement; "
                        + "it is imported degraded (see the notes on the affected snippets).");
                continue;
            }
            unsupported.Add(capability);
        }

        // ── legacy reference keys (§4) ──────────────────────────────────
        var rootNodes = NodeReferences.TranslateLegacyKeys(bundle.Nodes, bundle.Dependencies, untranslatable);
        var subNodes = new Dictionary<Guid, JsonElement>();
        foreach (var sub in bundle.Dependencies.Workflows)
            subNodes[sub.Id] = NodeReferences.TranslateLegacyKeys(sub.Nodes, bundle.Dependencies, untranslatable);

        var graphs = subNodes.Values.Prepend(rootNodes).ToList();
        var refs = graphs.Select(NodeReferences.Extract).ToList();
        var snippetsById = bundle.Dependencies.Snippets.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());

        // ── per-node contract checks (snippets/SPEC.md rule 3, ssh rule) ──
        foreach (var nodes in graphs)
            CheckNodeKeys(nodes, snippetsById, notes, untranslatable);

        // ── integrations ────────────────────────────────────────────────
        // Resolved by slug first, then by exact name. Unresolved is fatal: this
        // instance would need credentials for a system it does not know, and inventing
        // the row would produce a workflow that looks wired and fails on a device.
        var integrationRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var integrationsChecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wanted in bundle.Dependencies.Integrations)
        {
            integrationsChecked.Add(wanted.Name);
            if (!string.IsNullOrWhiteSpace(wanted.Slug)) integrationsChecked.Add(wanted.Slug);

            var local = await ResolveIntegrationAsync(wanted.Slug, wanted.Name, ct);
            if (local is null)
            {
                missing.Add(
                    $"integration '{wanted.Name}' (type '{wanted.Type}')"
                    + (string.IsNullOrWhiteSpace(wanted.BaseUrl) ? "" : $", source base_url {wanted.BaseUrl}"));
                continue;
            }

            // Found by slug under another name (§5.3): usable, because the node is
            // rewritten to the local name — this build's handler resolves by name —
            // and the operator is told.
            // Ordinal, not case-insensitive: this build's integration_action handler
            // resolves by exact name, so a node saying 'netbox' does not find 'NetBox'
            // and the slug the exporter wrote has to be rewritten to the local name.
            foreach (var written in new[] { wanted.Name, wanted.Slug })
            {
                if (string.IsNullOrWhiteSpace(written)) continue;
                if (string.Equals(written, local.Name, StringComparison.Ordinal)) continue;
                integrationRenames[written] = local.Name;
            }
            if (!string.Equals(local.Name, wanted.Name, StringComparison.OrdinalIgnoreCase))
                notes.Add(
                    $"integration '{wanted.Name}' matched the local integration '{local.Name}' by slug "
                    + $"('{local.Slug}'); the nodes now name '{local.Name}'. Confirm it is the same system.");

            foreach (var action in wanted.Actions)
            {
                var exists = await _db.IntegrationActions.AsNoTracking().AnyAsync(
                    a => a.IntegrationId == local.IntegrationId && a.Enabled
                         && (a.Name == action.Name || a.OperationId == action.Name), ct);
                if (exists) continue;

                // The integration is here but this operation is not — usually the local
                // spec is older. Naming the action is what lets the operator fix it by
                // re-importing the spec.
                missing.Add(
                    $"action '{action.Name}' on integration '{local.Name}'"
                    + (string.IsNullOrWhiteSpace(action.Method) ? "" : $" ({action.Method} {action.Path})"));
            }
        }
        // Names the nodes use that the manifest did not list (a hand-edited bundle, or
        // a v2 one from a build that only listed what it could see).
        foreach (var name in refs.SelectMany(r => r.IntegrationNames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (integrationsChecked.Contains(name)) continue;
            var local = await ResolveIntegrationAsync(name, name, ct);
            if (local is null) missing.Add($"integration '{name}' (named by a node)");
            else if (!string.Equals(local.Name, name, StringComparison.Ordinal))
                integrationRenames[name] = local.Name;
        }

        // ── MCP servers ─────────────────────────────────────────────────
        var serverNames = bundle.Dependencies.McpServers.Select(m => m.Name)
            .Concat(refs.SelectMany(r => r.McpServerNames))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in serverNames)
        {
            var exists = await _db.McpServers.AsNoTracking()
                .AnyAsync(m => m.IsActive && m.Name.ToLower() == name.ToLower(), ct);
            if (!exists) missing.Add($"MCP server '{name}'");
        }

        // ── credentials (§5.3) ──────────────────────────────────────────
        var credentialIdsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var wantedCredentials = bundle.Dependencies.Credentials
            .Select(c => (c.Name, Describe: $"credential '{c.Name}'"
                + (string.IsNullOrWhiteSpace(c.Type) ? "" : $" (type '{c.Type}'")
                + (string.IsNullOrWhiteSpace(c.AuthMethod) ? (string.IsNullOrWhiteSpace(c.Type) ? "" : ")") : $", auth {c.AuthMethod})")))
            .Concat(refs.SelectMany(r => r.CredentialNames).Select(n => (Name: n, Describe: $"credential '{n}' (named by a node)")))
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        foreach (var (name, describe) in wantedCredentials)
        {
            var local = await _db.Credentials.AsNoTracking()
                .FirstOrDefaultAsync(c => c.IsActive && c.Name.ToLower() == name.ToLower(), ct);
            if (local is null) missing.Add(describe);
            else credentialIdsByName[name] = local.CredentialId;
        }

        // ── repositories (§5.3) ─────────────────────────────────────────
        var repositoryIdsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var wantedRepositories = bundle.Dependencies.Repositories
            .Select(r => (r.Name, Describe: $"git repository '{r.Name}'"
                + (string.IsNullOrWhiteSpace(r.RemoteUrl) ? "" : $" (remote {r.RemoteUrl})")))
            .Concat(refs.SelectMany(r => r.RepositoryNames).Select(n => (Name: n, Describe: $"git repository '{n}' (named by a node)")))
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        foreach (var (name, describe) in wantedRepositories)
        {
            var local = await _db.GitRepositories.AsNoTracking()
                .FirstOrDefaultAsync(r => r.IsActive && r.Name.ToLower() == name.ToLower(), ct);
            if (local is null) missing.Add(describe);
            else repositoryIdsByName[name] = local.GitRepositoryId;
        }

        // ── secrets (§2.4): notes, never refusals ────────────────────────
        foreach (var required in requires.Secrets)
        {
            var parsed = SecretResolver.References(required.Ref).FirstOrDefault();
            if (parsed is null)
            {
                notes.Add($"requires.secrets lists '{required.Ref}', which is not a ${{secret:…}} reference.");
                continue;
            }
            if (await SecretExistsAsync(parsed, ct)) continue;
            var usedBy = required.UsedBy.Count > 0 ? $" (used by {string.Join(", ", required.UsedBy)})" : "";
            notes.Add(
                $"secret reference {required.Ref}{usedBy} does not resolve on this instance. "
                + "Create it before the first run; the reference was kept verbatim.");
        }

        // ── snippets ────────────────────────────────────────────────────
        var taken = new HashSet<string>(
            await _db.Snippets.Select(s => s.Slug).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);
        var takenNames = new HashSet<string>(
            await _db.Snippets.Where(s => s.IsActive).Select(s => s.Name).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);
        var known = _registry.KnownTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var wanted in bundle.Dependencies.Snippets)
        {
            // A type with no handler here cannot run, and storing it would defer the
            // discovery to a device. FlowWeaver ships handlers this build does not
            // (ansible, netconf, snmp_v3, …), so this is the common case for a bundle
            // crossing between them and it deserves a precise refusal.
            var type = (wanted.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (!known.Contains(type))
            {
                missing.Add(
                    $"snippet '{wanted.Name}' needs handler type '{type}', which this build cannot "
                    + $"execute (available: {string.Join(", ", known.OrderBy(t => t, StringComparer.Ordinal))})");
                continue;
            }

            var local = await ResolveSnippetAsync(wanted, ct);
            if (local is not null)
            {
                idMap[wanted.Id] = local.SnippetId;
                if (!string.Equals(local.Slug, wanted.Slug, StringComparison.OrdinalIgnoreCase))
                    notes.Add(
                        $"snippet '{wanted.Name}' matched an existing local snippet by name, not by slug — "
                        + "confirm it does the same thing.");
                continue;
            }

            var created = FromBundle(wanted, type, taken, takenNames);
            taken.Add(created.Slug);
            takenNames.Add(created.Name);
            toCreate.Add(created);
            idMap[wanted.Id] = created.SnippetId;

            if (wanted.NetworkEnabled)
                notes.Add(
                    $"snippet '{wanted.Name}' was network-enabled on the source instance; it was created "
                    + "WITHOUT that flag. An admin must re-enable it here after reviewing the code.");

            if (wanted.MaxParallel > 1)
                notes.Add(
                    $"snippet '{wanted.Name}' declared max_parallel={wanted.MaxParallel} on the source "
                    + "instance. This build has no per-step fan-out width; its devices are walked in order.");

            if (string.Equals(wanted.TargetMode?.Trim(), "per_pool", StringComparison.OrdinalIgnoreCase))
                notes.Add(
                    $"snippet '{wanted.Name}' used target_mode 'per_pool', which this build does not have. "
                    + "It was created as 'per_device', so it fans out over the run's target devices "
                    + "instead of a pool — check that is what the workflow means.");
        }

        // Required types the manifest names but no carried snippet uses (declared only).
        foreach (var type in requires.SnippetTypes.Where(t => !known.Contains(t)))
            if (!bundle.Dependencies.Snippets.Any(s => string.Equals(s.Type?.Trim(), type, StringComparison.OrdinalIgnoreCase)))
                missing.Add($"handler type '{type}' (declared in requires.snippet_types), which this build cannot execute");

        // ── one refusal naming everything (§7) ──────────────────────────
        Refuse(bundle, missing, unsupported, untranslatable);

        var plan = new NodeReferences.RewritePlan
        {
            SnippetIds = idMap,
            CredentialIdsByName = credentialIdsByName,
            RepositoryIdsByName = repositoryIdsByName,
            IntegrationNames = integrationRenames,
        };

        return new BundleResolution(
            idMap, toCreate, notes, plan, rootNodes, subNodes,
            BundleSubWorkflows.CreationOrder(bundle), requires);
    }

    // The three refusal families, aggregated: an operator fixing an import wants the
    // whole list, not the first item of it. One exception carries one code, so the
    // most structural family names it and the message carries every section.
    private void Refuse(
        WorkflowBundle bundle, List<string> missing, List<string> unsupported, List<string> untranslatable)
    {
        if (missing.Count == 0 && unsupported.Count == 0 && untranslatable.Count == 0) return;

        var sections = new List<string>();
        string code;
        if (unsupported.Count > 0)
        {
            code = "bundle_capability_unsupported";
            sections.Add(
                $"[bundle_capability_unsupported] the workflow needs capabilities this instance does not "
                + $"implement: {string.Join(", ", unsupported)}");
        }
        else code = untranslatable.Count > 0 ? "bundle_reference_untranslatable" : "bundle_dependencies_missing";

        if (untranslatable.Count > 0)
            sections.Add(
                "[bundle_reference_untranslatable] references that cannot be translated to a local identity: "
                + string.Join("; ", untranslatable));
        if (missing.Count > 0)
            sections.Add(
                "[bundle_dependencies_missing] this instance is missing dependencies the workflow needs: "
                + string.Join("; ", missing)
                + ". Create them here (with their own credentials) and import again");

        _logger.LogWarning(
            "workflow.bundle.refused code={Code} missing={Missing} unsupported={Unsupported} untranslatable={Untranslatable} workflow={Workflow}",
            code, missing.Count, unsupported.Count, untranslatable.Count, bundle.Workflow.Name);

        throw new ValidationException(string.Join(". ", sections) + ".", code);
    }

    // snippets/SPEC.md rule 3 (unknown keys are noted per node) and the ssh rule
    // (plain credential material is refused, a ${secret:…} reference is kept).
    private static void CheckNodeKeys(
        JsonElement nodes, IReadOnlyDictionary<Guid, BundleSnippet> snippets,
        List<string> notes, List<string> untranslatable)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            // A sentinel subflow node's extra keys are the child's input, by contract
            // (§5). Deliberately the sentinel and not IsSubflow: a node that only wears
            // type "subflow" while naming a real snippet must still be checked, or the
            // type field becomes a way around every gate below.
            if (NodeReferences.IsSubflowSentinel(node)) continue;
            if (!node.TryGetProperty("snippet_id", out var sid) || sid.ValueKind != JsonValueKind.String) continue;
            if (!Guid.TryParse(sid.GetString(), out var snippetId) || !snippets.TryGetValue(snippetId, out var snippet)) continue;
            if (!node.TryGetProperty("config_overrides", out var overrides) || overrides.ValueKind != JsonValueKind.Object) continue;

            var nodeId = NodeReferences.NodeId(node);
            var type = (snippet.Type ?? string.Empty).Trim().ToLowerInvariant();
            var keys = overrides.EnumerateObject().Select(p => p.Name).ToList();

            var unknown = SnippetKeyCatalog.UnknownKeys(type, keys);
            if (unknown.Count > 0)
                notes.Add(
                    $"node '{nodeId}' ({type}) carries keys the {type} contract does not define: "
                    + $"{string.Join(", ", unknown)}. The handler ignores what it does not know — check for typos.");

            if (keys.Contains(SnippetKeyCatalog.IdempotencyOverride, StringComparer.Ordinal))
                notes.Add(
                    $"node '{nodeId}' overrides idempotency in config_overrides; that can only make the tier "
                    + "stricter than the snippet's own declaration, never weaker.");

            // A bundled node must not be able to weaken this instance's outbound URL
            // guard. `allow_private_network` is in neither snippets/SPEC.md nor the key
            // catalogue, and the raw `rest_call` handler reads it straight off the
            // payload — so a file from elsewhere could switch off the private-network
            // block with nothing but an "unknown key" note to show for it. Refused
            // rather than quietly ignored: dropping it would import a workflow that
            // does not do what its own definition says, and whether this instance may
            // reach an internal host is a decision it makes on an integration row it
            // owns, never one an incoming file makes for it.
            if (type == SnippetEntity.TypeRestCall && overrides.TryGetProperty("allow_private_network", out _))
                untranslatable.Add(
                    $"node '{nodeId}' sets 'allow_private_network', which is not part of the rest_call "
                    + "contract and would relax this instance's outbound URL guard. Remove it and reach "
                    + "internal hosts through a catalogued integration");

            CheckPlainSecretsOnNode(nodeId, type, overrides, untranslatable);
        }
    }

    // snippets/SPEC.md, `ssh`: `password` / `private_key` / `key_passphrase` travel
    // only as a `${secret:…}` reference. Run in BOTH directions — an export that wrote
    // a plaintext password into a file bound for a pull request is the same leak as an
    // import that accepted one.
    private static void CheckPlainSecrets(
        JsonElement nodes, IReadOnlyDictionary<Guid, string> snippetTypes, List<string> sink)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (NodeReferences.IsSubflowSentinel(node)) continue;
            if (!node.TryGetProperty("snippet_id", out var sid) || sid.ValueKind != JsonValueKind.String) continue;
            if (!Guid.TryParse(sid.GetString(), out var snippetId)
                || !snippetTypes.TryGetValue(snippetId, out var rawType)) continue;
            if (!node.TryGetProperty("config_overrides", out var overrides)
                || overrides.ValueKind != JsonValueKind.Object) continue;

            CheckPlainSecretsOnNode(
                NodeReferences.NodeId(node), (rawType ?? string.Empty).Trim().ToLowerInvariant(),
                overrides, sink);
        }
    }

    private static void CheckPlainSecretsOnNode(
        string nodeId, string type, JsonElement overrides, List<string> sink)
    {
        if (type != SnippetEntity.TypeSsh) return;
        foreach (var key in SnippetKeyCatalog.SshSecretKeys)
        {
            if (!overrides.TryGetProperty(key, out var value)) continue;
            var ok = value.ValueKind == JsonValueKind.String && SecretResolver.IsSecretReference(value.GetString());
            if (!ok)
                sink.Add(
                    $"node '{nodeId}' key '{key}' carries a plain value; ssh material only travels as a "
                    + "${secret:<source>:<name>:<field>} reference");
        }
    }

    // Existence only — never a decryption. Same source vocabulary as SecretResolver;
    // `session` is per-request and always present.
    private async Task<bool> SecretExistsAsync(SecretResolver.SecretReference r, CancellationToken ct)
    {
        var key = r.IdOrName.Trim();
        var isGuid = Guid.TryParse(key, out var id);
        return r.Source.Trim().ToLowerInvariant() switch
        {
            "secret" => isGuid
                ? await _db.Secrets.AsNoTracking().AnyAsync(s => s.IsActive && s.SecretId == id, ct)
                : await _db.Secrets.AsNoTracking().AnyAsync(s => s.IsActive && s.Name == key, ct),
            "credential" => isGuid
                ? await _db.Credentials.AsNoTracking().AnyAsync(c => c.IsActive && c.CredentialId == id, ct)
                : await _db.Credentials.AsNoTracking().AnyAsync(c => c.IsActive && c.Name == key, ct),
            "ai_provider" => isGuid
                ? await _db.AIProviders.AsNoTracking().AnyAsync(p => p.IsActive && p.AIProviderId == id, ct)
                : await _db.AIProviders.AsNoTracking().AnyAsync(p => p.IsActive && p.Name == key, ct),
            "integration" => isGuid
                ? await _db.Integrations.AsNoTracking().AnyAsync(i => i.IsActive && i.IntegrationId == id, ct)
                : await _db.Integrations.AsNoTracking().AnyAsync(i => i.IsActive && i.Name == key, ct),
            "session" => true,
            _ => false,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // import
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<BundleImportResult> ImportAsync(
        WorkflowBundle bundle, string? nameOverride, string? changeSummary, CancellationToken ct)
    {
        var resolution = await ResolveAsync(bundle, ct);
        var notes = new List<string>(resolution.Notes);
        var now = DateTime.UtcNow;
        var createdBy = _user.IsAuthenticated ? _user.UserId : (Guid?)null;

        // Everything or nothing (§7). The in-memory provider used by the unit tests
        // has no transactions; a relational store gets one.
        await using var tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        foreach (var snippet in resolution.SnippetsToCreate)
            _db.Snippets.Add(snippet);
        // The remapped nodes point at rows that are added but not yet saved, so the
        // reference gate has to run against the same context that holds them.
        await _db.SaveChangesAsync(ct);

        // ── sub-workflows first (§5.4), children before parents ──────────
        var workflowIds = new Dictionary<Guid, Guid>();
        var createdWorkflows = new List<WorkflowEntity>();
        foreach (var sub in resolution.CreationOrder)
        {
            var plan = WithWorkflowIds(resolution.Plan, workflowIds);
            var nodes = NodeReferences.Rewrite(resolution.SubWorkflowNodes[sub.Id], plan);
            var edges = sub.Edges;
            var hash = _validator.ValidateAndHash(nodes, edges);
            await _references.EnsureResolvableAsync(nodes, ct);

            // Reuse only on an exact name match AND the same canonical graph: the same
            // name over a different graph is a different workflow that happens to be
            // called the same thing, and binding to it would run the wrong steps.
            var existing = await _db.Workflows.AsNoTracking()
                .Where(w => w.IsActive && w.Name.ToLower() == sub.Name.ToLower() && w.SchemaHash == hash)
                .OrderByDescending(w => w.Environment == WorkflowEntity.EnvDraft)
                .ThenByDescending(w => w.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                workflowIds[sub.Id] = existing.WorkflowId;
                notes.Add(
                    $"sub-workflow '{sub.Name}' matched the existing local workflow {existing.WorkflowId} "
                    + $"({existing.Environment}, identical graph) and was reused.");
                continue;
            }

            var row = new WorkflowEntity
            {
                WorkflowId = Guid.NewGuid(),
                Name = sub.Name,
                Description = sub.Description,
                Version = 1,
                SchemaVersion = "v1",
                NodesJson = nodes.GetRawText(),
                EdgesJson = edges.GetRawText(),
                InputSchemaJson = sub.InputSchema?.GetRawText(),
                MetadataJson = WithIsSubflow(sub.Metadata),
                Environment = WorkflowEntity.EnvDraft,
                SchemaHash = hash,
                LastSimulationId = null,
                ChangeSummary = $"imported from bundle as a sub-workflow of '{bundle.Workflow.Name}'",
                CreatedByUserId = createdBy,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Workflows.Add(row);
            createdWorkflows.Add(row);
            workflowIds[sub.Id] = row.WorkflowId;
        }

        // ── the workflow itself ─────────────────────────────────────────
        var rootPlan = WithWorkflowIds(resolution.Plan, workflowIds);
        var rootNodes = NodeReferences.Rewrite(resolution.RootNodes, rootPlan);
        var rootEdges = bundle.Edges;
        var rootHash = _validator.ValidateAndHash(rootNodes, rootEdges);
        await _references.EnsureResolvableAsync(rootNodes, ct);

        var name = string.IsNullOrWhiteSpace(nameOverride) ? bundle.Workflow.Name : nameOverride.Trim();
        var workflow = new WorkflowEntity
        {
            WorkflowId = Guid.NewGuid(),
            Name = name,
            Description = bundle.Workflow.Description,
            Version = 1,
            SchemaVersion = "v1",
            NodesJson = rootNodes.GetRawText(),
            EdgesJson = rootEdges.GetRawText(),
            InputSchemaJson = bundle.Workflow.InputSchema?.GetRawText(),
            MetadataJson = bundle.Workflow.Metadata?.GetRawText(),
            // Always draft. The bundle's own environment is informational — a file must
            // never be able to place a workflow straight into production here, and it
            // has never been simulated on this instance.
            Environment = WorkflowEntity.EnvDraft,
            SchemaHash = rootHash,
            LastSimulationId = null,
            ChangeSummary = changeSummary ?? "imported from bundle",
            CreatedByUserId = createdBy,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Workflows.Add(workflow);

        // ── triggers (§6, execution §7) ─────────────────────────────────
        var createdTriggers = await CreateTriggersAsync(bundle, workflow.WorkflowId, createdBy, now, notes, ct);

        await _db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        _logger.LogInformation(
            "workflow.bundle.imported workflow_id={WorkflowId} snippets_created={Snippets} subflows_created={Subflows} "
            + "triggers_created={Triggers} notes={Notes}",
            workflow.WorkflowId, resolution.SnippetsToCreate.Count, createdWorkflows.Count, createdTriggers.Count, notes.Count);

        return new BundleImportResult(workflow, notes, resolution.SnippetsToCreate, createdWorkflows, createdTriggers);
    }

    // Every trigger is created disabled, with a fresh secret and no targets: a file
    // must not be able to schedule anything on another instance, sign requests with a
    // secret that left the source, or aim at devices whose ids mean nothing here.
    private async Task<List<TriggerEntity>> CreateTriggersAsync(
        WorkflowBundle bundle, Guid workflowId, Guid? createdBy, DateTime now,
        List<string> notes, CancellationToken ct)
    {
        var created = new List<TriggerEntity>();
        var routesUsed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wanted in bundle.Triggers)
        {
            var type = (wanted.Type ?? string.Empty).Trim().ToLowerInvariant();
            var label = string.IsNullOrWhiteSpace(wanted.Name) ? "(unnamed)" : wanted.Name.Trim();

            if (string.IsNullOrWhiteSpace(wanted.Name))
            {
                notes.Add($"a trigger of type '{type}' had no name and was skipped.");
                continue;
            }
            if (!TriggerEntity.Types.Contains(type))
            {
                // Portable types are cron and webhook; anything else is exported as-is
                // by the other side and skipped here with a note (§6).
                notes.Add($"trigger '{label}' has type '{type}', which this build does not have; it was skipped.");
                continue;
            }

            var row = new TriggerEntity
            {
                WorkflowTriggerId = Guid.NewGuid(),
                WorkflowId = workflowId,
                Name = label,
                Type = type,
                Description = wanted.Description,
                TargetDevicesJson = "[]",
                InputDefaultsJson = wanted.InputDefaults is { ValueKind: JsonValueKind.Object } defaults
                    ? defaults.GetRawText() : null,
                AllowUnsigned = wanted.AllowUnsigned ?? false,
                AllowTargetOverride = wanted.AllowTargetOverride ?? false,
                Enabled = false,
                CreatedBy = createdBy,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };

            if (type == TriggerEntity.TypeCron)
            {
                if (!CronSchedule.TryParse(wanted.CronExpression, out _, out var cronError))
                {
                    notes.Add($"cron trigger '{label}' was skipped: {cronError}");
                    continue;
                }
                row.CronExpression = wanted.CronExpression!.Trim();
                var timezone = string.IsNullOrWhiteSpace(wanted.Timezone) ? "UTC" : wanted.Timezone.Trim();
                if (CronSchedule.IsValidTimezone(timezone, out _, out _))
                {
                    row.Timezone = timezone;
                }
                else
                {
                    row.Timezone = "UTC";
                    notes.Add(
                        $"cron trigger '{label}' names timezone '{timezone}', which this instance cannot resolve; "
                        + "it was stored as UTC and left disabled — fix the zone before enabling it.");
                }
                // NextRunAt stays null: the scheduler computes it when the trigger is enabled.
                notes.Add(
                    $"cron trigger '{label}' ({row.CronExpression} {row.Timezone}) was created DISABLED with no "
                    + "target devices. Set its targets, then enable it.");
            }
            else
            {
                var route = (wanted.Route ?? string.Empty).Trim();
                var routeFree = route.Length > 0
                                && !routesUsed.Contains(route)
                                && !await _db.WorkflowTriggers.AsNoTracking()
                                    .AnyAsync(t => t.IsActive && t.Route == route, ct);
                if (!routeFree)
                {
                    var regenerated = WorkflowTriggerSecrets.NewRoute();
                    if (route.Length > 0)
                        notes.Add(
                            $"webhook trigger '{label}': route '{route}' is already in use on this instance; "
                            + $"it was given the route '{regenerated}' instead — update the caller.");
                    route = regenerated;
                }
                routesUsed.Add(route);
                row.Route = route;
                // Fresh, never the source's: an HMAC secret that has been in a file is
                // no longer a secret. The plaintext is not returned by the import —
                // rotate it to obtain one.
                row.EncryptedSecret = _protector.Encrypt(WorkflowTriggerSecrets.NewSecret());
                notes.Add(
                    $"webhook trigger '{label}' (/api/hooks/{route}) was created DISABLED with a fresh secret and "
                    + "no target devices. Rotate the secret to obtain it, set the targets, then enable the trigger.");
            }

            // Fields the other product's triggers carry and this build's rows have no
            // column for. Dropping them silently is exactly what §7's "silence means
            // nothing was degraded" forbids: a trigger that was supposed to notify
            // someone on failure would simply stop doing it, and no one would know.
            var dropped = new List<string>();
            if (wanted.NotifyOn is { Count: > 0 }) dropped.Add($"notify_on ({string.Join("/", wanted.NotifyOn)})");
            if (!string.IsNullOrWhiteSpace(wanted.NotificationWebhookUrl)) dropped.Add("notification_webhook_url");
            if (wanted.InputSchema is { ValueKind: JsonValueKind.Object }) dropped.Add("input_schema");
            if (dropped.Count > 0)
                notes.Add(
                    $"trigger '{label}' carried {string.Join(", ", dropped)}, which this build's triggers do "
                    + "not have; it was created without them.");

            _db.WorkflowTriggers.Add(row);
            created.Add(row);
        }

        return created;
    }

    private static NodeReferences.RewritePlan WithWorkflowIds(
        NodeReferences.RewritePlan plan, IReadOnlyDictionary<Guid, Guid> workflowIds) => new()
    {
        SnippetIds = plan.SnippetIds,
        WorkflowIds = workflowIds,
        CredentialIdsByName = plan.CredentialIdsByName,
        RepositoryIdsByName = plan.RepositoryIdsByName,
        IntegrationNames = plan.IntegrationNames,
    };

    // metadata.is_subflow = true, merged into whatever metadata travelled (§5.4).
    private static string WithIsSubflow(JsonElement? metadata)
    {
        JsonObject obj;
        if (metadata is { ValueKind: JsonValueKind.Object } m && JsonNode.Parse(m.GetRawText()) is JsonObject parsed)
            obj = parsed;
        else
            obj = new JsonObject();
        obj["is_subflow"] = true;
        return obj.ToJsonString();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // lookups and mapping
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<Data.Models.Integration?> ResolveIntegrationAsync(
        string? slug, string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = await _db.Integrations.AsNoTracking()
                .FirstOrDefaultAsync(i => i.IsActive && i.Slug == slug, ct);
            if (bySlug is not null) return bySlug;
        }

        // Exact, case-insensitive — never fuzzy. A near-miss here points a workflow at
        // the wrong system, with that system's credentials.
        return await _db.Integrations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.IsActive && i.Name.ToLower() == name.ToLower(), ct);
    }

    private async Task<SnippetEntity?> ResolveSnippetAsync(BundleSnippet wanted, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(wanted.Slug))
        {
            var bySlug = await _db.Snippets.AsNoTracking()
                .FirstOrDefaultAsync(s => s.IsActive && s.Slug == wanted.Slug, ct);
            if (bySlug is not null) return bySlug;
        }
        return await _db.Snippets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsActive && s.Name.ToLower() == wanted.Name.ToLower(), ct);
    }

    // Appends " 2", " 3", … until the display name is free. The slug has Slug.Unique;
    // a display name cannot use it, because slugifying a name would rename it.
    private static string UniqueName(string candidate, Func<string, bool> isTaken)
    {
        if (!isTaken(candidate)) return candidate;
        for (var n = 2; n < 1000; n++)
        {
            var next = $"{candidate} {n}";
            if (!isTaken(next)) return next;
        }
        return $"{candidate} {Guid.NewGuid():N}";
    }

    private SnippetEntity FromBundle(
        BundleSnippet b, string type, IReadOnlySet<string> takenSlugs, IReadOnlySet<string> takenNames)
    {
        var now = DateTime.UtcNow;

        // Prefer the source slug when it is free, so the same snippet ends up with the
        // same identity on both instances and a later bundle from either side resolves
        // by slug rather than falling back to a name match.
        var slug = !string.IsNullOrWhiteSpace(b.Slug) && !takenSlugs.Contains(b.Slug)
            ? b.Slug!
            : Slug.Unique(b.Name, takenSlugs.Contains);

        // The name column is unique among active rows, and the resolver only got here
        // because no local snippet carries this name — but a bundle can carry two
        // snippets whose names collide with each other, and two of them called `notify`
        // both became `notify (imported)`: a unique-index violation surfacing as a raw
        // DbUpdateException instead of an import that simply works. Same de-collision
        // loop the slug above uses.
        var name = takenNames.Contains(b.Name) ? UniqueName($"{b.Name} (imported)", takenNames.Contains) : b.Name;

        var targetMode = (b.TargetMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            // FlowWeaver's pool fan-out has no equivalent here; per_device is the
            // nearest honest reading and the resolution notes say it was translated.
            "per_pool" or SnippetEntity.TargetPerDevice => SnippetEntity.TargetPerDevice,
            _ => SnippetEntity.TargetOnce,
        };

        return new SnippetEntity
        {
            SnippetId = Guid.NewGuid(),
            Slug = slug,
            Name = name,
            Type = type,
            Description = b.Description,
            Code = b.Code,
            ScriptLanguage = b.ScriptLanguage,
            InputSchemaJson = RawOrNull(b.InputSchema),
            OutputSchemaJson = RawOrNull(b.OutputSchema),
            TargetMode = targetMode,
            TimeoutSeconds = b.TimeoutSeconds > 0 ? b.TimeoutSeconds : 60,
            Idempotency = SnippetCatalog.NormalizeIdempotency(b.Idempotency),
            // Stored as received. The canonical shape (execution/SPEC.md §3) is what
            // the engine reads; the legacy shape is accepted there on read.
            RetryPolicyJson = RawOrNull(b.RetryPolicy),
            LogicDiagramMermaid = b.LogicDiagramMermaid,
            // NEVER carried over. network_enabled lifts the python sandbox's network
            // isolation and is admin-gated locally; a file from another instance must
            // not be able to grant it. The notes tell the operator it was dropped.
            NetworkEnabled = false,
            // Unverified here regardless of its status on the source instance — this
            // instance has not reviewed it and has not run it.
            Verified = false,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static BundleSnippet ToBundleSnippet(SnippetEntity s) => new()
    {
        Id = s.SnippetId,
        Slug = s.Slug,
        Name = s.Name,
        Type = s.Type,
        Description = s.Description,
        Code = s.Code,
        ScriptLanguage = s.ScriptLanguage,
        InputSchema = ParseOrNull(s.InputSchemaJson),
        OutputSchema = ParseOrNull(s.OutputSchemaJson),
        TargetMode = s.TargetMode,
        MaxParallel = 1,
        TimeoutSeconds = s.TimeoutSeconds,
        Idempotency = s.Idempotency,
        RetryPolicy = CanonicalRetryPolicy(s.RetryPolicyJson),
        LogicDiagramMermaid = s.LogicDiagramMermaid,
        NetworkEnabled = s.NetworkEnabled,
    };

    private static BundleTrigger ToBundleTrigger(TriggerEntity t) => new()
    {
        Name = t.Name,
        Type = t.Type,
        Description = t.Description,
        CronExpression = t.CronExpression,
        Timezone = t.Type == TriggerEntity.TypeCron ? t.Timezone : null,
        Route = t.Route,
        AllowUnsigned = t.Type == TriggerEntity.TypeWebhook ? t.AllowUnsigned : null,
        AllowTargetOverride = t.Type == TriggerEntity.TypeWebhook ? t.AllowTargetOverride : null,
        InputSchema = null,
        InputDefaults = ParseOrNull(t.InputDefaultsJson),
        Enabled = t.Enabled,
        // EncryptedSecret, TargetDevicesJson and the run statistics never travel.
    };

    /// <summary>
    /// The wire shape of execution/SPEC.md §3. Nashira stores the legacy
    /// <c>{max_attempts, delay_seconds, backoff}</c>; exporters write the canonical one.
    /// A policy already in canonical shape, or anything unrecognisable, passes through.
    /// </summary>
    internal static JsonElement? CanonicalRetryPolicy(string? json)
    {
        var parsed = ParseOrNull(json);
        if (parsed is not { ValueKind: JsonValueKind.Object } policy) return parsed;
        if (policy.TryGetProperty("max_retries", out _)) return policy;
        if (!policy.TryGetProperty("max_attempts", out var attemptsEl) || attemptsEl.ValueKind != JsonValueKind.Number)
            return policy;

        var attempts = attemptsEl.TryGetInt32(out var a) ? a : (int)attemptsEl.GetDouble();
        double delay = 1;
        if (policy.TryGetProperty("delay_seconds", out var delayEl) && delayEl.ValueKind == JsonValueKind.Number)
            delay = delayEl.GetDouble();
        var backoff = policy.TryGetProperty("backoff", out var backoffEl) && backoffEl.ValueKind == JsonValueKind.String
            ? backoffEl.GetString()!.Trim().ToLowerInvariant()
            : "fixed";
        if (backoff is not ("exponential" or "linear" or "fixed")) backoff = "fixed";

        return JsonSerializer.SerializeToElement(new
        {
            max_retries = Math.Max(0, attempts - 1),
            initial_delay_seconds = delay,
            backoff,
            // The profile cap this build applies (execution/SPEC.md §3, Nashira profile).
            max_delay_seconds = 30,
        });
    }

    private static JsonElement ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("[]").RootElement.Clone();
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("[]").RootElement.Clone(); }
    }

    private static JsonElement? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string? RawOrNull(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
            ? element.Value.GetRawText()
            : null;
}
