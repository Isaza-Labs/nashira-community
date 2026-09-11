using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

// Deletes are soft everywhere, so a name/slug unique index that also covers
// inactive rows makes re-creating a deleted resource die on the index with a
// 500 after the controller's live-rows-only duplicate check passed. These pin
// which indexes are scoped to live rows — and which are unfiltered on purpose.
public class SoftDeleteUniqueIndexTests
{
    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"soft-delete-index-{Guid.NewGuid()}")
        .Options);

    [Theory]
    [InlineData(typeof(Profile), new[] { "Name" })]
    [InlineData(typeof(AIProvider), new[] { "Name" })]
    [InlineData(typeof(Device), new[] { "DeviceName" })]
    [InlineData(typeof(Credential), new[] { "Name" })]
    [InlineData(typeof(GitRepository), new[] { "Name" })]
    [InlineData(typeof(KnowledgeArticle), new[] { "Slug" })]
    [InlineData(typeof(InventorySource), new[] { "Name" })]
    [InlineData(typeof(AiPromptSkill), new[] { "Name" })]
    [InlineData(typeof(DevicePool), new[] { "Name" })]
    [InlineData(typeof(VendorCommand), new[] { "Intent", "Platform" })]
    [InlineData(typeof(NotificationChannel), new[] { "Name" })]
    [InlineData(typeof(EmailChannel), new[] { "Name" })]
    [InlineData(typeof(AllowedPythonModule), new[] { "Module" })]
    [InlineData(typeof(Policy), new[] { "Name" })]
    [InlineData(typeof(Snippet), new[] { "Name" })]
    [InlineData(typeof(McpServer), new[] { "Name" })]
    public void Uniqueness_applies_only_to_live_rows(Type entity, string[] properties)
    {
        using var db = NewDb();
        var index = FindIndex(db, entity, properties);

        Assert.True(index.IsUnique);
        Assert.Equal("\"IsActive\"", index.GetFilter());
    }

    // users keep reserving their username after deletion (audit identity), and a
    // re-created secret must not silently repoint the templates that name it.
    [Theory]
    [InlineData(typeof(User), new[] { "Username" })]
    [InlineData(typeof(Secret), new[] { "Name" })]
    public void Uniqueness_deliberately_covers_deleted_rows(Type entity, string[] properties)
    {
        using var db = NewDb();
        var index = FindIndex(db, entity, properties);

        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IIndex FindIndex(
        AppDbContext db, Type entity, string[] properties)
        => db.Model.FindEntityType(entity)!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual(properties));
}
