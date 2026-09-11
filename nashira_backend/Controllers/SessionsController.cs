using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Audit;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Every conversation in the installation, for an administrator.
//
// Distinct from /api/ai/conversations, which is scoped to `UserId == me` and always
// will be — that is a user reading their own history. This is the oversight view: what
// has been asked of the agent across the organisation, and what the agent did about it.
// Admin-only for the obvious reason that it reads everyone's chats.
//
// The transcript answers "what was said". The turns answer "what was done": model,
// tokens, and every tool call with its arguments and its result. Those are recorded as
// they happen (AgentTurn) because the audit trail only records mutations — a turn that
// read twenty things and changed nothing leaves no trace there.
[ApiController]
[Route("api/sessions")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class SessionsController : ControllerBase
{
    // A transcript is read whole; a turn's payloads are not. Bounded so one enormous
    // session cannot make the endpoint a liability.
    private const int MaxTurnsReturned = 200;

    private readonly AppDbContext _db;

    public SessionsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<ListResponse<SessionSummary>>> Get(
        Guid? userId = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var q = _db.AIConversations.AsNoTracking().Where(c => c.IsActive);
        if (userId is { } uid) q = q.Where(c => c.UserId == uid);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.UpdatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        var ids = rows.Select(r => r.AIConversationId).ToList();

        // Grouped once rather than per row: a session list is the one place where an
        // N+1 is guaranteed to be N=50.
        var stats = await _db.AgentTurns.AsNoTracking()
            .Where(t => ids.Contains(t.ConversationId))
            .GroupBy(t => t.ConversationId)
            .Select(g => new
            {
                ConversationId = g.Key,
                Turns = g.Count(),
                ToolCalls = g.Sum(t => t.ToolCallCount),
                Failed = g.Count(t => t.Status != Data.Models.AgentTurn.StatusCompleted),
            })
            .ToDictionaryAsync(x => x.ConversationId, ct);

        var users = await UsernamesAsync(rows.Where(r => r.UserId != Guid.Empty).Select(r => r.UserId), ct);

        return new OkObjectResult(new ListResponse<SessionSummary>
        {
            Items = rows.Select(c =>
            {
                var s = stats.GetValueOrDefault(c.AIConversationId);
                return new SessionSummary
                {
                    ConversationId = c.AIConversationId,
                    UserId = c.UserId,
                    Username = users.GetValueOrDefault(c.UserId),
                    Title = c.Title,
                    Status = c.Status,
                    MessageCount = CountMessages(c.MessagesJson),
                    TurnCount = s?.Turns ?? 0,
                    ToolCallCount = s?.ToolCalls ?? 0,
                    FailedTurnCount = s?.Failed ?? 0,
                    TokensIn = c.TokensIn,
                    TokensOut = c.TokensOut,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                };
            }).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SessionDetail>> GetById(Guid id, CancellationToken ct)
    {
        var c = await _db.AIConversations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AIConversationId == id && x.IsActive, ct);
        if (c is null) throw new NotFoundException("session not found");

        var turns = await _db.AgentTurns.AsNoTracking()
            .Where(t => t.ConversationId == id)
            .OrderBy(t => t.StartedAt)
            .Take(MaxTurnsReturned)
            .ToListAsync(ct);

        var users = await UsernamesAsync([c.UserId], ct);
        var messages = ParseMessages(c.MessagesJson);

        return new SessionDetail
        {
            ConversationId = c.AIConversationId,
            UserId = c.UserId,
            Username = users.GetValueOrDefault(c.UserId),
            Title = c.Title,
            Status = c.Status,
            MessageCount = messages.Count,
            TurnCount = turns.Count,
            ToolCallCount = turns.Sum(t => t.ToolCallCount),
            FailedTurnCount = turns.Count(t => t.Status != Data.Models.AgentTurn.StatusCompleted),
            TokensIn = c.TokensIn,
            TokensOut = c.TokensOut,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            Messages = messages,
            Turns = turns.Select(t => new SessionTurn
            {
                AgentTurnId = t.AgentTurnId,
                Model = t.Model,
                UserMessage = t.UserMessage,
                AssistantText = t.AssistantText,
                ToolCalls = ParseJson(t.ToolCallsJson),
                ToolCallCount = t.ToolCallCount,
                TokensIn = t.TokensIn,
                TokensOut = t.TokensOut,
                Iterations = t.Iterations,
                Status = t.Status,
                Error = t.Error,
                StartedAt = t.StartedAt,
                ElapsedMs = t.ElapsedMs,
            }).ToList(),
        };
    }

    // Turns whose conversation was deleted still matter — that is exactly the trail
    // someone would want gone — so the flat view is not a join on conversations.
    [HttpGet("turns")]
    public async Task<ActionResult<ListResponse<SessionTurn>>> Turns(
        Guid? userId = null, string? status = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var q = _db.AgentTurns.AsNoTracking();
        if (userId is { } uid) q = q.Where(t => t.UserId == uid);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(t => t.Status == status);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(t => t.StartedAt).Skip(offset).Take(limit).ToListAsync(ct);

        return new OkObjectResult(new ListResponse<SessionTurn>
        {
            Items = rows.Select(t => new SessionTurn
            {
                AgentTurnId = t.AgentTurnId,
                Model = t.Model,
                UserMessage = t.UserMessage,
                AssistantText = t.AssistantText,
                ToolCalls = ParseJson(t.ToolCallsJson),
                ToolCallCount = t.ToolCallCount,
                TokensIn = t.TokensIn,
                TokensOut = t.TokensOut,
                Iterations = t.Iterations,
                Status = t.Status,
                Error = t.Error,
                StartedAt = t.StartedAt,
                ElapsedMs = t.ElapsedMs,
            }).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    private async Task<Dictionary<Guid, string>> UsernamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await _db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.Username, ct);
    }

    private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement.Clone();

    private static JsonElement ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EmptyArray;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return EmptyArray; }
    }

    private sealed record StoredMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private static List<SessionMessage> ParseMessages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<StoredMessage>>(json) ?? [])
                .Select(m => new SessionMessage { Role = m.Role, Content = m.Content })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int CountMessages(string? json) => ParseMessages(json).Count;
}
