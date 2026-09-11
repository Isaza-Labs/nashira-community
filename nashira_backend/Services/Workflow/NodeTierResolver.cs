using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Worker;

namespace nashira_backend.Services.Workflow;

/// <summary>
/// A node whose effective tier the static plan could not work out, and why.
/// </summary>
/// <remarks>
/// Only <c>subflow</c> nodes reach this: every other node's tier is a property of a
/// snippet row this instance either has or does not have, and "does not have" already
/// has an answer (the contract's default for an unannotated node). A subflow's tier
/// lives in another workflow, and there are four ways that workflow can be unreadable —
/// it is not named, it does not exist, it re-enters the chain, or it sits past the depth
/// cap. In every one of them the plan reports the node here AND scores it
/// <see cref="IdempotencyKind.NonReversible"/>: the endpoint exists to promise that a
/// failed run can be undone, and a promise made over work nobody can see is the one
/// thing it must never make.
/// </remarks>
/// <param name="Code">The code the step executor would fail this node with at run time.</param>
public sealed record UnresolvableNode(string NodeId, string Code, string Reason);

/// <summary>The effective tier of every node in a graph, plus the ones that had no answer.</summary>
public sealed record NodeTiers(
    IReadOnlyDictionary<string, IdempotencyKind> Tiers,
    IReadOnlyList<UnresolvableNode> Unresolvable);

// Resolves the effective idempotency tier of every node in a graph, the way the step
// executor will at run time: the handler's floor for the snippet's type, the snippet's
// own declaration, and a stricter-only `config_overrides.idempotency` override
// (execution/SPEC.md §2, Idempotency.Effective).
//
// It exists so the static answer — GET /workflows/{id}/plan — cannot disagree with the
// executed one. The tier is a property of the snippet, and a caller holding only a Dag
// cannot see snippets; before this, the plan read the node's config_overrides alone and
// called an `email_send` step reversible.
//
// A `subflow` node is the same failure one level down. Its executed tier is the strictest
// among the child's nodes (execution/SPEC.md §5, SubflowOutcome.StrictestTier), and a
// resolver that stopped at the parent's graph scored it at the unannotated default — so
// the plan could report a workflow fully reversible when running it would send the email
// its subflow wraps. So the resolver descends, under the two guards execution already
// uses: the ancestry cuts a cycle and SubflowLimits.MaxDepth bounds the descent. Those
// are also what keep this endpoint from hanging or overflowing its stack on a graph
// authored to do exactly that.
public sealed class NodeTierResolver
{
    // execution/SPEC.md §5. Every other config_overrides key is the child's input; this
    // one names the child.
    private const string SubflowWorkflowIdKey = "subflow_workflow_id";

    private readonly AppDbContext _db;
    private readonly ISnippetHandlerRegistry _registry;
    private readonly IServiceProvider _sp;

    // Subflow answers that do not depend on WHERE they were reached from, keyed by child
    // workflow id. A diamond — two branches calling the same shared child — is the
    // ordinary shape of reuse, and without this a wide graph re-walks the same subtree
    // once per path into it. An answer the depth cap or the cycle guard produced is not
    // cached: those are about a position in a chain, not about the workflow.
    private readonly Dictionary<Guid, ChildTier> _children = [];

    public NodeTierResolver(AppDbContext db, ISnippetHandlerRegistry registry, IServiceProvider sp)
    {
        _db = db;
        _registry = registry;
        _sp = sp;
    }

    /// <param name="workflowId">
    /// The workflow <paramref name="dag"/> belongs to. It seeds the ancestry, so a
    /// workflow whose subflow node names itself is caught at the first descent rather
    /// than on the eighth.
    /// </param>
    public async Task<NodeTiers> ResolveAsync(Dag dag, Guid workflowId, CancellationToken ct)
    {
        var graph = await ResolveGraphAsync(dag, [workflowId], depth: 0, ct);
        return new NodeTiers(graph.Tiers, graph.Unresolvable);
    }

