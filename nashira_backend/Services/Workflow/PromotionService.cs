using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Workflow;

// Promotes a workflow along draft -> qa -> production. draft->qa runs the simulation
// gate (PromotionGate; 412 with a contract code on refusal). qa->production requires
// an approver different from the promoter. Promotion creates an immutable copy in the
// target environment (PromotedFrom links back; the source row is untouched) and emits
// an audit event describing the executed artifact.
public sealed class PromotionService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    private readonly Services.Policy.IPolicyGate _gate;

    public PromotionService(
        AppDbContext db, ICurrentUser user, IAuditLogger audit, Services.Policy.IPolicyGate gate)
    {
        _db = db;
        _user = user;
        _audit = audit;
        _gate = gate;
    }

    public async Task<WorkflowEntity> PromoteAsync(
        WorkflowEntity source, string target, string? approvedBy, string? changeSummary, CancellationToken ct)
    {
        target = (target ?? string.Empty).Trim().ToLowerInvariant();
        var action = $"promote:{source.Environment}->{target}";

        GateSimulation? sim = null;
        if (source.LastSimulationId is { } simId)
        {
            var record = await _db.SimulationResults.AsNoTracking().FirstOrDefaultAsync(
                s => s.SimulationResultId == simId && s.IsActive, ct);
            if (record is not null) sim = new GateSimulation(record.SchemaHash, record.Ok);
        }

        var gate = PromotionGate.Check(action, source.Environment, sim, source.SchemaHash);
        if (!gate.Ok)
        {
            throw gate.HttpStatus == 412
                ? new PreconditionFailedException(gate.Message, gate.Code)
                : new ValidationException(gate.Message, gate.Code);
        }

        // Organisational gates sit between the built-in gate and the four-eyes
        // check, and the order is deliberate in both directions.
        //
        // After the built-in gate: an artifact's own problems (unsimulated, stale
        // hash) are the author's to fix alone, so they are worth reporting first.
        //
        // Before four-eyes: "this needs three successful qa runs" is something the
        // promoter should learn without first having to find a second person to
        // approve a promotion that was never going to be allowed.
        var gateDecision = await _gate.EvaluateAsync(
            new Services.Policy.PolicyGateContext("promote", source.Environment, target, source.WorkflowId), ct);
        if (gateDecision.Denied)
            throw new PreconditionFailedException(
                $"blocked by policy '{gateDecision.PolicyName}': {gateDecision.Reason}", "policy_blocked");

        var promotedBy = _user.Username ?? string.Empty;
        if (target == WorkflowEntity.EnvProduction)
        {
            if (string.IsNullOrWhiteSpace(approvedBy))
                throw new PreconditionFailedException(
                    "qa->production requires approved_by", "approval_required");
            if (string.Equals(approvedBy.Trim(), promotedBy, StringComparison.OrdinalIgnoreCase))
                throw new PreconditionFailedException(
                    "approved_by must be different from the promoter", "approval_required");
        }

        var now = DateTime.UtcNow;
        var promoted = new WorkflowEntity
        {
            WorkflowId = Guid.NewGuid(),
            Name = source.Name,
            Description = source.Description,
            Version = source.Version + 1,
            SchemaVersion = source.SchemaVersion,
            NodesJson = source.NodesJson,
            EdgesJson = source.EdgesJson,
            InputSchemaJson = source.InputSchemaJson,
            MetadataJson = source.MetadataJson,
            Environment = target,
            SchemaHash = source.SchemaHash,
            LastSimulationId = source.LastSimulationId,
            PromotedFrom = source.WorkflowId,
            ChangeSummary = changeSummary ?? source.ChangeSummary,
            PromotedAt = now,
            CreatedByUserId = _user.IsAuthenticated ? _user.UserId : null,
            // Provenance follows the promotion: a production workflow still points at
            // the conversation that authored its draft.
            ConversationId = source.ConversationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Workflows.Add(promoted);

        // Snapshot the definition as it was promoted. Written here rather than by a
        // later job so the record cannot diverge from the promotion it documents.
        _db.WorkflowVersions.Add(new Data.Models.WorkflowVersion
        {
            WorkflowVersionId = Guid.NewGuid(),
            WorkflowId = promoted.WorkflowId,
            Version = promoted.Version,
            NodesJson = promoted.NodesJson,
            EdgesJson = promoted.EdgesJson,
            InputSchemaJson = promoted.InputSchemaJson,
            MetadataJson = promoted.MetadataJson,
            SchemaHash = promoted.SchemaHash,
            Environment = target,
            ChangeSummary = promoted.ChangeSummary,
            PromotedAt = now,
            PromotedBy = _user.Username ?? string.Empty,
            ConversationId = promoted.ConversationId,
            SimulationId = promoted.LastSimulationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("workflow", promoted.WorkflowId, "promote",
            before: new { from_environment = source.Environment, from_workflow_id = source.WorkflowId },
            after: new
            {
                to_environment = target,
                version = promoted.Version,
                schema_hash = promoted.SchemaHash,
                approved_by = approvedBy,
                promoted_by = promotedBy,
            },
            ct: ct);

        return promoted;
    }
}
