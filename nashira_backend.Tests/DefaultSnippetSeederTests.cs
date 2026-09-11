using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Worker;
using SnippetEntity = nashira_backend.Data.Models.Snippet;

namespace nashira_backend.Tests;

// The baseline catalogue, and why it is keyed on slug.
//
// A workflow node runs a snippet, never a type. With an empty catalogue the agent has
// nothing legal to point a node at, and what it did instead is on record: a hundred
// stored workflows whose only nodes are __start__ and __end__, plus one that invented
// `__ping__` and failed at run time with `unbound_node`.
public class DefaultSnippetSeederTests
{
    private static (IServiceScopeFactory Scopes, AppDbContext Db) New(
        IReadOnlyCollection<string>? knownTypes = null)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"seed-{Guid.NewGuid()}")
            .Options);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<ISnippetHandlerRegistry>(new FakeRegistry(knownTypes ?? All));
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IServiceScopeFactory>(), db);
    }

    // Every executable handler. netconf and snmp_v3 are registered stubs and
    // deliberately absent: they have no baseline to seed.
    private static readonly string[] All =
    [
        "ping", "transform", "rest_call", "integration_action", "ssh", "mcp_call", "python_snippet", "git",
        "report", "email_send", "email_mailbox", "slack_message", "ansible_playbook",
    ];

    private static Task SeedAsync(IServiceScopeFactory scopes) =>
        DefaultSnippetSeeder.SeedAsync(scopes, NullLogger.Instance);

    [Fact]
    public async Task A_fresh_install_gets_a_runnable_snippet_for_every_registered_handler()
    {
        var (scopes, db) = New();

        await SeedAsync(scopes);

        var seeded = await db.Snippets.ToListAsync();
        Assert.NotEmpty(seeded);
        // Every handler this build registers is represented, so the agent has a legal
        // reference for any step type it might reach for.
        foreach (var type in All)
            Assert.Contains(seeded, s => s.Type == type);
        // A seeded row is not a reviewed one.
        Assert.All(seeded, s => Assert.False(s.Verified));
        // And nothing arrives with the sandbox's network isolation lifted.
        Assert.All(seeded, s => Assert.False(s.NetworkEnabled));
    }

    // The decision this class turns on. FlowWeaver's seeder matches by type, which is
    // wrong here: the snippets table already holds forty-five `ping` rows left by the
    // e2e suite, so a type match would conclude ping was covered and skip it — leaving
    // the catalogue with no usable ping snippet on a table that looks full of them.
    [Fact]
    public async Task Unrelated_rows_of_the_same_type_do_not_suppress_the_baseline()
    {
        var (scopes, db) = New();
        for (var i = 0; i < 3; i++)
            db.Snippets.Add(new SnippetEntity
            {
                SnippetId = Guid.NewGuid(),
                Name = $"e2e_snippet_{i}",
                Slug = $"e2e-snippet-{i}",
                Type = "ping",
                IsActive = true,
            });
        await db.SaveChangesAsync();

        await SeedAsync(scopes);

        Assert.Contains(await db.Snippets.ToListAsync(), s => s.Slug == "baseline-tcp-reachability");
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing_the_second_time()
    {
        var (scopes, db) = New();

        await SeedAsync(scopes);
        var afterFirst = await db.Snippets.CountAsync();
        await SeedAsync(scopes);

        Assert.Equal(afterFirst, await db.Snippets.CountAsync());
    }

    // Slug is permanent identity, so a baseline an operator renamed is still recognised
    // and does not come back as a duplicate.
    [Fact]
    public async Task A_renamed_baseline_is_not_reseeded()
    {
        var (scopes, db) = New();
        await SeedAsync(scopes);

        var row = await db.Snippets.FirstAsync(s => s.Slug == "baseline-tcp-reachability");
        row.Name = "our standard reachability check";
        await db.SaveChangesAsync();
        var before = await db.Snippets.CountAsync();

        await SeedAsync(scopes);

        Assert.Equal(before, await db.Snippets.CountAsync());
        Assert.Single(await db.Snippets.Where(s => s.Slug == "baseline-tcp-reachability").ToListAsync());
    }

    // Seeding a row whose type has no handler produces a snippet that validates at
    // write time and fails with `unknown_handler` on a device — exactly the late
    // failure the reference gate exists to prevent.
    [Fact]
    public async Task Only_handlers_this_build_registers_are_seeded()
    {
        var (scopes, db) = New(knownTypes: ["ping", "transform"]);

        await SeedAsync(scopes);

        var seeded = await db.Snippets.ToListAsync();
        Assert.All(seeded, s => Assert.Contains(s.Type, new[] { "ping", "transform" }));
        Assert.DoesNotContain(seeded, s => s.Type == "ssh");
    }

    // The name column is unique among active rows. An operator who already owns that
    // name keeps it; the baseline takes a suffixed one rather than failing the whole
    // seed — a boot that dies on one collision takes the rest of the catalogue with it.
    [Fact]
    public async Task A_name_an_operator_already_uses_does_not_break_the_seed()
    {
        var (scopes, db) = New();
        db.Snippets.Add(new SnippetEntity
        {
            SnippetId = Guid.NewGuid(),
            Name = "TCP reachability probe",
            Slug = "operators-own-probe",
            Type = "ping",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        await SeedAsync(scopes);

        var baseline = await db.Snippets.SingleAsync(s => s.Slug == "baseline-tcp-reachability");
        Assert.NotEqual("TCP reachability probe", baseline.Name);
        // The operator's row is untouched.
        Assert.Single(await db.Snippets.Where(s => s.Name == "TCP reachability probe").ToListAsync());
    }

    // A baseline has to be usable as written — the point is that the agent can
    // reference one without configuring anything first.
    [Fact]
    public async Task The_per_device_baseline_is_marked_per_device()
    {
        var (scopes, db) = New();
        await SeedAsync(scopes);

        var fleet = await db.Snippets.SingleAsync(s => s.Slug == "baseline-tcp-reachability-per-device");
        Assert.Equal(SnippetEntity.TargetPerDevice, fleet.TargetMode);

        var single = await db.Snippets.SingleAsync(s => s.Slug == "baseline-tcp-reachability");
        Assert.Equal(SnippetEntity.TargetOnce, single.TargetMode);
    }

    private sealed class FakeRegistry(IReadOnlyCollection<string> types) : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes => types;
        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => null;
    }
}
