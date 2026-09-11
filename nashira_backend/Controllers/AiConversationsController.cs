using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Chat;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// The caller's own chat threads. A user only ever sees their own conversations.
[ApiController]
[Route("api/ai/conversations")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AiConversationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public AiConversationsController(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<ConversationResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.AIConversations.AsNoTracking()
            .Where(c => c.UserId == _user.UserId && c.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.UpdatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<ConversationResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConversationDetailResponse>> GetById(Guid id, CancellationToken ct)
    {
        var conv = await Find(id, ct);
        JsonElement messages;
        try { messages = JsonDocument.Parse(conv.MessagesJson).RootElement.Clone(); }
        catch { messages = JsonDocument.Parse("[]").RootElement.Clone(); }

        return new ConversationDetailResponse
        {
            ConversationId = conv.AIConversationId,
            Title = conv.Title,
            Status = conv.Status,
            TokensIn = conv.TokensIn,
            TokensOut = conv.TokensOut,
            CreatedAt = conv.CreatedAt,
            UpdatedAt = conv.UpdatedAt,
            AIProviderId = conv.AIProviderId,
            Model = conv.Model,
            Messages = messages,
        };
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var conv = await Find(id, ct);
        var now = DateTime.UtcNow;
        conv.IsActive = false;
        conv.UpdatedAt = now;
        // Attachments are soft-deleted WITH the conversation — not hard-removed — so
        // an admin restore from the audit trail brings the thread back whole, files
        // included. Every read path filters on IsActive, so a deleted thread's file
        // content is unreachable until then.
        var attachments = await _db.ConversationAttachments
            .Where(a => a.ConversationId == id && a.IsActive).ToListAsync(ct);
        foreach (var a in attachments)
        {
            a.IsActive = false;
            a.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return new NoContentResult();
    }

    private async Task<AIConversation> Find(Guid id, CancellationToken ct)
    {
        var conv = await _db.AIConversations.FirstOrDefaultAsync(
            c => c.AIConversationId == id && c.UserId == _user.UserId && c.IsActive, ct);
        if (conv is null) throw new NotFoundException("conversation not found");
        return conv;
    }

    private static ConversationResponse ToResponse(AIConversation c) => new()
    {
        ConversationId = c.AIConversationId,
        Title = c.Title,
        Status = c.Status,
        TokensIn = c.TokensIn,
        TokensOut = c.TokensOut,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
        AIProviderId = c.AIProviderId,
        Model = c.Model,
    };
}
