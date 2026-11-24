using System.Text.Json;
using nashira_backend.Services.Ai.Conversation;

namespace nashira_backend.Tests;

// The history replayed to the model carries, for every reply, a server-written record
// of the tools that reply really ran: in front of the user message that followed it,
// never inside the reply, and absent for rows written before the record existed.
public class ToolRunRecordTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static AgentConversationRunner.StoredMessage User(string text) => new("user", text);
    private static AgentConversationRunner.StoredMessage Reply(string text, List<StoredToolCall>? tools) =>
        new("assistant", text, Tools: tools);

    [Fact]
    public void A_reply_that_ran_nothing_says_so()
    {
        Assert.Contains("it ran no tools", ToolRunRecord.Render([]));
    }

    [Fact]
    public void A_reply_lists_its_calls_and_counts_failures()
    {
        var record = ToolRunRecord.Render(
        [
            new("execute_operation", true, "{\"op\":\"dcim_interfaces_create\"}", "{\"status\":201,\"id\":95}"),
            new("execute_operation", false, "{\"op\":\"ipam_ip_addresses_create\"}", "{\"error\":\"400\"}"),
        ]);

        Assert.Contains("tools actually run: 2 (1 ok, 1 failed)", record);
        Assert.Contains("\"id\":95", record);
        Assert.Contains("FAILED", record);
    }

    [Fact]
    public void A_long_run_is_capped_and_the_rest_counted()
    {
        var calls = Enumerable.Range(0, 200)
            .Select(i => new StoredToolCall("execute_operation", true, $"{{\"i\":{i}}}", new string('x', 150)))
            .ToList();

        var record = ToolRunRecord.Render(calls);

        Assert.True(record.Length <= ToolRunRecord.MaxRenderChars + 80);
        Assert.Contains("more calls not listed", record);
    }

    [Fact]
    public void Secrets_in_arguments_are_not_kept()
    {
        var stored = ToolRunRecord.FromTelemetry(
            [new ToolTelemetryEntry("set_secret", J("{\"name\":\"db\",\"value\":\"hunter2\"}"), true, J("{\"ok\":true}"), 3)]);

        Assert.DoesNotContain("hunter2", stored[0].Args);
        Assert.Contains(ToolTelemetry.RedactedMarker, stored[0].Args);
    }

    [Fact]
    public void Each_record_goes_in_front_of_the_message_after_its_reply()
    {
        var (kept, omitted, trailing) = AgentConversationRunner.ReplayHistory(
        [
            User("carga KAWAK"), Reply("Creo las 18 interfaces.", []),
            User("continúa"), Reply("Hecho.", [new("execute_operation", true, "{}", "{\"id\":1}")]),
        ], charBudget: 0);

        Assert.Equal(0, omitted);
        Assert.Equal("carga KAWAK", kept[0].Content);
        Assert.Equal("Creo las 18 interfaces.", kept[1].Content);
        Assert.StartsWith("[Nashira server record of your previous reply: it ran no tools", kept[2].Content);
        Assert.EndsWith("continúa", kept[2].Content);
        Assert.Equal("Hecho.", kept[3].Content);
        Assert.NotNull(trailing);
        Assert.Contains("tools actually run: 1", trailing);
    }

    [Fact]
    public void Rows_written_before_the_record_existed_get_none()
    {
        var (kept, _, trailing) = AgentConversationRunner.ReplayHistory(
            [User("hola"), Reply("Hola.", null), User("sigue"), Reply("Ok.", null)], charBudget: 0);

        Assert.Equal("sigue", kept[2].Content);
        Assert.Null(trailing);
    }

    [Fact]
    public void A_kept_message_whose_reply_fell_out_of_the_window_loses_its_record()
    {
        var (kept, omitted, _) = AgentConversationRunner.ReplayHistory(
        [
            User(new string('u', 500)), Reply(new string('a', 500), []),
            User("continúa"), Reply("Ok.", []),
        ], charBudget: 300);

        Assert.Equal(2, omitted);
        Assert.Equal("continúa", kept[0].Content);
    }
}
