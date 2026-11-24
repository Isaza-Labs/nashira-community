using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;

namespace nashira_backend.Tests;

// Turns that say they are doing something and then call nothing. In a long NetBox load,
// 55 turns ended on a single round like "Verifico el estado real antes de continuar."
// with no tool call; the loop read that as the final answer and recorded the turn as
// completed, and later turns reported objects those turns never created. And because
// the history kept only text, the model had no way to tell what had really run.
public class UnrunActionTests
{
    // ---- the detector ----------------------------------------------------------------

    [Theory]
    // Taken from the conversation that showed the problem.
    [InlineData("Verifico el estado real antes de continuar, para no duplicar.")]
    [InlineData("Creando las 8 interfaces restantes de HOMI.")]
    [InlineData("Confirmado: faltan las interfaces de los devices 62-69. Las creo ahora (8 escrituras).")]
    [InlineData("Marcando los 9 `primary_ip4` restantes de KAWAK con el mapeo ya verificado.")]
    [InlineData("Confirmado: KAWAK tiene 0 devices todavía. Creo los 18 ahora.")]
    [InlineData("Releo la hoja HOMI para confirmar las IPs exactas de cada equipo antes de crearlas.")]
    [InlineData("Let me check the device list first.")]
    [InlineData("Creating the 5 missing IP addresses.")]
    public void A_round_that_only_announces_work_is_caught(string text) =>
        Assert.True(ActionAnnouncement.Unrun(text, turnRanTools: false));

    [Theory]
    [InlineData("Gracias por las correcciones. Actualizo: la IP de Country es 10.101.30.201.")]
    [InlineData("Creo que el rol monitoring-server es más claro que sonda.")]
    [InlineData("NetBox stores roles by function, not by whether a device is virtual.")]
    [InlineData("")]
    public void An_ordinary_answer_is_not(string text) =>
        Assert.False(ActionAnnouncement.Unrun(text, turnRanTools: false));

    [Fact]
    public void After_tools_ran_only_the_closing_paragraph_counts()
    {
        var text = "Verifiqué el mapeo.\n\nVoy a resumir lo que encontré: todo cuadra.\n\n| a | b |\n|---|---|\n\nTodo quedó creado.";
        Assert.False(ActionAnnouncement.Unrun(text, turnRanTools: true));

        var trailing = "Las 14 interfaces quedaron creadas.\n\nAhora marco las 22 IPs como primary_ip4.";
        Assert.True(ActionAnnouncement.Unrun(trailing, turnRanTools: true));
    }

    // ---- the turn ----------------------------------------------------------------------

    private static string Text(string s) => RunnerTestKit.Sse(
        JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = s }, finish_reason = "stop" } } }),
        "[DONE]");

    private const string ToolRound =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"not_a_tool\",\"arguments\":\"{\\\"device\\\":42}\"}}]}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Fact]
    public async Task An_announcement_with_no_tool_gets_one_nudge_and_then_a_notice()
    {
        await using var kit = new RunnerTestKit(new ScriptedHttp(
            Text("Verifico el estado real antes de continuar."),
            Text("Verifico el estado real antes de continuar.")));

        await kit.RunAsync("continúa");

        Assert.Equal(2, kit.Http.Calls);
        Assert.Contains("Never report the result of an action that no tool ran", kit.Http.Requests[1]);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusUnrunAction, turn.Status);
        Assert.EndsWith(ActionAnnouncement.UnrunNotice, turn.AssistantText);
        Assert.Contains(ActionAnnouncement.UnrunNotice, string.Concat(kit.Sink.Text));
    }

    [Fact]
    public async Task A_nudged_model_that_then_calls_the_tool_completes_normally()
    {
        await using var kit = new RunnerTestKit(new ScriptedHttp(
            Text("Creando las 8 interfaces restantes."),
            ToolRound,
            Text("Listo.")));

        await kit.RunAsync("continúa");

        Assert.Equal(3, kit.Http.Calls);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusCompleted, turn.Status);
        Assert.DoesNotContain(ActionAnnouncement.UnrunNotice, turn.AssistantText);
    }

    [Fact]
    public async Task An_ordinary_answer_is_one_round_and_untouched()
    {
        await using var kit = new RunnerTestKit(new ScriptedHttp(Text("El rol monitoring-server es una buena opción.")));

        await kit.RunAsync("¿y si en role se pone monitoring-server?");

        Assert.Equal(1, kit.Http.Calls);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusCompleted, turn.Status);
        Assert.Equal("El rol monitoring-server es una buena opción.", turn.AssistantText);
    }

    [Fact]
    public async Task The_next_turn_is_told_what_the_previous_one_really_ran()
    {
        // Turn one claims work without running anything; turn two must see the record.
        await using var kit = new RunnerTestKit(new ScriptedHttp(
            Text("KAWAK: 18/18 interfaces creadas."),
            Text("Entendido.")));

        await kit.RunAsync("continúa");
        var conversation = await kit.Db.AIConversations.SingleAsync();
        await kit.Runner.RunAsync(
            new AgentTurnRequest(conversation.AIConversationId, "ya finalizó?"), kit.Sink, CancellationToken.None);

        var stored = JsonDocument.Parse((await kit.Db.AIConversations.SingleAsync()).MessagesJson).RootElement;
        Assert.Equal(0, stored[1].GetProperty("tools").GetArrayLength());
        Assert.Contains("it ran no tools, so nothing was read, created or changed in it", kit.Http.Requests[1]);
    }

    [Fact]
    public async Task A_turn_that_ran_tools_stores_them_with_its_reply()
    {
        await using var kit = new RunnerTestKit(new ScriptedHttp(ToolRound, Text("Hecho.")));

        await kit.RunAsync("revisa el device 42");

        var stored = JsonDocument.Parse((await kit.Db.AIConversations.SingleAsync()).MessagesJson).RootElement;
        var tool = Assert.Single(stored[1].GetProperty("tools").EnumerateArray());
        Assert.Equal("not_a_tool", tool.GetProperty("name").GetString());
        Assert.False(tool.GetProperty("ok").GetBoolean());
        Assert.Contains("42", tool.GetProperty("args").GetString());
    }
}
