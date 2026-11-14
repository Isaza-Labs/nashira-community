using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Audit;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;

namespace nashira_backend.Tests;

// A turn's prompt-cache figures are kept on its agent_turns row and come back from
// /api/sessions, and a row recorded before they were kept reads as unknown, not as 0.
public class TurnCacheRecordTests
{
    private static AgentConversationRunner.TurnRecord Turn(Guid conversationId, int cacheRead, int cacheWrite) => new(
        conversationId, "m", "hello", "hi", [], TokensIn: 45_139, TokensOut: 120,
        TokensCacheRead: cacheRead, TokensCacheWrite: cacheWrite, Iterations: 2,
        Status: AgentTurn.StatusCompleted, Error: null,
        StartedAt: new DateTime(2026, 11, 13, 1, 0, 0, DateTimeKind.Utc), ElapsedMs: 4_500);

    [Fact]
    public void The_stored_row_keeps_the_cache_figures_beside_the_token_counts()
    {
        var row = AgentConversationRunner.ToAgentTurn(Turn(Guid.NewGuid(), 32_768, 0), userId: null);

        Assert.Equal(45_139, row.TokensIn);
        Assert.Equal(120, row.TokensOut);
        Assert.Equal(32_768, row.TokensCacheRead);
        Assert.Equal(0, row.TokensCacheWrite);
        Assert.Equal(2, row.Iterations);
        Assert.Equal(4_500, row.ElapsedMs);
    }

    [Fact]
    public async Task Sessions_return_the_cache_figures_and_null_for_an_older_turn()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"turn-cache-{Guid.NewGuid()}").Options);
        var conversationId = Guid.NewGuid();
        db.AIConversations.Add(new AIConversation
        {
            AIConversationId = conversationId,
            UserId = Guid.NewGuid(),
            Title = "t",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var newer = AgentConversationRunner.ToAgentTurn(Turn(conversationId, 32_768, 1_024), userId: null);
        var older = AgentConversationRunner.ToAgentTurn(Turn(conversationId, 0, 0), userId: null);
        older.StartedAt = newer.StartedAt.AddHours(-1);
        older.TokensCacheRead = null;
        older.TokensCacheWrite = null;
        db.AgentTurns.AddRange(newer, older);
        await db.SaveChangesAsync();
        var controller = new SessionsController(db);

        var flat = Assert.IsType<ListResponse<SessionTurn>>(
            Assert.IsType<OkObjectResult>((await controller.Turns(ct: CancellationToken.None)).Result).Value);
        var detail = (await controller.GetById(conversationId, CancellationToken.None)).Value!;

        foreach (var turns in new[] { flat.Items, (IReadOnlyList<SessionTurn>)detail.Turns })
        {
            var n = Assert.Single(turns, t => t.AgentTurnId == newer.AgentTurnId);
            Assert.Equal(32_768, n.TokensCacheRead);
            Assert.Equal(1_024, n.TokensCacheWrite);
            var o = Assert.Single(turns, t => t.AgentTurnId == older.AgentTurnId);
            Assert.Null(o.TokensCacheRead);
            Assert.Null(o.TokensCacheWrite);
        }

        // What the browser reads: present with a number, present as null when unknown.
        var json = JsonSerializer.Serialize(flat.Items);
        Assert.Contains("\"tokens_cache_read\":32768", json);
        Assert.Contains("\"tokens_cache_read\":null", json);
    }
}
