using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Identity;

namespace nashira_backend.Tests;

// The agent could produce documents and never read one again. `export_document` handed
// back a download link and nothing else; the only route to /api/reports was
// execute_operation against a spec, and its download endpoint returns bytes — so a PDF
// came back UTF-8-decoded into replacement characters. Asked what its own report said,
// the agent answered that it had no access to it.
//
// What these tests pin down is the round trip: a document that goes in comes back out as
// the same text, whichever of the two stores it landed in, and a format that genuinely
// cannot come back says so instead of returning mojibake.
public class ReportToolsTests
{
    private static readonly Guid ActorId = Guid.NewGuid();

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId => ActorId;
        public bool IsAuthenticated => true;
        public string? Username => "agent";
        public IReadOnlyList<string> Roles => ["operator"];
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"reports-{Guid.NewGuid()}").Options);

    private static JsonElement Args(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static Guid AddReport(
        AppDbContext db, string title, string content,
        string contentType = "text/markdown", DateTime? expiresAt = null, Guid? runId = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var id = Guid.NewGuid();
        db.ReportArtifacts.Add(new ReportArtifact
        {
            ReportArtifactId = id,
            Title = title,
            FileName = title.ToLowerInvariant().Replace(' ', '-') + ".md",
            ContentType = contentType,
            Content = bytes,
            SizeBytes = bytes.Length,
            ExpiresAt = expiresAt,
            WorkflowRunId = runId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid AddExport(AppDbContext db, string fileName, byte[] content, string contentType)
    {
        var id = Guid.NewGuid();
        db.ExportArtifacts.Add(new ExportArtifact
        {
            ExportArtifactId = id,
            FileName = fileName,
            ContentType = contentType,
            Content = content,
            SizeBytes = content.LongLength,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Fact]
    public async Task Read_report_returns_the_markdown_verbatim()
    {
        using var db = NewDb();
        var body = "# Upgrade\n\nTwo devices rebooted.\n";
        var id = AddReport(db, "Upgrade", body);

        var result = await new ReadReportHandler(db).ExecuteAsync(Args($$"""{"id":"{{id}}"}"""), default);

        Assert.True(result.GetProperty("readable").GetBoolean());
        Assert.Equal(body, Str(result, "content"));
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal("report", Str(result, "kind"));
    }

    // The complaint that started this: the document the agent generated itself is an
    // ExportArtifact, and nothing could open one. The id it was handed at creation time
    // is the id it must be able to read back with.
    [Fact]
    public async Task Read_report_opens_a_document_the_agent_exported_itself()
    {
        using var db = NewDb();
        var body = "# Incident\n\nBGP flapped on core-1.\n";
        var id = AddExport(db, "incident.md", Encoding.UTF8.GetBytes(body), "text/markdown; charset=utf-8");

        var result = await new ReadReportHandler(db).ExecuteAsync(Args($$"""{"id":"{{id}}"}"""), default);

        Assert.Equal("export", Str(result, "kind"));
        Assert.Equal(body, Str(result, "content"));
        Assert.Equal($"/api/export/{id}/download", Str(result, "download_url"));
    }

    // A PDF's bytes are a rendering. Decoding them produces a page of replacement
    // characters that reads, to a model, like a corrupted document it should try to
    // summarise anyway — so the tool must refuse rather than hand them over.
    [Fact]
    public async Task Read_report_refuses_a_pdf_without_calling_it_an_error()
    {
        using var db = NewDb();
        var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x00, 0x01, 0x02 };
        var id = AddExport(db, "report.pdf", pdf, "application/pdf");

        var result = await new ReadReportHandler(db).ExecuteAsync(Args($$"""{"id":"{{id}}"}"""), default);

        Assert.False(result.GetProperty("readable").GetBoolean());
        Assert.Equal($"/api/export/{id}/download", Str(result, "download_url"));
        Assert.Contains("cannot be read back", Str(result, "note"));
        // An `error` field is what ToolDispatcher reads as a failed call worth
        // self-correcting; there is nothing here to correct.
        Assert.False(result.TryGetProperty("error", out _));
    }

    // A text/plain label on binary bytes is a claim, not a fact.
    [Fact]
    public async Task Read_report_refuses_binary_bytes_that_claim_to_be_text()
    {
        using var db = NewDb();
        var id = AddExport(db, "sneaky.txt", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x11], "text/plain");

        var result = await new ReadReportHandler(db).ExecuteAsync(Args($$"""{"id":"{{id}}"}"""), default);

        Assert.False(result.GetProperty("readable").GetBoolean());
    }

    [Fact]
    public async Task A_long_report_pages_instead_of_truncating_silently()
    {
        using var db = NewDb();
        var body = new string('x', 3000);
        var id = AddReport(db, "Long", body);
        var handler = new ReadReportHandler(db);

        var first = await handler.ExecuteAsync(Args($$"""{"id":"{{id}}","max_chars":2000}"""), default);
        Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.Equal(2000, first.GetProperty("chars_returned").GetInt32());
        Assert.Equal(3000, first.GetProperty("total_chars").GetInt32());
        var next = first.GetProperty("next_offset").GetInt32();
        Assert.Equal(2000, next);

        var second = await handler.ExecuteAsync(
            Args($$"""{"id":"{{id}}","offset":{{next}},"max_chars":2000}"""), default);
        Assert.False(second.GetProperty("truncated").GetBoolean());
        Assert.Equal(1000, second.GetProperty("chars_returned").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_offset").ValueKind);
        Assert.Equal(body, Str(first, "content") + Str(second, "content"));
    }

    // Retention is a deletion the hourly sweeper has not performed yet. Reading through
    // it would let the agent quote a document the platform considers gone.
    [Fact]
    public async Task An_expired_report_is_not_readable()
    {
        using var db = NewDb();
        var id = AddReport(db, "Old", "# Old", expiresAt: DateTime.UtcNow.AddDays(-1));

        var result = await new ReadReportHandler(db).ExecuteAsync(Args($$"""{"id":"{{id}}"}"""), default);

        Assert.Contains("expired", Str(result, "error"));
    }

    [Fact]
    public async Task An_unknown_id_names_list_reports_rather_than_failing_blankly()
    {
        using var db = NewDb();
        var result = await new ReadReportHandler(db)
            .ExecuteAsync(Args($$"""{"id":"{{Guid.NewGuid()}}"}"""), default);
        Assert.Contains("list_reports", Str(result, "error"));
    }

    [Fact]
    public async Task List_reports_covers_both_stores_and_flags_what_can_be_read()
    {
        using var db = NewDb();
        AddReport(db, "Weekly audit", "# Weekly audit");
        AddExport(db, "devices.pdf", [0x25, 0x50, 0x44, 0x46, 0x00], "application/pdf");
        var handler = new ListReportsHandler(db);

        var reportsOnly = await handler.ExecuteAsync(Args("{}"), default);
        Assert.Equal(1, reportsOnly.GetProperty("count").GetInt32());

        var all = await handler.ExecuteAsync(Args("""{"source":"all"}"""), default);
        Assert.Equal(2, all.GetProperty("count").GetInt32());
        var items = all.GetProperty("reports").EnumerateArray().ToList();
        Assert.Contains(items, i => Str(i, "kind") == "report" && i.GetProperty("readable").GetBoolean());
        Assert.Contains(items, i => Str(i, "kind") == "export" && !i.GetProperty("readable").GetBoolean());
        Assert.Contains("readable=false", Str(all, "note"));
    }

    [Fact]
    public async Task List_reports_filters_by_title_and_by_run()
    {
        using var db = NewDb();
        var runId = Guid.NewGuid();
        AddReport(db, "Core upgrade", "# a", runId: runId);
        AddReport(db, "Edge audit", "# b");

        var handler = new ListReportsHandler(db);

        var searched = await handler.ExecuteAsync(Args("""{"search":"UPGRADE"}"""), default);
        Assert.Equal(1, searched.GetProperty("count").GetInt32());

        var byRun = await handler.ExecuteAsync(Args($$"""{"workflow_run_id":"{{runId}}"}"""), default);
        Assert.Equal(1, byRun.GetProperty("count").GetInt32());
        Assert.Equal("Core upgrade", Str(byRun.GetProperty("reports")[0], "title"));
    }

    [Fact]
    public async Task An_expired_report_is_hidden_from_the_listing_unless_asked_for()
    {
        using var db = NewDb();
        AddReport(db, "Gone", "# gone", expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var handler = new ListReportsHandler(db);
        Assert.Equal(0, (await handler.ExecuteAsync(Args("{}"), default)).GetProperty("count").GetInt32());
        Assert.Equal(1, (await handler.ExecuteAsync(Args("""{"include_expired":true}"""), default))
            .GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Save_report_round_trips_through_read_report()
    {
        using var db = NewDb();
        var body = "# Handover\n\n| device | state |\n|---|---|\n| core-1 | ok |\n";
        var saved = await new SaveReportHandler(db, new FakeUser())
            .ExecuteAsync(Args(JsonSerializer.Serialize(new { title = "Handover", content = body })), default);

        var id = saved.GetProperty("report_artifact_id").GetGuid();
        Assert.Equal("handover.md", Str(saved, "file_name"));
        Assert.Equal(ActorId, db.ReportArtifacts.Single().CreatedBy);

        var read = await new ReadReportHandler(db).ExecuteAsync(Args($$"""{"id":"{{id}}"}"""), default);
        Assert.Equal(body, Str(read, "content"));
    }

    // A run id the agent inferred rather than read produces a report attributed to
    // nothing, which then never surfaces under that run again.
    [Fact]
    public async Task Save_report_rejects_a_workflow_run_that_does_not_exist()
    {
        using var db = NewDb();
        var result = await new SaveReportHandler(db, new FakeUser()).ExecuteAsync(
            Args(JsonSerializer.Serialize(new
            {
                title = "x",
                content = "y",
                workflow_run_id = Guid.NewGuid().ToString(),
            })), default);

        Assert.Contains("no workflow run", Str(result, "error"));
        Assert.Empty(db.ReportArtifacts);
    }

    [Fact]
    public async Task Save_report_honours_retention()
    {
        using var db = NewDb();
        var result = await new SaveReportHandler(db, new FakeUser()).ExecuteAsync(
            Args(JsonSerializer.Serialize(new { title = "Temp", content = "# temp", retain_days = 7 })), default);

        var expires = result.GetProperty("expires_at").GetDateTime();
        Assert.InRange(expires, DateTime.UtcNow.AddDays(6.9), DateTime.UtcNow.AddDays(7.1));
    }

    // Re-reading a document the caller could have downloaded from /reports anyway must
    // not raise a confirmation dialog: that is the friction that teaches people to
    // approve without reading. Saving one persists a row, so it confirms.
    [Fact]
    public void The_reading_tools_are_autonomous_and_saving_confirms()
    {
        Assert.Equal(PermissionClassifier.TierAutonomous, PermissionClassifier.Matrix["list_reports"].Tier);
        Assert.Equal(PermissionClassifier.TierAutonomous, PermissionClassifier.Matrix["read_report"].Tier);
        Assert.Equal("read", PermissionClassifier.Matrix["read_report"].Level);
        Assert.Equal(PermissionClassifier.TierSingleConfirm, PermissionClassifier.Matrix["save_report"].Tier);
        Assert.Equal("write", PermissionClassifier.Matrix["save_report"].Level);
    }
}
