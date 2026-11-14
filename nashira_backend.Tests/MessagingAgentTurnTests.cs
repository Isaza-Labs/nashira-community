using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Data.Models;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Messaging;

namespace nashira_backend.Tests;

// A message that arrives from a chat channel runs the same agent loop as the web chat,
// with nothing to stream into: the turn is recorded like any other, under the linked
// user, and what the model said is queued as one reply.
public class MessagingAgentTurnTests
{
    [Fact]
    public async Task A_channel_message_is_answered_and_recorded_under_the_linked_user()
    {
        var body = RunnerTestKit.Sse(
            "{\"choices\":[{\"delta\":{\"content\":\"Two devices are down.\"},\"finish_reason\":\"stop\"}]}",
            "{\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5,"
            + "\"prompt_tokens_details\":{\"cached_tokens\":80}}}",
            "[DONE]");
        var caller = new MutableCurrentUser();
        await using var kit = new RunnerTestKit(new ScriptedHttp(body), caller: caller);
        var (job, user) = await InboundMessageAsync(kit, "which devices are down?");

        await Processor(kit, caller).ProcessAsync(job, CancellationToken.None);

        var reply = await QueuedReplyAsync(kit);
        Assert.Equal("Two devices are down.", reply.Text);
        Assert.Equal("thread-1", reply.ExternalThreadId);

        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusCompleted, turn.Status);
        Assert.Equal(user.UserId, turn.UserId);
        Assert.Equal("which devices are down?", turn.UserMessage);
        Assert.Equal((100, 5, 80), (turn.TokensIn, turn.TokensOut, turn.TokensCacheRead));
        Assert.Equal(["operator"], caller.Roles);
    }

    [Fact]
    public async Task A_channel_message_whose_model_call_breaks_gets_an_apology_and_an_error_turn()
    {
        // The connection breaks before the model has said anything.
        var body = new ScriptedBody(string.Empty, ScriptedBody.Ending.Break);
        var caller = new MutableCurrentUser();
        await using var kit = new RunnerTestKit(new ScriptedHttp(body), caller: caller);
        var (job, _) = await InboundMessageAsync(kit, "hello");

        await Processor(kit, caller).ProcessAsync(job, CancellationToken.None);

        var reply = await QueuedReplyAsync(kit);
        Assert.StartsWith("Sorry", reply.Text);
        Assert.Equal(AgentTurn.StatusError, (await kit.StoredTurnAsync()).Status);
        Assert.Contains(" outcome=failed", Assert.Single(kit.Log.Rounds));
    }

    private static MessagingJobProcessor Processor(RunnerTestKit kit, MutableCurrentUser caller) =>
        // Only the agent_message path runs here; sending is the provider's and the
        // secret store's business, and neither is reached.
        new(kit.Db, null!, null!, kit.Runner, caller,
            Options.Create(new MessagingOptions()), NullLogger<MessagingJobProcessor>.Instance);

    // The rows the webhook ingest leaves behind for one inbound message, and its job.
    private static async Task<(Job Job, User User)> InboundMessageAsync(RunnerTestKit kit, string text)
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            UserId = Guid.NewGuid(), Username = "ana", Email = "ana@example.invalid", Role = "operator",
            IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        var channel = new MessagingChannel
        {
            MessagingChannelId = Guid.NewGuid(), Provider = MessagingChannel.ProviderSlack, Name = "ops",
            Slug = "ops", Enabled = true, IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        var conversation = new AIConversation
        {
            AIConversationId = Guid.NewGuid(), UserId = user.UserId, MessagesJson = "[]", Status = "active",
            Source = channel.Provider, MessagingChannelId = channel.MessagingChannelId,
            ExternalThreadId = "thread-1", IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        kit.Db.AddRange(user, channel, conversation);
        await kit.Db.SaveChangesAsync();

        var job = new Job
        {
            JobId = Guid.NewGuid(),
            Type = MessagingJobTypes.AgentMessage,
            PayloadJson = MessagingJobJson.Serialize(new AgentMessagePayload
            {
                ChannelId = channel.MessagingChannelId,
                ConversationId = conversation.AIConversationId,
                ExternalThreadId = "thread-1",
                Text = text,
                LinkedUserId = user.UserId,
            }),
        };
        return (job, user);
    }

    private static async Task<SendPayload> QueuedReplyAsync(RunnerTestKit kit)
    {
        var send = Assert.Single(await kit.Db.Jobs.Where(j => j.Type == MessagingJobTypes.Send).ToListAsync());
        Assert.Equal(Job.StatusQueued, send.Status);
        return MessagingJobJson.Deserialize<SendPayload>(send.PayloadJson)!;
    }
}
