using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Worker;
using SnippetEntity = nashira_backend.Data.Models.Snippet;

namespace nashira_backend.Tests;

public class ModuleSnippetVisibilityTests
{
    [Fact]
    public async Task Agent_catalog_hides_rows_whose_handler_is_not_registered()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"snippet-visibility-{Guid.NewGuid()}")
            .Options);
        db.Snippets.AddRange(
            Snippet("base-transform", SnippetEntity.TypeTransform),
            Snippet("disabled-git", SnippetEntity.TypeGit));
        await db.SaveChangesAsync();

        var registry = new FakeRegistry([SnippetEntity.TypeTransform]);
        var services = new ServiceCollection().BuildServiceProvider();
        var result = await new ListSnippetsHandler(
                db, registry, services, NullLogger<ListSnippetsHandler>.Instance)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), default);

        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal("base-transform", item.GetProperty("name").GetString());
        Assert.Equal(1, result.GetProperty("total").GetInt32());
    }

    private static SnippetEntity Snippet(string name, string type) => new()
    {
        SnippetId = Guid.NewGuid(),
        Name = name,
        Slug = name,
        Type = type,
        IsActive = true,
    };

    private sealed class FakeRegistry(IReadOnlyCollection<string> knownTypes)
        : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes => knownTypes;
        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => null;
    }
}
