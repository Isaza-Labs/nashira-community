using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

public class AiApiSpecModelTests
{
    [Fact]
    public void Api_uniqueness_applies_only_to_active_specs()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ai-api-spec-model-{Guid.NewGuid()}")
            .Options);

        var index = db.Model.FindEntityType(typeof(AiApiSpec))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(AiApiSpec.Api)]));

        Assert.True(index.IsUnique);
        Assert.Equal("\"IsActive\" = true", index.GetFilter());
    }
}
