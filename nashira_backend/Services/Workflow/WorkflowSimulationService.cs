using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Workflow;

// Runs the structural analysis over a workflow's current nodes/edges, persists the
// SimulationResult (with the canonical SchemaHash of what was simulated), and points
// the workflow's LastSimulationId at it. The promotion gate consumes that record.
public sealed class WorkflowSimulationService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public WorkflowSimulationService(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<SimulationResult> SimulateAsync(Data.Models.Workflow workflow, CancellationToken ct)
    {
        using var nodesDoc = JsonDocument.Parse(workflow.NodesJson);
        using var edgesDoc = JsonDocument.Parse(workflow.EdgesJson);
        var nodes = nodesDoc.RootElement;
        var edges = edgesDoc.RootElement;

        var analysis = WorkflowSimulationAnalyzer.Analyze(nodes, edges);
        var hash = WorkflowCanonicalizer.ComputeSchemaHash(nodes, edges);

        var now = DateTime.UtcNow;
        var row = new SimulationResult
        {
            SimulationResultId = Guid.NewGuid(),
            WorkflowId = workflow.WorkflowId,
            NodeCount = analysis.NodeCount,
            IssueCount = analysis.Issues.Count,
            WarningCount = analysis.Warnings.Count,
            Ok = analysis.Ok,
            SchemaHash = hash,
            IssuesJson = JsonSerializer.Serialize(analysis.Issues),
            WarningsJson = JsonSerializer.Serialize(analysis.Warnings),
            SimulatedByUserId = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.SimulationResults.Add(row);

        workflow.LastSimulationId = row.SimulationResultId;
        workflow.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return row;
    }
}
