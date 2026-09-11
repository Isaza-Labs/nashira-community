using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Integration;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Integration;

namespace nashira_backend.Tests;

// Attaching a skill or a spec to an integration and then reading it back.
//
// The round trip is the whole contract: an upload that saves but does not show up
// under its integration is indistinguishable, from the screen, from an upload that
// silently failed — and the user's next move is to upload it again and get told it
// already exists.
public class IntegrationCatalogTests
{
    private const string MinimalSpec = """
        openapi: 3.1.0
        info: { title: Test, version: "1.0" }
        paths:
          /widgets:
            get:
              operationId: test_listWidgets
              summary: List widgets
        """;

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"catalog-{Guid.NewGuid()}")
            .Options);

    private static IntegrationCatalog NewCatalog(AppDbContext db) =>
        new(db, new FakeUser(), new FakeIndex(), new FakeLoader(),
            new IntegrationActionSync(db, NullLogger<IntegrationActionSync>.Instance),
            new TemplateSecurityValidator(), new FakeRecorder(),
            NullLogger<IntegrationCatalog>.Instance);

    private static Guid AddIntegration(AppDbContext db, string name = "NetBox")
    {
        var id = Guid.NewGuid();
        db.Integrations.Add(new Data.Models.Integration
        {
            IntegrationId = id,
            Name = name,
            Slug = name.ToLowerInvariant(),
            Type = "netbox",
            BaseUrl = "https://netbox.example",
            IsActive = true,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Attached_skill_comes_back_in_the_bundle()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        await catalog.AttachSkillAsync(id,
            new AttachSkillRequest { Name = "netbox", Content = "# NetBox\n\nHow it behaves." },
            CancellationToken.None);

        var bundle = await catalog.GetBundleAsync(id, CancellationToken.None);

        Assert.Single(bundle.Skills);
        Assert.Equal("netbox", bundle.Skills[0].Name);
        Assert.Equal(id, bundle.Skills[0].IntegrationId);
    }

    [Fact]
    public async Task Attached_spec_comes_back_in_the_bundle_and_materialises_actions()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        var result = await catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = MinimalSpec },
            CancellationToken.None);

        Assert.Equal(1, result.Spec.OperationCount);
        Assert.Equal(1, result.ActionsCreated);

        var bundle = await catalog.GetBundleAsync(id, CancellationToken.None);
        Assert.Single(bundle.Specs);
        Assert.Equal("netbox", bundle.Specs[0].Api);
    }

    // Re-uploading the same name is how a user fixes a typo in a skill. It has to
    // replace, not collide — the alternative is telling someone their own skill is
    // already taken by themselves.
    [Fact]
    public async Task Re_attaching_the_same_skill_replaces_it()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        await catalog.AttachSkillAsync(id,
            new AttachSkillRequest { Name = "netbox", Content = "first" }, CancellationToken.None);
        await catalog.AttachSkillAsync(id,
            new AttachSkillRequest { Name = "netbox", Content = "second" }, CancellationToken.None);

        var bundle = await catalog.GetBundleAsync(id, CancellationToken.None);
        Assert.Single(bundle.Skills);
        Assert.Equal("second", bundle.Skills[0].Content);
    }

    [Fact]
    public async Task Re_attaching_the_same_spec_replaces_it()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        await catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = MinimalSpec }, CancellationToken.None);
        await catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = MinimalSpec }, CancellationToken.None);

        var bundle = await catalog.GetBundleAsync(id, CancellationToken.None);
        Assert.Single(bundle.Specs);
    }

    // A name already in use is refused rather than annexed: that skill is already
    // merged into prompts nobody is currently looking at.
    [Fact]
    public async Task A_global_skill_name_is_refused_not_annexed()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        AddGlobalSkill(db, "shared");

        var catalog = NewCatalog(db);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => catalog.AttachSkillAsync(id,
            new AttachSkillRequest { Name = "shared", Content = "mine" }, CancellationToken.None));

        // The code is what lets the screen offer "attach the existing one" instead of
        // leaving the user at a dead end with a name they cannot use.
        Assert.Equal(CatalogConflicts.SkillNameGlobal, ex.Code);
    }

    // …and the offer works: adopting re-scopes the existing row rather than creating
    // a second one, so the name stops being unusable.
    [Fact]
    public async Task A_global_skill_can_be_adopted_on_request()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        AddGlobalSkill(db, "shared");

        var catalog = NewCatalog(db);
        await catalog.AttachSkillAsync(id,
            new AttachSkillRequest { Name = "shared", Content = "mine", Adopt = true },
            CancellationToken.None);

        var bundle = await catalog.GetBundleAsync(id, CancellationToken.None);
        Assert.Single(bundle.Skills);
        Assert.Equal("mine", bundle.Skills[0].Content);
        Assert.Single(await db.AiPromptSkills.Where(x => x.Name == "shared").ToListAsync());
    }

    // A skill owned by a DIFFERENT integration is never adoptable: taking it would
    // strip it from there without anyone being asked.
    [Fact]
    public async Task A_skill_owned_by_another_integration_is_not_adoptable()
    {
        using var db = NewDb();
        var mine = AddIntegration(db, "NetBox");
        var theirs = AddIntegration(db, "ServiceNow");
        db.AiPromptSkills.Add(new AiPromptSkill
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = "shared",
            Content = "theirs",
            IntegrationId = theirs,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var catalog = NewCatalog(db);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => catalog.AttachSkillAsync(mine,
            new AttachSkillRequest { Name = "shared", Content = "mine", Adopt = true },
            CancellationToken.None));

        Assert.Equal(CatalogConflicts.SkillNameTaken, ex.Code);
    }

    // Plain prose is valid YAML, so parsing does not throw on the wrong file. Without
    // an explicit check the upload reports success and the action list never moves.
    [Fact]
    public async Task A_document_with_no_operations_is_refused()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        await Assert.ThrowsAsync<ValidationException>(() => catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = "just some notes about netbox" },
            CancellationToken.None));

        Assert.Empty(await db.AiApiSpecs.ToListAsync());
    }

    // Adopting a self-contained spec has to REPOINT it, not just relabel it. Setting
    // IntegrationId alone leaves RestOperationExecutor preferring the spec's own
    // BaseUrl and its own auth, so the integration would show a catalogue of
    // operations that still call the old host with the old token.
    [Fact]
    public async Task Adopting_a_global_spec_makes_it_inherit_the_integration()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        db.AiApiSpecs.Add(new Data.Models.AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "netbox",
            Content = MinimalSpec,
            OperationCount = 1,
            BaseUrl = "https://somewhere-else.example",
            AuthType = "bearer",
            AuthConfig = "{\"token\":\"old\"}",
            IntegrationId = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var catalog = NewCatalog(db);
        await catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = MinimalSpec, Adopt = true },
            CancellationToken.None);

        var row = await db.AiApiSpecs.SingleAsync(x => x.Api == "netbox");
        Assert.Equal(id, row.IntegrationId);
        Assert.Null(row.BaseUrl);
        Assert.Equal("none", row.AuthType);
        Assert.Null(row.AuthConfig);
    }

    // Same normalisation on the ordinary attach path, not just adoption: a spec
    // uploaded here is a spec that belongs to this integration.
    [Fact]
    public async Task An_attached_spec_never_carries_its_own_url_or_auth()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        await catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = MinimalSpec }, CancellationToken.None);

        var row = await db.AiApiSpecs.SingleAsync(x => x.Api == "netbox");
        Assert.Null(row.BaseUrl);
        Assert.Equal("none", row.AuthType);
    }

    // Re-uploading a corrected document onto a spec this integration ALREADY owns
    // must not touch its connection settings. A spec-level base URL override is a
    // supported, deliberate state; wiping it here would silently repoint every
    // operation while reporting success.
    [Fact]
    public async Task Re_attaching_keeps_a_deliberate_base_url_override()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        await catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = MinimalSpec }, CancellationToken.None);

        // What /admin/specs lets an admin do to a linked spec.
        var row = await db.AiApiSpecs.SingleAsync(x => x.Api == "netbox");
        row.BaseUrl = "https://netbox.example/api/plugins/foo";
        row.VerifySsl = false;
        await db.SaveChangesAsync();

        await catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = MinimalSpec }, CancellationToken.None);

        var after = await db.AiApiSpecs.SingleAsync(x => x.Api == "netbox");
        Assert.Equal("https://netbox.example/api/plugins/foo", after.BaseUrl);
        Assert.False(after.VerifySsl);
    }

    [Fact]
    public async Task A_global_spec_api_is_refused_with_the_adoptable_code()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        db.AiApiSpecs.Add(new Data.Models.AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "netbox",
            Content = MinimalSpec,
            OperationCount = 1,
            IntegrationId = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var catalog = NewCatalog(db);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => catalog.AttachSpecAsync(id,
            new AttachSpecRequest { Api = "netbox", Content = MinimalSpec }, CancellationToken.None));

        Assert.Equal(CatalogConflicts.SpecApiGlobal, ex.Code);
    }

    private static void AddGlobalSkill(AppDbContext db, string name)
    {
        db.AiPromptSkills.Add(new AiPromptSkill
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = name,
            Content = "global",
            IntegrationId = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Bundle_attach_validates_every_item_before_writing_any()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        await Assert.ThrowsAsync<ValidationException>(() => catalog.AttachBundleAsync(id,
            [new BundledSkill { Name = "ok", Content = "fine" }],
            // The second spec is not an OpenAPI document at all.
            [
                new BundledSpec { Api = "good", Content = MinimalSpec },
                new BundledSpec { Api = "bad", Content = "not a spec" },
            ],
            CancellationToken.None));

        // Nothing persisted: the failure came before SaveChanges.
        Assert.Empty(await db.AiPromptSkills.ToListAsync());
        Assert.Empty(await db.AiApiSpecs.ToListAsync());
    }

    [Fact]
    public async Task Bundle_attach_refuses_two_items_with_the_same_name()
    {
        using var db = NewDb();
        var id = AddIntegration(db);
        var catalog = NewCatalog(db);

        await Assert.ThrowsAsync<ValidationException>(() => catalog.AttachBundleAsync(id,
            [],
            [
                new BundledSpec { Api = "netbox", Content = MinimalSpec },
                new BundledSpec { Api = "netbox", Content = MinimalSpec },
            ],
            CancellationToken.None));
    }

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "tester";
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => true;
    }

    private sealed class FakeIndex : IApiSpecIndex
    {
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [];
        public ApiOperation? GetByOperationId(string operationId) => null;
    }

    private sealed class FakeLoader : ISkillPromptLoader
    {
        public Task<string> LoadAsync(string toolList, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<BuiltinSkill>> ListBuiltinsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BuiltinSkill>>([]);
        public Task<bool> SaveBuiltinAsync(string name, string content, CancellationToken ct) =>
            Task.FromResult(true);
        public void Invalidate() { }
    }

    private sealed class FakeRecorder : IValidationRecorder
    {
        public Task RecordAsync(string kind, string targetName, TemplateValidationResult result, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