    // One graph's nodes. `ancestry` is the workflow id of every graph on the path to this
    // one, this one included; `depth` counts nested runs the way SubflowInvocation does —
    // the workflow the plan was asked about is depth 0.
    private async Task<GraphTiers> ResolveGraphAsync(
        Dag dag, IReadOnlyList<Guid> ancestry, int depth, CancellationToken ct)
    {
        var ids = dag.Nodes.Values
            .Select(n => Guid.TryParse(n.SnippetId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var snippets = ids.Count == 0
            ? []
            : await _db.Snippets.AsNoTracking()
                .Where(s => s.IsActive && ids.Contains(s.SnippetId))
                .ToDictionaryAsync(s => s.SnippetId, ct);

        var tiers = new Dictionary<string, IdempotencyKind>(StringComparer.Ordinal);
        var unresolvable = new List<UnresolvableNode>();
        var pathDependent = false;

        foreach (var node in dag.Nodes.Values)
        {
            // The schema's node `type` enum settles the word, so either marker alone
            // identifies the node — the same test ExecuteAsync makes before dispatching.
            if (node.SnippetId == NodeReferences.SubflowLiteral
                || node.NodeType == NodeReferences.SubflowLiteral)
            {
                var child = await ResolveSubflowAsync(node, ancestry, depth, ct);
                pathDependent |= child.PathDependent;

                if (child.Reason is not null)
                {
                    unresolvable.Add(new UnresolvableNode(node.Id, child.Code!, child.Reason));
                    tiers[node.Id] = IdempotencyKind.NonReversible;
                    continue;
                }

                // The child's strictest becomes this node's floor, and the node's own
                // stricter-only override applies on top — the same rule every other node
                // gets, and the only way an author can raise a subflow they know more
                // about than its graph says. It cannot lower it: the child's tier is a
                // ceiling reached by what the child will actually do.
                tiers[node.Id] = Idempotency.Effective(
                    new Data.Models.Snippet(), child.Tier, node.ConfigOverrides);
                continue;
            }

            // `__start__`, `__end__` and any unbound reference: no snippet, so no floor to
            // read. The contract's default for an unannotated node, still raisable by a
            // stricter override — and it is what executing them records, since the
            // sentinel branch returns an outcome carrying that same default.
            if (!Guid.TryParse(node.SnippetId, out var snippetId)
                || !snippets.TryGetValue(snippetId, out var snippet))
            {
                tiers[node.Id] = Idempotency.Effective(
                    new Data.Models.Snippet(), IdempotencyKind.RequiresCompensation, node.ConfigOverrides);
                continue;
            }

            var floor = _registry.Resolve(snippet.Type, _sp)?.DefaultIdempotency
                        ?? IdempotencyKind.RequiresCompensation;
            tiers[node.Id] = Idempotency.Effective(snippet, floor, node.ConfigOverrides);
        }

        return new GraphTiers(tiers, unresolvable, pathDependent);
    }

    // What one `subflow` node's child contributes: the tier the parent's step would carry,
    // or the reason there is no honest answer.
    private async Task<ChildTier> ResolveSubflowAsync(
        WorkflowNode node, IReadOnlyList<Guid> ancestry, int depth, CancellationToken ct)
    {
        if (node.ConfigOverrides.ValueKind != JsonValueKind.Object
            || !node.ConfigOverrides.TryGetProperty(SubflowWorkflowIdKey, out var rawId)
            || rawId.ValueKind != JsonValueKind.String
            || !Guid.TryParse(rawId.GetString(), out var childId)
            || childId == Guid.Empty)
            return ChildTier.Unresolved("subflow_missing",
                $"config_overrides.{SubflowWorkflowIdKey} is missing or is not a GUID, so there is no "
                + "child workflow to score. The run will fail this node rather than execute it.");

        // Both guards run before the row is read, in the order execution applies them: a
        // chain that can never finish should cost one comparison, not eight nested
        // queries on the way to finding out.
        if (depth + 1 > SubflowLimits.MaxDepth)
            return ChildTier.Unresolved("subflow_depth_exceeded",
                $"this node sits at subflow depth {depth + 1}, past the cap of {SubflowLimits.MaxDepth} "
                + "(execution/SPEC.md §5). The run refuses it before the child starts, so what that "
                + "child would have done is not part of this plan.",
                pathDependent: true);

        if (ancestry.Contains(childId))
            return ChildTier.Unresolved("subflow_cycle",
                $"subflow workflow {childId} is already running above this node, so the chain re-enters "
                + "itself and can never finish (execution/SPEC.md §5). The run refuses it before "
                + "anything happens.",
                pathDependent: true);

        if (_children.TryGetValue(childId, out var cached)) return cached;

        var row = await _db.Workflows.AsNoTracking()
            .Where(w => w.WorkflowId == childId && w.IsActive)
            .Select(w => new { w.Name, w.NodesJson, w.EdgesJson })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            var gone = ChildTier.Unresolved("subflow_missing",
                $"subflow workflow {childId} does not exist on this instance. Nothing will run for "
                + "this node.");
            _children[childId] = gone;
            return gone;
        }

        Dag childDag;
        try
        {
            using var nodesDoc = JsonDocument.Parse(row.NodesJson);
            using var edgesDoc = JsonDocument.Parse(row.EdgesJson);
            childDag = Dag.Parse(nodesDoc.RootElement, edgesDoc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or ValidationException)
        {
            // The run refuses this child too — a graph that will not parse is a refusal in
            // RunDetailedAsync rather than a step outcome — but the plan is read before
            // the run, which is the useful moment to learn it.
            var broken = ChildTier.Unresolved("subflow_unreadable",
                $"the graph of subflow '{row.Name}' ({childId}) cannot be read: {ex.Message}");
            _children[childId] = broken;
            return broken;
        }

        var child = await ResolveGraphAsync(childDag, [.. ancestry, childId], depth + 1, ct);

        // An unreadable node anywhere below makes the whole subtree unreadable. The reason
        // names the workflow and the node it came from: "this subflow cannot be scored",
        // three levels above where the trouble actually is, is not actionable.
        if (child.Unresolvable.Count > 0)
        {
            var first = child.Unresolvable[0];
            var nested = ChildTier.Unresolved(first.Code,
                $"subflow '{row.Name}' ({childId}), node '{first.NodeId}': {first.Reason}",
                child.PathDependent);
            if (!child.PathDependent) _children[childId] = nested;
            return nested;
        }

        // execution/SPEC.md §5: the step's tier is the strictest among the child's nodes,
        // and a child in which nothing runs left nothing to compensate. The one place this
        // can read stricter than the executed answer is a child whose strictest node sits
        // behind an edge that never fires — SubflowOutcome.StrictestTier excludes skipped
        // nodes, and no static reading knows which those will be. Erring strict is the
        // only direction that cannot become a rollback promise nobody can keep.
        var tier = child.Tiers.Count == 0
            ? IdempotencyKind.Idempotent
            : child.Tiers.Values.Max();

        var resolved = new ChildTier(tier, null, null, child.PathDependent);
        if (!child.PathDependent) _children[childId] = resolved;
        return resolved;
    }

    private sealed record GraphTiers(
        Dictionary<string, IdempotencyKind> Tiers,
        List<UnresolvableNode> Unresolvable,
        bool PathDependent);

    // `PathDependent` marks an answer that came from the depth cap or the cycle guard,
    // which are about this node's position in a chain rather than about the child itself.
    // Such an answer must not be cached and handed back for the same child reached from
    // somewhere shallower, where it would be wrong.
    private sealed record ChildTier(
        IdempotencyKind Tier, string? Code, string? Reason, bool PathDependent)
    {
        public static ChildTier Unresolved(string code, string reason, bool pathDependent = false) =>
            new(IdempotencyKind.NonReversible, code, reason, pathDependent);
    }
}
