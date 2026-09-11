using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.SelfCorrection;

public sealed class LearningsStore : ILearningsStore
{
    private const double MinConfidence = 0.3;
    private const int CandidateLimit = 25;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public LearningsStore(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<AgentLearning?> FindBestFixAsync(string toolName, string serviceType, string errorMessage, CancellationToken ct)
    {
        var candidates = await _db.AgentLearnings.AsNoTracking()
            .Where(l => l.IsActive
                && l.Confidence >= MinConfidence
                && (l.ToolName == "" || l.ToolName == toolName)
                && (l.ServiceType == "" || l.ServiceType == serviceType))
            .OrderByDescending(l => l.Confidence)
            .Take(CandidateLimit)
            .ToListAsync(ct);

        // Pattern match is done in-memory (regex, substring fallback) — the DB filter
        // has already narrowed to tool/service/confidence candidates.
        return candidates.FirstOrDefault(l => PatternMatches(l.ErrorPattern, errorMessage));
    }

    public async Task RecordOutcomeAsync(Guid learningId, bool success, CancellationToken ct)
    {
        var l = await _db.AgentLearnings.FirstOrDefaultAsync(x => x.AgentLearningId == learningId, ct);
        if (l is null) return;
        // Only mutate the tenant's own learnings — never shared/system knowledge.
        // Never mutate seeded system knowledge — its confidence is curated, not learned.
        if (l.IsSystem) return;

        if (success) l.SuccessCount++; else l.FailureCount++;
        l.Confidence = ConfidenceOf(l.SuccessCount, l.FailureCount);
        l.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RecordDiscoveredAsync(
        string errorPattern, string errorCategory, string serviceType,
        string toolName, string fixStrategy, string? fixParamsJson, CancellationToken ct)
    {
        var pattern = Truncate(errorPattern, 500);
        var existing = await _db.AgentLearnings.FirstOrDefaultAsync(l =>
            l.IsActive
            && l.ToolName == (toolName ?? "") && l.FixStrategy == fixStrategy && l.ErrorPattern == pattern, ct);

        if (existing is not null)
        {
            existing.SuccessCount++;
            existing.Confidence = ConfidenceOf(existing.SuccessCount, existing.FailureCount);
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var now = DateTime.UtcNow;
        _db.AgentLearnings.Add(new AgentLearning
        {
            AgentLearningId = Guid.NewGuid(),
            ErrorPattern = pattern,
            ErrorCategory = errorCategory,
            ServiceType = serviceType ?? string.Empty,
            ToolName = toolName ?? string.Empty,
            FixStrategy = fixStrategy,
            FixParamsJson = fixParamsJson,
            Category = AgentLearning.CategoryLearning,
            Confidence = 0.5,
            SuccessCount = 1,
            FailureCount = 0,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(ct);
    }

    internal static double ConfidenceOf(long success, long failure)
    {
        var total = success + failure;
        return total == 0 ? 0.5 : Math.Round((double)success / total, 4);
    }

    internal static bool PatternMatches(string pattern, string message)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        try
        {
            return Regex.IsMatch(message, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (Exception) // invalid regex or timeout → substring fallback
        {
            return message.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
