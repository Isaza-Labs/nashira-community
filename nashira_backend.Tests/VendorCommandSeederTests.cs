using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Seed;
using VendorCommandEntity = nashira_backend.Data.Models.VendorCommand;

namespace nashira_backend.Tests;

// The vendor catalogue, and why an empty one is not a neutral state.
//
// `GET /api/vendor-commands/resolve` is what turns one workflow into a multi-vendor
// workflow, and the snippets skill tells the agent to use it instead of hardcoding
// `show version` per platform. With vendor_commands empty every resolve 404s, the
// instruction is unfollowable, and the agent does the thing the skill told it not to.
// The shipped Skills/vendors/*.yaml are the fix; these tests are what keeps them
// loadable and internally consistent.
public class VendorCommandSeederTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nashira.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class FakeEnv : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public string EnvironmentName { get; set; } = "Testing";
    }

    private static (IServiceScopeFactory Scopes, AppDbContext Db) NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"vendor-seed-{Guid.NewGuid()}")
            .Options);
        var services = new ServiceCollection();
        services.AddSingleton(db);
        return (services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), db);
    }

    // A content root whose Skills/vendors holds exactly the files given.
    private static IWebHostEnvironment EnvWith(params (string Name, string Yaml)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "nashira-vendor-seed", Guid.NewGuid().ToString("n"));
        var dir = Path.Combine(root, "Skills", "vendors");
        Directory.CreateDirectory(dir);
        foreach (var (name, yaml) in files)
            File.WriteAllText(Path.Combine(dir, name), yaml);
        return new FakeEnv { ContentRootPath = root };
    }

    private static Task SeedAsync(IServiceScopeFactory scopes, IWebHostEnvironment env) =>
        VendorCommandSeeder.SeedAsync(scopes, env, NullLogger.Instance);

    private const string TwoIntents = """
        platform: cisco_ios
        description: test
        commands:
          - intent: show_version
            command: show version
            description: Version.
          - intent: save_config
            command: write memory
            read_only: false
        """;

    private const string OtherPlatform = """
        platform: juniper_junos
        commands:
          - intent: show_version
            command: show version
        """;

    [Fact]
    public async Task A_fresh_install_gets_the_shipped_catalogue()
    {
        var (scopes, db) = NewDb();

        await SeedAsync(scopes, EnvWith(
            ("cisco_ios.yaml", TwoIntents), ("juniper_junos.yaml", OtherPlatform)));

        var rows = await db.VendorCommands.ToListAsync();
        Assert.Equal(3, rows.Count);
        // The same intent on two platforms is the entire point: one workflow, two boxes.
        var platforms = rows.Where(r => r.Intent == "show_version")
            .Select(r => r.Platform).Order().ToList();
        Assert.Equal(["cisco_ios", "juniper_junos"], platforms);
    }

    [Fact]
    public async Task Read_only_defaults_to_true_and_is_honoured_when_set()
    {
        var (scopes, db) = NewDb();

        await SeedAsync(scopes, EnvWith(("cisco_ios.yaml", TwoIntents)));

        Assert.True((await db.VendorCommands.SingleAsync(r => r.Intent == "show_version")).ReadOnly);
        // A catalogue entry that mutates must not reach the idempotency tier as a show.
        Assert.False((await db.VendorCommands.SingleAsync(r => r.Intent == "save_config")).ReadOnly);
    }

    [Fact]
    public async Task Reseeding_is_idempotent()
    {
        var (scopes, db) = NewDb();
        var env = EnvWith(("cisco_ios.yaml", TwoIntents));

        await SeedAsync(scopes, env);
        await SeedAsync(scopes, env);

        Assert.Equal(2, await db.VendorCommands.CountAsync());
    }

    [Fact]
    public async Task An_operators_deletion_survives_a_redeploy()
    {
        var (scopes, db) = NewDb();
        var env = EnvWith(("cisco_ios.yaml", TwoIntents));
        await SeedAsync(scopes, env);

        // What DELETE /api/vendor-commands/{id} does: soft-delete, key still taken.
        var row = await db.VendorCommands.SingleAsync(r => r.Intent == "save_config");
        row.IsActive = false;
        await db.SaveChangesAsync();

        await SeedAsync(scopes, env);

        // Reviving it would reverse a decision, and the unfiltered unique index on
        // (Intent, Platform) would reject the insert anyway.
        Assert.Equal(2, await db.VendorCommands.CountAsync());
        Assert.False((await db.VendorCommands.SingleAsync(r => r.Intent == "save_config")).IsActive);
    }

    [Fact]
    public async Task A_malformed_file_does_not_cost_the_other_platforms_their_catalogue()
    {
        var (scopes, db) = NewDb();

        await SeedAsync(scopes, EnvWith(
            ("broken.yaml", "platform: [unterminated\n  commands: nope"),
            ("juniper_junos.yaml", OtherPlatform)));

        Assert.Equal(1, await db.VendorCommands.CountAsync());
    }

    [Fact]
    public async Task Entries_missing_an_intent_or_a_command_are_skipped_not_stored()
    {
        var (scopes, db) = NewDb();

        await SeedAsync(scopes, EnvWith(("cisco_ios.yaml", """
            platform: cisco_ios
            commands:
              - intent: show_version
                command: show version
              - intent: show_arp
              - command: show clock
            """)));

        var rows = await db.VendorCommands.ToListAsync();
        Assert.Single(rows);
        Assert.Equal("show_version", rows[0].Intent);
    }

    [Fact]
    public async Task Intent_and_platform_are_stored_lowercased_because_resolve_compares_lowercase()
    {
        var (scopes, db) = NewDb();

        await SeedAsync(scopes, EnvWith(("x.yaml", """
            platform: Cisco_IOS
            commands:
              - intent: Show_Version
                command: show version
            """)));

        var row = await db.VendorCommands.SingleAsync();
        Assert.Equal("cisco_ios", row.Platform);
        Assert.Equal("show_version", row.Intent);
    }

    [Fact]
    public async Task No_directory_is_a_no_op_rather_than_a_boot_failure()
    {
        var (scopes, db) = NewDb();
        var root = Path.Combine(Path.GetTempPath(), "nashira-vendor-seed", Guid.NewGuid().ToString("n"));

        await SeedAsync(scopes, new FakeEnv { ContentRootPath = root });

        Assert.Equal(0, await db.VendorCommands.CountAsync());
    }

    // ---- the files this build actually ships ----

    private static IReadOnlyList<string> ShippedFiles()
    {
        var dir = Path.Combine(RepoRoot(), "nashira_backend", "Skills", "vendors");
        Assert.True(Directory.Exists(dir), $"Skills/vendors not found at {dir}");
        var files = Directory.GetFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        return files;
    }

    private static async Task<List<VendorCommandEntity>> SeedShippedAsync()
    {
        var (scopes, db) = NewDb();
        await SeedAsync(scopes, EnvWith(
            ShippedFiles().Select(f => (Path.GetFileName(f), File.ReadAllText(f))).ToArray()));
        return await db.VendorCommands.ToListAsync();
    }

    [Fact]
    public async Task Every_shipped_catalogue_loads_at_least_one_command()
    {
        var rows = await SeedShippedAsync();

        // A file that parses to nothing is invisible at runtime — the seeder logs it and
        // moves on — so the only symptom would be an intent that never resolves.
        var platforms = rows.Select(r => r.Platform).Distinct().ToList();
        Assert.Equal(ShippedFiles().Count, platforms.Count);
        foreach (var file in ShippedFiles())
            Assert.Contains(Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), platforms);
    }

    [Fact]
    public async Task A_shipped_platform_never_declares_the_same_intent_twice()
    {
        var rows = await SeedShippedAsync();

        // The seeder drops a duplicate silently (the unique index would reject it), so a
        // repeated intent means one of the two commands quietly never ships.
        foreach (var group in rows.GroupBy(r => r.Platform))
        {
            var dupes = group.GroupBy(r => r.Intent).Where(g => g.Count() > 1)
                .Select(g => g.Key).ToList();
            Assert.True(dupes.Count == 0, $"{group.Key}: duplicate intents {string.Join(", ", dupes)}");
        }
    }

    [Fact]
    public void A_catalogue_file_is_named_after_the_platform_it_declares()
    {
        // Resolve joins on Device.Platform, so a file whose `platform:` disagrees with
        // its own name seeds entries no device will ever match.
        foreach (var file in ShippedFiles())
        {
            var declared = File.ReadAllLines(file)
                .FirstOrDefault(l => l.StartsWith("platform:", StringComparison.Ordinal))
                ?.Split(':', 2)[1].Trim();
            Assert.Equal(Path.GetFileNameWithoutExtension(file), declared);
        }
    }

    [Fact]
    public async Task Every_shipped_platform_is_one_the_ssh_runner_knows()
    {
        var parsers = await File.ReadAllTextAsync(Path.Combine(
            RepoRoot(), "deploy", "python", "nashira_ssh_parsers.py"));
        var rows = await SeedShippedAsync();

        // A platform absent from VENDOR_FAMILY connects as generic_ssh and parses
        // nothing, so the resolved command runs but its output never becomes structured.
        foreach (var platform in rows.Select(r => r.Platform).Distinct())
            Assert.Contains($"\"{platform}\":", parsers);
    }

    [Fact]
    public async Task Every_shipped_command_that_mutates_is_flagged_as_such()
    {
        var rows = await SeedShippedAsync();

        // read_only defaults to true, so forgetting the flag on a save entry is the easy
        // mistake — and it is the one that lets a write be treated as a repeatable read.
        foreach (var row in rows.Where(r => r.Intent == "save_config"))
            Assert.False(row.ReadOnly, $"{row.Platform}/save_config is stored as read-only");
    }

    [Fact]
    public async Task The_core_read_intents_resolve_on_every_shipped_platform()
    {
        var rows = await SeedShippedAsync();

        // The floor a multi-vendor workflow can rely on. Anything richer is per-platform,
        // but a catalogue that cannot answer "what version" or "what routes" for one of
        // its own platforms is a catalogue with a hole in it.
        foreach (var group in rows.GroupBy(r => r.Platform))
        {
            var intents = group.Select(r => r.Intent).ToHashSet();
            foreach (var required in new[] { "show_version", "show_interfaces", "show_route_table" })
                Assert.Contains(required, intents);
        }
    }
}
