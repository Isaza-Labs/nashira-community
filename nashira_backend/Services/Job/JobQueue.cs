using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using JobEntity = nashira_backend.Data.Models.Job;

namespace nashira_backend.Services.Jobs;

public interface IJobQueue
{
    // Enqueues a workflow run. When `deliveryKey` names a delivery this trigger
    // already accepted, the EXISTING job is returned — whatever its status — so a
    // sender's retry can never produce a second run.
    Task<JobEntity> EnqueueWorkflowRunAsync(
        Guid workflowId, JsonElement? input, IReadOnlyList<Guid> targetDevices,
        Guid? triggerId, string? deliveryKey, CancellationToken ct,
        string trigger = Data.Models.RunTrigger.Schedule);
}

public sealed class JobQueue : IJobQueue
{
    private readonly AppDbContext _db;
    private readonly ILogger<JobQueue> _logger;

    public JobQueue(AppDbContext db, ILogger<JobQueue> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<JobEntity> EnqueueWorkflowRunAsync(
        Guid workflowId, JsonElement? input, IReadOnlyList<Guid> targetDevices,
        Guid? triggerId, string? deliveryKey, CancellationToken ct,
        string trigger = Data.Models.RunTrigger.Schedule)
    {
        if (triggerId is not null && !string.IsNullOrWhiteSpace(deliveryKey))
        {
            // Returned regardless of status: a completed delivery that gets
            // retried was still delivered once, and that is the whole promise.
            var existing = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(
                j => j.WorkflowTriggerId == triggerId && j.DeliveryKey == deliveryKey && j.IsActive, ct);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "jobs.dedup trigger={Trigger} delivery={Delivery} job={Job}",
                    triggerId, deliveryKey, existing.JobId);
                return existing;
            }
        }

        var now = DateTime.UtcNow;
        var job = new JobEntity
        {
            JobId = Guid.NewGuid(),
            Type = JobEntity.TypeWorkflowRun,
            PayloadJson = Data.Models.JobPayloads.WorkflowRun(workflowId, input, targetDevices, trigger),
            Status = JobEntity.StatusQueued,
            WorkflowTriggerId = triggerId,
            DeliveryKey = string.IsNullOrWhiteSpace(deliveryKey) ? null : deliveryKey.Trim(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Jobs.Add(job);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (job.DeliveryKey is not null)
        {
            // Two retries of the same delivery raced past the pre-check; the
            // unique index decided. The row that won is the answer.
            _db.Entry(job).State = EntityState.Detached;
            var winner = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(
                j => j.WorkflowTriggerId == triggerId && j.DeliveryKey == job.DeliveryKey && j.IsActive, ct);
            if (winner is not null) return winner;
            throw;
        }

        return job;
    }
}
