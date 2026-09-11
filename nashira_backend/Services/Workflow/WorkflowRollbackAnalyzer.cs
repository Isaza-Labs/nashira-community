namespace nashira_backend.Services.Workflow;

public sealed record RollbackReport(
    bool Reversible, IReadOnlyList<string> ReversibleNodes, IReadOnlyList<string> NonReversibleNodes);

// Classifies a workflow's nodes by idempotency to decide whether it can be automatically
// rolled back. A workflow is reversible only if it contains no NonReversible node — the
// oracle blocks rollback of such workflows (a forward fix is required instead).
public static class WorkflowRollbackAnalyzer
{
    /// <summary>
    /// <paramref name="tiers"/> is the effective tier per node id
    /// (<see cref="Idempotency.Effective"/>), which only a caller that can see the
    /// snippet rows can compute — see <see cref="NodeTierResolver"/>. A node the map
    /// does not mention takes the contract's default for an unannotated node.
    /// </summary>
    /// <remarks>
    /// This used to read <c>config_overrides.idempotency</c> straight off the node,
    /// which made the plan disagree with the run: an <c>email_send</c> node was
    /// "reversible" here and non-reversible where it actually executed.
    /// </remarks>
    public static RollbackReport Analyze(Dag dag, IReadOnlyDictionary<string, IdempotencyKind> tiers)
    {
        var reversible = new List<string>();
        var nonReversible = new List<string>();

        foreach (var node in dag.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            var tier = tiers.TryGetValue(node.Id, out var t) ? t : IdempotencyKind.RequiresCompensation;
            if (Idempotency.IsReversible(tier))
                reversible.Add(node.Id);
            else
                nonReversible.Add(node.Id);
        }

        return new RollbackReport(nonReversible.Count == 0, reversible, nonReversible);
    }
}
