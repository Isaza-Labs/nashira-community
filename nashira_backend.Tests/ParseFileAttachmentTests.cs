using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Files;

namespace nashira_backend.Tests;

// parse_file's `attachment` route: the current message's files come from the turn
// scope (registered by the runner), earlier messages' files from the persisted
// conversation_attachments rows. This is the only way the agent can reach a binary
// attachment (xlsx), so the error messages double as the model's instructions —
// they must name what IS available.
public class ParseFileAttachmentTests
{
    private static (ParseFileHandler Handler, AgentTurnScope Scope, AppDbContext Db) Build()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"parse-file-{Guid.NewGuid()}")
            .Options);
        var scope = new AgentTurnScope();
        return (new ParseFileHandler(new FileParsingService(), scope, db), scope, db);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static byte[] Xlsx(params (string A, string B)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 1, 1).Value = rows[i].A;
            ws.Cell(i + 1, 2).Value = rows[i].B;
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Parses_an_xlsx_attachment_by_filename()
    {
        var (handler, scope, _) = Build();
        scope.AddAttachment("matrix.xlsx", Xlsx(("feature", "tested"), ("sync", "no")));

        var result = await handler.ExecuteAsync(
            Args("""{"format":"xlsx","attachment":"matrix.xlsx"}"""), default);

        Assert.Equal("xlsx", result.GetProperty("format").GetString());
        Assert.Equal(1, result.GetProperty("row_count").GetInt32());
        Assert.Equal("sync", result.GetProperty("rows")[0].GetProperty("feature").GetString());
    }

    [Fact]
    public async Task Attachment_lookup_is_case_insensitive()
    {
        var (handler, scope, _) = Build();
        scope.AddAttachment("Matrix.XLSX", Xlsx(("a", "b")));

        var result = await handler.ExecuteAsync(
            Args("""{"format":"xlsx","attachment":"matrix.xlsx"}"""), default);
        Assert.False(result.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Parses_a_csv_attachment_through_the_text_path()
    {
        var (handler, scope, _) = Build();
        scope.AddAttachment("devices.csv", Encoding.UTF8.GetBytes("name,ip\nsw1,10.0.0.1\n"));

        var result = await handler.ExecuteAsync(
            Args("""{"format":"csv","attachment":"devices.csv"}"""), default);

        Assert.Equal(1, result.GetProperty("row_count").GetInt32());
        Assert.Equal("10.0.0.1", result.GetProperty("rows")[0].GetProperty("ip").GetString());
    }

    [Fact]
    public async Task Reads_an_attachment_persisted_by_an_earlier_turn()
    {
        var (handler, scope, db) = Build();
        var convId = Guid.NewGuid();
        scope.ConversationId = convId; // set by the runner on every turn
        db.ConversationAttachments.Add(new ConversationAttachment
        {
            ConversationAttachmentId = Guid.NewGuid(),
            ConversationId = convId,
            Filename = "matrix.xlsx",
            Content = Xlsx(("feature", "tested"), ("sync", "yes")),
            SizeBytes = 1,
        });
        await db.SaveChangesAsync();

        // Nothing on the turn scope: this turn had no attachments of its own.
        var result = await handler.ExecuteAsync(
            Args("""{"format":"xlsx","attachment":"MATRIX.xlsx"}"""), default);

        Assert.Equal(1, result.GetProperty("row_count").GetInt32());
        Assert.Equal("yes", result.GetProperty("rows")[0].GetProperty("tested").GetString());
    }

    [Fact]
    public async Task Missing_attachment_names_what_is_available_across_turns()
    {
        var (handler, scope, db) = Build();
        var convId = Guid.NewGuid();
        scope.ConversationId = convId;
        scope.AddAttachment("current.csv", Encoding.UTF8.GetBytes("a\n1\n"));
        db.ConversationAttachments.Add(new ConversationAttachment
        {
            ConversationAttachmentId = Guid.NewGuid(),
            ConversationId = convId,
            Filename = "earlier.xlsx",
            Content = [1],
            SizeBytes = 1,
        });
        await db.SaveChangesAsync();

        var result = await handler.ExecuteAsync(
            Args("""{"format":"xlsx","attachment":"wrong.xlsx"}"""), default);

        var error = result.GetProperty("error").GetString();
        Assert.Contains("current.csv", error);
        Assert.Contains("earlier.xlsx", error);
    }

    [Fact]
    public async Task No_attachments_at_all_says_to_re_attach()
    {
        var (handler, scope, _) = Build();
        scope.ConversationId = Guid.NewGuid();

        var result = await handler.ExecuteAsync(
            Args("""{"format":"xlsx","attachment":"gone.xlsx"}"""), default);

        var error = result.GetProperty("error").GetString();
        Assert.Contains("no attachments", error);
    }

    [Fact]
    public async Task Content_base64_still_works_without_an_attachment()
    {
        var (handler, _, _) = Build();
        var b64 = Convert.ToBase64String(Xlsx(("h", "v"), ("x", "y")));

        var result = await handler.ExecuteAsync(
            Args($$"""{"format":"xlsx","content_base64":"{{b64}}"}"""), default);

        Assert.Equal(1, result.GetProperty("row_count").GetInt32());
    }
}
