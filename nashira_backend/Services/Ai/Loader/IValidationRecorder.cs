using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Loader;

// Persists the outcome of a template validation for audit/history.
public interface IValidationRecorder
{
    Task RecordAsync(string kind, string targetName, TemplateValidationResult result, CancellationToken ct);
}

public sealed class ValidationRecorder : IValidationRecorder
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ValidationRecorder(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task RecordAsync(string kind, string targetName, TemplateValidationResult result, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _db.ValidationRecords.Add(new ValidationRecord
        {
            ValidationRecordId = Guid.NewGuid(),
            Kind = kind,
            TargetName = targetName,
            Ok = result.Ok,
            IssuesJson = result.Issues.Count > 0 ? JsonSerializer.Serialize(result.Issues) : null,
            UserId = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(ct);
    }
}
