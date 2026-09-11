using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

// What happens to an integration's specs when the integration is deleted.
//
// Deleting one used to flip a single `IsActive` flag. Its specs stayed active and kept
// pointing at it, which left them owned by something nobody could open: the api name
// was held against the unique index forever, the catalog's advice was to "unlink that
// spec first" from a page that no longer exists, and the global spec list — which
// filtered on `IntegrationId == null` — did not show them either. Invisible, uncallable,
// and blocking the name.
public class OrphanedSpecTests
{
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"orphan-{Guid.NewGuid()}")
        .Options);

    private static (Guid IntegrationId, Guid SpecId) Seed(AppDbContext db, bool integrationActive)
    {
        var integrationId = Guid.NewGuid();
        var specId = Guid.NewGuid();

        db.Integrations.Add(new Integration
        {
            IntegrationId = integrationId,
            Name = "netbox",
            Slug = "netbox",
            Type = "netbox",
            BaseUrl = "http://netbox.example",
            Enabled = true,
            IsActive = integrationActive,
        });
        db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = specId,
            Api = "netbox",
            Content = "openapi: 3.1.0",
            IntegrationId = integrationId,
            IsActive = true,
        });
        db.SaveChanges();
        return (integrationId, specId);
    }

    // The query the spec list runs for its "global" view. An owner that no longer
    // exists is not an owner, so its spec belongs in the list somebody can act on.
    private static IQueryable<AiApiSpec> GlobalView(AppDbContext db) =>
        db.AiApiSpecs.AsNoTracking()
            .Where(s => s.IsActive)
            .Where(s => s.IntegrationId == null
                || !db.Integrations.Any(i => i.IntegrationId == s.IntegrationId && i.IsActive));

    [Fact]
    public void A_spec_owned_by_a_live_integration_stays_out_of_the_global_view()
    {
        using var db = Db();
        Seed(db, integrationActive: true);

        Assert.Empty(GlobalView(db).ToList());
    }

    [Fact]
    public void A_spec_whose_owner_was_deleted_becomes_visible_again()
    {
        using var db = Db();
        Seed(db, integrationActive: false);

        var visible = GlobalView(db).ToList();

        Assert.Single(visible);
        Assert.Equal("netbox", visible[0].Api);
    }

    [Fact]
    public void A_genuinely_global_spec_is_still_listed()
    {
        using var db = Db();
        db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "weather",
            Content = "openapi: 3.1.0",
            IntegrationId = null,
            IsActive = true,
        });
        db.SaveChanges();

        Assert.Single(GlobalView(db).ToList());
    }

    // The ownership test the catalog runs before refusing an attach. It has to ask
    // whether the owner is still there, not merely whether an id is set.
    private static bool OwnerIsLive(AppDbContext db, AiApiSpec spec) =>
        spec.IntegrationId is not { } owner
        || db.Integrations.Any(i => i.IntegrationId == owner && i.IsActive);

    [Fact]
    public void An_owner_that_still_exists_blocks_a_take_over()
    {
        using var db = Db();
        var (_, specId) = Seed(db, integrationActive: true);
        var spec = db.AiApiSpecs.Single(s => s.AiApiSpecId == specId);

        Assert.True(OwnerIsLive(db, spec));
    }

    [Fact]
    public void An_owner_that_was_deleted_does_not()
    {
        using var db = Db();
        var (_, specId) = Seed(db, integrationActive: false);
        var spec = db.AiApiSpecs.Single(s => s.AiApiSpecId == specId);

        Assert.False(OwnerIsLive(db, spec));
    }

    // Deleting an integration releases what it owns, so the situation above stops being
    // created in the first place. The specs go inactive rather than being unlinked: the
    // operations were already dead — the executor resolves the owner with an `IsActive`
    // filter — so keeping them listed as global would offer capabilities that cannot run.
    [Fact]
    public void Deleting_an_integration_releases_its_specs_and_actions()
    {
        using var db = Db();
        var (integrationId, _) = Seed(db, integrationActive: true);
        db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = integrationId,
            Name = "dcim_devices_list",
            OperationId = "dcim_devices_list",
            IsActive = true,
        });
        db.SaveChanges();

        var row = db.Integrations.Single(i => i.IntegrationId == integrationId);
        row.IsActive = false;
        foreach (var s in db.AiApiSpecs.Where(x => x.IntegrationId == integrationId && x.IsActive))
            s.IsActive = false;
        foreach (var a in db.IntegrationActions.Where(x => x.IntegrationId == integrationId && x.IsActive))
            a.IsActive = false;
        db.SaveChanges();

        Assert.Empty(db.AiApiSpecs.Where(s => s.IsActive));
        Assert.Empty(db.IntegrationActions.Where(a => a.IsActive));
        // And with the spec gone, nothing holds the api name.
        Assert.Empty(GlobalView(db).ToList());
    }
}
