using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using SnippetEntity = nashira_backend.Data.Models.Snippet;

namespace nashira_backend.Tests;

// The gate that asks whether a definition's nodes point at anything real.
//
// The schema validator answers shape and acyclicity, and both were true of a workflow
// whose only meaningful node carried `snippet_id: "__ping__"` — a string nobody had
// ever defined. It stored, it ran, and every node reported no_change, which reads
// exactly like a run that did its job and found nothing to change.
public class WorkflowReferenceTests
{
    private static (WorkflowReferenceChecker Checker, AppDbContext Db) New(
        params string[] knownTypes)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"refs-{Guid.NewGuid()}")
            .Options);
        var registry = new FakeRegistry(
            knownTypes.Length == 0 ? [SnippetEntity.TypePing] : knownTypes);
        return (new WorkflowReferenceChecker(db, registry), db);
    }

    private static JsonElement Nodes(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Guid Seed(AppDbContext db, bool active = true)
    {
        var id = Guid.NewGuid();
        db.Snippets.Add(new SnippetEntity
        {
            SnippetId = id,
            Name = "ping-probe",
            Type = SnippetEntity.TypePing,
            IsActive = active,
        });
        db.SaveChanges();
        return id;
    }

    // The exact definition the agent authored.
    [Fact]
    public async Task An_invented_literal_is_rejected()
    {
        var (checker, _) = New();

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            checker.EnsureResolvableAsync(Nodes(
                """[{"id":"start","snippet_id":"__start__"},{"id":"ping_4222","snippet_id":"__ping__"}]""")));

        Assert.Equal("snippet_reference_invalid", ex.Code);
        // Both halves matter: which node, and what it said.
        Assert.Contains("ping_4222", ex.Message);
        Assert.Contains("__ping__", ex.Message);
    }

    [Fact]
    public async Task A_well_formed_uuid_with_no_snippet_behind_it_is_rejected()
    {
        var (checker, _) = New();
        var orphan = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            checker.EnsureResolvableAsync(Nodes(
                $$"""[{"id":"n","snippet_id":"{{orphan}}"}]""")));

        Assert.Equal("snippet_not_found", ex.Code);
        Assert.Contains(orphan.ToString(), ex.Message);
    }

    // Soft-deleted is gone as far as a definition is concerned: the run would fail on
    // it, so writing it should not succeed either.
    [Fact]
    public async Task A_soft_deleted_snippet_does_not_resolve()
    {
        var (checker, db) = New();
        var id = Seed(db, active: false);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            checker.EnsureResolvableAsync(Nodes($$"""[{"id":"n","snippet_id":"{{id}}"}]""")));

        Assert.Equal("snippet_not_found", ex.Code);
    }

    [Fact]
    public async Task A_real_snippet_resolves()
    {
        var (checker, db) = New();
        var id = Seed(db);

        await checker.EnsureResolvableAsync(Nodes(
            $$"""[{"id":"start","snippet_id":"__start__"},{"id":"p","snippet_id":"{{id}}"},{"id":"end","snippet_id":"__end__"}]"""));
    }

    [Fact]
    public async Task A_snippet_whose_handler_is_disabled_does_not_resolve()
    {
        var (checker, db) = New("transform");
        var id = Seed(db);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            checker.EnsureResolvableAsync(Nodes($$"""[{"id":"p","snippet_id":"{{id}}"}]""")));

        Assert.Equal("snippet_type_unavailable", ex.Code);
        Assert.Contains("ping", ex.Message);
    }

    // All three literals pass, including `subflow`: the schema accepts it, so refusing
    // to store it would make the API and workflow.v1 disagree. It is the run that
    // reports it skipped.
    [Theory]
    [InlineData("__start__")]
    [InlineData("__end__")]
    [InlineData("subflow")]
    public async Task The_documented_literals_pass(string literal)
    {
        var (checker, _) = New();

        await checker.EnsureResolvableAsync(Nodes($$"""[{"id":"n","snippet_id":"{{literal}}"}]"""));
    }

    // One message, every offender — an author fixing a definition should not have to
    // resubmit it once per broken node to discover the next one.
    [Fact]
    public async Task Every_bad_reference_is_reported_at_once()
    {
        var (checker, _) = New();

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            checker.EnsureResolvableAsync(Nodes(
                """[{"id":"a","snippet_id":"__ping__"},{"id":"b","snippet_id":"__ssh__"}]""")));

        Assert.Contains("a", ex.Message);
        Assert.Contains("b", ex.Message);
        Assert.Contains("__ping__", ex.Message);
        Assert.Contains("__ssh__", ex.Message);
        Assert.Contains("2 nodes reference", ex.Message);
    }

    [Fact]
    public async Task An_empty_definition_is_not_an_error()
    {
        var (checker, _) = New();

        await checker.EnsureResolvableAsync(Nodes("[]"));
    }

    private sealed class FakeRegistry(IReadOnlyCollection<string> knownTypes)
        : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes => knownTypes;
        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => null;
    }
}
