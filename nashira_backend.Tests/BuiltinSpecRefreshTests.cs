using nashira_backend.Configuration.Modules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.Specs;

namespace nashira_backend.Tests;

// Keeping a shipped spec row in step with the file it came from.
//
// The seeder only ever inserted, so a row seeded before an endpoint existed went on
// advertising an API this build no longer has. That is not a theoretical drift: on a
// live instance na_workflows was missing /bundle and /import, and na_admin_readonly had
// spent a release without the audit query parameters its endpoint had gained — for the
// agent, a parameter absent from the spec and absent from the API are the same thing.
//
// Refreshing unconditionally would have swapped one bug for its mirror image: an admin
// who trims operations out of a built-in spec to save agent context would find the edit
// silently reverted on every redeploy. The hash is what separates the two.
public class BuiltinSpecRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"spec-refresh-{Guid.NewGuid():N}");

    // The seeder reads ContentRootPath/Specs, so the fixture has to mirror that layout
    // rather than dropping the files at the root.
    private string SpecsDir => Path.Combine(_dir, "Specs");

    public BuiltinSpecRefreshTests() => Directory.CreateDirectory(SpecsDir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // A minimal but real OpenAPI document, so the operation count is parsed rather than
    // asserted against a stub.
    private static string SpecWith(params string[] operationIds)
    {
        var paths = string.Join("\n", operationIds.Select(id => $$"""
              /api/thing/{{id}}:
                get:
                  operationId: {{id}}
                  responses:
                    "200": { description: ok }
            """));
        return $"""
            openapi: 3.1.0
            info:
              title: test
              version: "1.0"
            paths:
            {paths}
            """;
    }

    private string WriteSpec(string api, string content)
    {
        var path = Path.Combine(SpecsDir, $"{api}.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<AppDbContext> RunSeederAsync(AppDbContext? existing = null)
    {
        var db = existing ?? new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"spec-{Guid.NewGuid()}")
            .Options);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        var provider = services.BuildServiceProvider();

        await BuiltinSpecSeeder.SeedAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeEnv(_dir),
            new FakeIndex(),
            ModuleSelection.Parse(null),
            NullLogger.Instance);

        return db;
    }

    // ─── the drift this exists to fix ────────────────────────────────────────

    [Fact]
    public async Task A_shipped_spec_that_gained_an_endpoint_reaches_an_existing_row()
    {
        WriteSpec("na_things", SpecWith("list"));
        var db = await RunSeederAsync();

        var before = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.Equal(1, before.OperationCount);

        // The next release documents a second endpoint.
        WriteSpec("na_things", SpecWith("list", "bundle"));
        await RunSeederAsync(db);

        var after = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.Contains("bundle", after.Content, StringComparison.Ordinal);
        // The count is reparsed, not left describing the old document.
        Assert.Equal(2, after.OperationCount);
    }

    // The property that makes the refresh safe to run on every boot.
    [Fact]
    public async Task An_edit_made_through_the_api_is_not_reverted()
    {
        WriteSpec("na_things", SpecWith("list", "detail"));
        var db = await RunSeederAsync();

        // An admin trims an operation out to save agent context.
        var row = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        row.Content = SpecWith("list");
        row.OperationCount = 1;
        await db.SaveChangesAsync();

        // A later release changes the shipped file again.
        WriteSpec("na_things", SpecWith("list", "detail", "extra"));
        await RunSeederAsync(db);

        var after = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.DoesNotContain("extra", after.Content, StringComparison.Ordinal);
        Assert.Equal(1, after.OperationCount);
    }

    [Fact]
    public async Task An_unchanged_file_leaves_the_row_alone()
    {
        WriteSpec("na_things", SpecWith("list"));
        var db = await RunSeederAsync();
        var stamp = (await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things")).UpdatedAt;

        await RunSeederAsync(db);

        var after = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.Equal(stamp, after.UpdatedAt);
    }

    // ─── rows from before the column existed ─────────────────────────────────

    // There is no way to tell an untouched legacy row from an edited one, so the file
    // wins once. This is the only pass that can overwrite an edit it could not see, and
    // it is why the seeder logs the names it adopted.
    [Fact]
    public async Task A_row_with_no_hash_is_adopted_once_and_tracked_afterwards()
    {
        WriteSpec("na_things", SpecWith("list", "bundle"));
        var db = await RunSeederAsync();

        // Reproduce a row seeded before content tracking: stale content, no stamp.
        var row = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        row.Content = SpecWith("list");
        row.OperationCount = 1;
        row.ShippedContentHash = null;
        await db.SaveChangesAsync();

        await RunSeederAsync(db);

        var adopted = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.Contains("bundle", adopted.Content, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(adopted.ShippedContentHash));

        // From here on it behaves like any tracked row: an edit stands.
        adopted.Content = SpecWith("list");
        await db.SaveChangesAsync();
        WriteSpec("na_things", SpecWith("list", "bundle", "more"));
        await RunSeederAsync(db);

        var after = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.DoesNotContain("more", after.Content, StringComparison.Ordinal);
    }

    // ─── what the refresh must not touch ─────────────────────────────────────

    [Fact]
    public async Task A_user_created_row_with_a_shipped_name_is_never_adopted()
    {
        WriteSpec("na_things", SpecWith("shipped"));
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"spec-{Guid.NewGuid()}")
            .Options);
        var userContent = SpecWith("operator");
        db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "na_things",
            Content = userContent,
            OperationCount = 1,
            CreatedBy = Guid.NewGuid(),
            AuthType = "none",
            AuthConfig = null,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        await RunSeederAsync(db);

        var after = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.Equal(userContent, after.Content);
        Assert.Null(after.ShippedContentHash);
        Assert.Equal("none", after.AuthType);
        Assert.Null(after.AuthConfig);
    }

    // A spec an admin deleted stays deleted. Reviving it would be the seeder overruling
    // a decision rather than correcting a cache.
    [Fact]
    public async Task A_deleted_spec_is_not_revived()
    {
        WriteSpec("na_things", SpecWith("list"));
        var db = await RunSeederAsync();

        var row = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        row.IsActive = false;
        await db.SaveChangesAsync();

        WriteSpec("na_things", SpecWith("list", "bundle"));
        await RunSeederAsync(db);

        var after = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.False(after.IsActive);
        Assert.DoesNotContain("bundle", after.Content, StringComparison.Ordinal);
    }

    // A third-party spec has no shipped file behind it, so nothing here applies to it.
    [Fact]
    public async Task A_spec_with_no_shipped_file_is_untouched()
    {
        WriteSpec("na_things", SpecWith("list"));
        var db = await RunSeederAsync();

        db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "netbox",
            Content = SpecWith("dcim_devices_list"),
            OperationCount = 1,
            BaseUrl = "https://netbox.internal",
            AuthType = "token",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        WriteSpec("na_things", SpecWith("list", "bundle"));
        await RunSeederAsync(db);

        var netbox = await db.AiApiSpecs.SingleAsync(s => s.Api == "netbox");
        Assert.Null(netbox.ShippedContentHash);
        Assert.Equal("https://netbox.internal", netbox.BaseUrl);
    }

    // A file that stops parsing must not blank out a working row — the row would then
    // advertise zero operations, which reads as "this API has nothing".
    [Fact]
    public async Task A_broken_file_does_not_replace_a_working_row()
    {
        WriteSpec("na_things", SpecWith("list"));
        var db = await RunSeederAsync();

        WriteSpec("na_things", "this is not: [ valid yaml at all");
        await RunSeederAsync(db);

        var after = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.Equal(1, after.OperationCount);
        Assert.Contains("openapi", after.Content, StringComparison.Ordinal);
    }

    // A checkout on Windows and one on a build agent can differ in nothing but CRLF.
    // Hashing the raw bytes would make every boot see an edit that is not there.
    [Fact]
    public async Task Line_endings_alone_are_not_treated_as_a_change()
    {
        var unix = SpecWith("list").Replace("\r\n", "\n");
        WriteSpec("na_things", unix);
        var db = await RunSeederAsync();
        var stamp = (await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things")).UpdatedAt;

        WriteSpec("na_things", unix.Replace("\n", "\r\n"));
        await RunSeederAsync(db);

        var after = await db.AiApiSpecs.SingleAsync(s => s.Api == "na_things");
        Assert.Equal(stamp, after.UpdatedAt);
    }

    // ─── fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeEnv(string root) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public string EnvironmentName { get; set; } = "Testing";
    }

    private sealed class FakeIndex : IApiSpecIndex
    {
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [];
        public ApiOperation? GetByOperationId(string operationId) => null;
    }
}
