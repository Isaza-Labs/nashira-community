using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Data.DTos.Chat;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// A chat turn is not a CRUD mutation; the tools it invokes are already audited by the
// ToolDispatcher, so the turn itself is kept out of the CRUD audit trail.
[ApiController]
[Route("api/ai")]
[Authorize(Policy = "Viewer")]
[SkipAudit]
[EnableRateLimiting(RateLimitingConfiguration.AiChat)]
public class AiChatController : ControllerBase
{
    private readonly AgentConversationRunner _runner;

    public AiChatController(AgentConversationRunner runner) => _runner = runner;

    // POST /api/ai/chat — runs one agent turn and streams it as Server-Sent
    // Events (frames: conversation, token, tool_start, tool_result, done, error).
    [HttpPost("chat")]
    public async Task Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = "message is required" }, ct);
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering

        var sink = new SseAgentEventSink(Response.Body);
        await _runner.RunAsync(
            new AgentTurnRequest(
                request.ConversationId, request.Message, request.Approvals, request.Attachments,
                request.ProviderId, request.Model),
            sink, ct);
    }
}
