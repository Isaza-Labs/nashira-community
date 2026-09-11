using nashira_backend.Exceptions;

namespace nashira_backend.Services.Workflow;

// The sub-workflow graph a bundle carries (bundle/SPEC.md §5.4): which carried
// workflow reaches which, in the order they have to be created so that every
// `subflow_workflow_id` can be remapped to a row that already exists.
public static class BundleSubWorkflows
{
    // execution/SPEC.md §5 caps subflow nesting at 8, so a bundle nested deeper than
    // that could never run anyway and is better refused where the refusal is readable.
    // It also bounds the walk below, which is plain recursion: a StackOverflowException
    // cannot be caught, so a deep enough bundle would take the process down instead of
    // producing an error message.
    public const int MaxDepth = SubflowLimits.MaxDepth;

    /// <summary>
    /// The carried sub-workflows, children before parents. Throws
    /// <c>bundle_subflow_cycle</c> when a workflow reaches itself, and
    /// <c>bundle_incomplete</c> when a <c>subflow</c> node names a workflow the bundle
    /// does not carry — a placeholder child is exactly the guess this format forbids.
    /// </summary>
    /// <param name="rootId">
    /// The source id of the workflow at the root, when the caller knows it. The exporter
    /// does; an importer does not, because a bundle deliberately drops the source
    /// <c>workflow_id</c> (§3). Without it a child pointing back at the root looks like a
    /// workflow the bundle forgot to carry, and the cycle would be refused as
    /// <c>bundle_incomplete</c> — the right refusal for the wrong reason.
    /// </param>
    public static IReadOnlyList<BundleSubWorkflow> CreationOrder(WorkflowBundle bundle, Guid? rootId = null)
    {
        var carried = bundle.Dependencies.Workflows
            .GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First());

        // The root is keyed Guid.Empty throughout — it is not a carried sub-workflow —
        // so a reference to its source id means "back to the root", not "missing".
        Guid Key(Guid id) => rootId is { } root && id == root ? Guid.Empty : id;

        // Every edge: parent (root = Guid.Empty) → child.
        var children = new Dictionary<Guid, List<Guid>> { [Guid.Empty] = [] };
        var missing = new List<string>();

        void Collect(Guid parentId, string parentName, System.Text.Json.JsonElement nodes)
        {
            var refs = NodeReferences.Extract(nodes);
            children[parentId] = refs.SubflowWorkflowIds.Select(Key).ToList();
            foreach (var childId in refs.SubflowWorkflowIds)
                if (Key(childId) != Guid.Empty && !carried.ContainsKey(childId))
                    missing.Add($"'{parentName}' → {childId}");
        }

        Collect(Guid.Empty, bundle.Workflow.Name, bundle.Nodes);
        foreach (var w in carried.Values) Collect(w.Id, w.Name, w.Nodes);

        if (missing.Count > 0)
            throw new ValidationException(
                "the bundle's subflow nodes reference workflows whose definitions it does not carry "
                + $"({string.Join(", ", missing)}). Re-export it from the source instance.",
                "bundle_incomplete");

        // Depth-first with a grey set: a grey node reached again is a cycle. Post-order
        // is the creation order (children first).
        var order = new List<BundleSubWorkflow>();
        var done = new HashSet<Guid>();
        var active = new HashSet<Guid>();
        var path = new Stack<Guid>();

        void Visit(Guid id, int depth)
        {
            if (depth > MaxDepth)
                throw new ValidationException(
                    $"the bundle's subflows nest deeper than {MaxDepth} levels, which no run could execute "
                    + "(execution/SPEC.md §5). Flatten the workflow and export it again.",
                    "bundle_incomplete");

            if (done.Contains(id)) return;
            if (!active.Add(id))
            {
                var cycle = path.Reverse().SkipWhile(p => p != id).Append(id)
                    .Select(p => p == Guid.Empty ? $"'{bundle.Workflow.Name}'" : $"'{carried[p].Name}'");
                throw new ValidationException(
                    $"the bundle's subflows form a cycle: {string.Join(" → ", cycle)}. A workflow "
                    + "that reaches itself can never finish, so it is refused rather than imported.",
                    "bundle_subflow_cycle");
            }
            path.Push(id);
            foreach (var child in children.TryGetValue(id, out var cs) ? cs : [])
                Visit(child, depth + 1);
            path.Pop();
            active.Remove(id);
            done.Add(id);
            if (id != Guid.Empty) order.Add(carried[id]);
        }

        Visit(Guid.Empty, 0);
        // Carried but unreachable workflows are still created: the exporter put them
        // there on purpose and a bundle is imported whole or not at all.
        foreach (var w in carried.Values) Visit(w.Id, 0);

        return order;
    }
}
