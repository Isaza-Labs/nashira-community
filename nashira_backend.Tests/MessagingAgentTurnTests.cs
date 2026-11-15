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

    // A turn that fails after the model has started answering leaves part of an answer.
    // The web chat shows the error beside it; a channel gets one message, so the part
    // goes out with a line saying it may be incomplete, in each way a turn can fail.

    [Fact]
    public async Task A_reply_whose_connection_breaks_part_way_is_sent_with_a_note_that_it_may_be_incomplete()
    {
        var body = new ScriptedBody(
            RunnerTestKit.Sse("{\"choices\":[{\"delta\":{\"content\":\"Two devices are\"}}]}"),
            ScriptedBody.Ending.Break);
        var caller = new MutableCurrentUser();
        await using var kit = new RunnerTestKit(new ScriptedHttp(body), caller: caller);
        var (job, _) = await InboundMessageAsync(kit, "which devices are down?");

        await Processor(kit, caller).ProcessAsync(job, CancellationToken.None);

        Assert.Equal(CollectingAgentEventSink.IncompleteNote + "\n\nTwo devices are", (await QueuedReplyAsync(kit)).Text);
        Assert.Equal(AgentTurn.StatusError, (await kit.StoredTurnAsync()).Status);
    }

    [Fact]
    public async Task A_reply_that_goes_silent_part_way_is_sent_with_a_note_that_it_may_be_incomplete()
    {
        // The server stops sending with the connection open; the provider's limit is one second.
        var body = new ScriptedBody(
            RunnerTestKit.Sse("{\"choices\":[{\"delta\":{\"content\":\"Two devices are\"}}]}"),
            ScriptedBody.Ending.Hang);
        var caller = new MutableCurrentUser();
        await using var kit = new RunnerTestKit(
            new ScriptedHttp(body), caller: caller, providerConfig: """{"stream_idle_seconds":1}""");
        var (job, _) = await InboundMessageAsync(kit, "which devices are down?");

        await Processor(kit, caller).ProcessAsync(job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CollectingAgentEventSink.IncompleteNote + "\n\nTwo devices are", (await QueuedReplyAsync(kit)).Text);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusError, turn.Status);
        Assert.Contains("stopped sending", turn.Error);
    }

    [Fact]
    public async Task A_reply_cut_off_by_the_deadline_is_sent_with_a_note_that_it_may_be_incomplete()
    {
        // No idle limit: the silent server holds the turn until its deadline, two seconds here.
        var body = new ScriptedBody(
            RunnerTestKit.Sse("{\"choices\":[{\"delta\":{\"content\":\"Two devices are\"}}]}"),
            ScriptedBody.Ending.Hang);
        var caller = new MutableCurrentUser();
        await using var kit = new RunnerTestKit(new ScriptedHttp(body), deadlineSeconds: 2, caller: caller);
        var (job, _) = await InboundMessageAsync(kit, "which devices are down?");

        await Processor(kit, caller).ProcessAsync(job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12));

        Assert.Equal(CollectingAgentEventSink.IncompleteNote + "\n\nTwo devices are", (await QueuedReplyAsync(kit)).Text);
        Assert.Equal(AgentTurn.StatusTimeout, (await kit.StoredTurnAsync()).Status);
    }

    // The sink's own choice of what to send, without a turn around it.
    [Fact]
    public async Task The_reply_text_carries_the_note_only_when_text_and_an_error_are_both_present()
    {
        var none = CancellationToken.None;

        var finished = new CollectingAgentEventSink();
        await finished.TextAsync("All four are up.", none);
        Assert.Equal("All four are up.", finished.ReplyText());

        var partWay = new CollectingAgentEventSink();
        await partWay.TextAsync("All four", none);
        await partWay.ErrorAsync("the connection was reset", null, none);
        Assert.Equal(CollectingAgentEventSink.IncompleteNote + "\n\nAll four", partWay.ReplyText());

        var nothingSaid = new CollectingAgentEventSink();
        await nothingSaid.ErrorAsync("the connection was reset", null, none);
        Assert.StartsWith("Sorry", nothingSaid.ReplyText());
        Assert.DoesNotContain(CollectingAgentEventSink.IncompleteNote, nothingSaid.ReplyText());

        // A confirmation request still wins over text and error alike.
        var needsApproval = new CollectingAgentEventSink();
        await needsApproval.TextAsync("I will restart it.", none);
        await needsApproval.ConfirmationRequiredAsync("restart_device", default, "single_confirm", none);
        await needsApproval.ErrorAsync("the connection was reset", null, none);
        Assert.Contains("needs confirmation", needsApproval.ReplyText());
        Assert.DoesNotContain(CollectingAgentEventSink.IncompleteNote, needsApproval.ReplyText());
    }

    // Telegram and WhatsApp send only the first 4,096 characters of a message. The
    // note has to be in what is left of a long reply, or the user gets a cut-off
    // answer with nothing to say so.
    [Fact]
    public async Task The_note_is_still_there_after_Telegram_shortens_a_long_reply()
    {
        var sent = new SentBodies();
        var channel = new MessagingChannel { MessagingChannelId = Guid.NewGuid(), Provider = MessagingChannel.ProviderTelegram, Name = "t", Slug = "t" };

        await new nashira_backend.Services.Messaging.Providers.TelegramProvider(
                sent, NullLogger<nashira_backend.Services.Messaging.Providers.TelegramProvider>.Instance)
            .SendAsync(channel, "token", new OutboundMessage { ExternalThreadId = "42", Text = await LongReplyThatFailedAsync() }, CancellationToken.None);

        var text = System.Text.Json.JsonDocument.Parse(Assert.Single(sent.Bodies)).RootElement.GetProperty("text").GetString()!;
        Assert.Equal(4096, text.Length);
        Assert.Contains(CollectingAgentEventSink.IncompleteNote, text);
    }

    [Fact]
    public async Task The_note_is_still_there_after_WhatsApp_shortens_a_long_reply()
    {
        var sent = new SentBodies();
        var channel = new MessagingChannel
        {
            MessagingChannelId = Guid.NewGuid(), Provider = MessagingChannel.ProviderWhatsApp, Name = "w", Slug = "w",
            ExternalConfigJson = """{"phone_number_id":"1"}""",
        };

        await new nashira_backend.Services.Messaging.Providers.WhatsAppProvider(
                sent, NullLogger<nashira_backend.Services.Messaging.Providers.WhatsAppProvider>.Instance)
            .SendAsync(channel, "token", new OutboundMessage { ExternalThreadId = "15550001111", Text = await LongReplyThatFailedAsync() }, CancellationToken.None);

        var text = System.Text.Json.JsonDocument.Parse(Assert.Single(sent.Bodies)).RootElement.GetProperty("text").GetProperty("body").GetString()!;
        Assert.Equal(4096, text.Length);
        Assert.Contains(CollectingAgentEventSink.IncompleteNote, text);
    }

    // What the sink hands over for a reply of 5,000 characters that then failed.
    private static async Task<string> LongReplyThatFailedAsync()
    {
        var sink = new CollectingAgentEventSink();
        await sink.TextAsync(new string('a', 5000), CancellationToken.None);
        await sink.ErrorAsync("the connection was reset", null, CancellationToken.None);
        return sink.ReplyText();
    }

    // Answers every request with 200 and keeps what was sent.
    private sealed class SentBodies : IHttpClientFactory
    {
        public List<string> Bodies { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(SentBodies owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
            }
        }
    }

    private static MessagingJobProcessor Processor(RunnerTestKit kit, MutableCurrentUser caller) =>
        // Only the agent_message path runs here; sending is the provider's and the
        // secret store's business, and neither is reached.
        new(kit.Db, null!, null!, kit.Runner, caller,
            Options.Create(new MessagingOptions()), NullLogger<MessagingJobProcessor>.Instance);

    // The user, channel and conversation the webhook ingest leaves behind for one inbound
    // message, and its job. The inbound audit row is left out, so the claim on it is
    // not exercised here: it is a bulk update, which EF InMemory cannot run.
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
