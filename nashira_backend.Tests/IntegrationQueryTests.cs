using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Integration;

namespace nashira_backend.Tests;

// The lookup behind list_integrations.
//
// These exist because of a specific failure: the agent asked for "netbox", the
// integration was called "NetBox", the exact-match filter returned nothing, and the
// agent told the user NetBox was not configured — while it sat there configured and
// health-checked. Being confidently wrong about absence is the worst answer a lookup
// can give, so the matching is deliberately loose.
public class IntegrationQueryTests
{
    private static AppDbContext DbWith(params (string Name, string Slug, string Type)[] rows)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"intq-{Guid.NewGuid()}").Options);
        foreach (var (name, slug, type) in rows)
            db.Integrations.Add(new Data.Models.Integration
            {
                IntegrationId = Guid.NewGuid(),
                Name = name,
                Slug = slug,
                Type = type,
                BaseUrl = "https://example.test",
                IsActive = true,
                Enabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        db.SaveChanges();
        return db;
    }

    [Theory]
    [InlineData("netbox")]   // how the agent asks
    [InlineData("NetBox")]   // how it is stored
    [InlineData("NETBOX")]
    [InlineData(" netbox ")] // padded
    [InlineData("net")]      // partial
    public void Name_matching_finds_the_integration_however_it_was_typed(string query)
    {
        using var db = DbWith(("NetBox Production", "netbox-production", "netbox"));
        var found = IntegrationQuery.FilterByName(db.Integrations, query).ToList();
        Assert.Single(found);
    }

    // The slug and the type are matched too: an admin who named it "Inventory" still
    // has type=netbox, and that is what a caller looking for NetBox will ask for.
    [Fact]
    public void Name_matching_also_covers_slug_and_type()
    {
        using var db = DbWith(("Inventory", "inventory", "netbox"));
        Assert.Single(IntegrationQuery.FilterByName(db.Integrations, "netbox").ToList());
    }

    [Fact]
    public void A_name_that_matches_nothing_still_matches_nothing()
    {
        using var db = DbWith(("NetBox", "netbox", "netbox"));
        Assert.Empty(IntegrationQuery.FilterByName(db.Integrations, "servicenow").ToList());
    }

    [Fact]
    public void An_empty_filter_returns_everything()
    {
        using var db = DbWith(("NetBox", "netbox", "netbox"), ("AWX", "awx", "awx"));
        Assert.Equal(2, IntegrationQuery.FilterByName(db.Integrations, null).Count());
        Assert.Equal(2, IntegrationQuery.FilterByName(db.Integrations, "   ").Count());
    }
}
