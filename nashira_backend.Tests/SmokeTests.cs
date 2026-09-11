using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;

namespace nashira_backend.Tests;

// Phase 0 smoke test: proves the project reference, EF Core wiring, and the
// AppDbContext constructor all compile and run. Grows as models are added.
public class SmokeTests
{
    [Fact]
    public void AppDbContext_can_be_constructed()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("nashira-smoke")
            .Options;

        using var db = new AppDbContext(options);

        Assert.NotNull(db);
    }
}
