using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Identity;

namespace nashira_backend.Tests;

// The shipped AI content against the deployment's capabilities: what the agent is told
// it is, and what it is told it can call. Filtered on reading, never deleted — a row an
// all-enabled deployment wrote is still there when the capability comes back.
public class ModuleContentCatalogTests
{
    private static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nashira.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "nashira_backend");
    }

    private static IReadOnlyList<string> ShippedSkills() =>
        [.. Directory
            .GetFiles(Path.Combine(BackendRoot(), "Skills"), "*.md", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Path.Combine(BackendRoot(), "Skills"), file)
                .Replace(Path.DirectorySeparatorChar, '/'))
            // Skills/vendors is seed data for VendorCommandSeeder, not prompt text.
            .Where(name => !name.StartsWith("vendors/", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static IReadOnlyList<string> ShippedSpecs() =>
        [.. Directory
            .GetFiles(Path.Combine(BackendRoot(), "Specs"), "*.yaml", SearchOption.TopDirectoryOnly)
            .Select(file => Path.GetFileNameWithoutExtension(file).ToLowerInvariant())
            .Order(StringComparer.OrdinalIgnoreCase)];

    // Both directions: nothing shipped is unclassified, and nothing classified has been
    // renamed or deleted out from under the table. A one-way check would pass on an
    // empty directory, and a stale key would silently hide a file that no longer exists.
    [Fact]
    public void Every_shipped_skill_is_classified()
    {
        Assert.Equal(
            ShippedSkills(),
            ModuleContentCatalog.AllSkills.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    [Fact]
    public void Every_shipped_spec_is_classified()
    {
        Assert.Equal(
            ShippedSpecs(),
            ModuleContentCatalog.AllSpecs.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }
    [Fact]
    public void Every_operation_in_every_mixed_spec_is_classified()
    {
        var mixedSpecs = ModuleContentCatalog.AllSpecs
            .Where(api => ModuleContentCatalog.SpecCoverage(api)?.Count > 1)
            .ToList();

        Assert.NotEmpty(mixedSpecs);
        foreach (var api in mixedSpecs)
        {
            var yaml = File.ReadAllText(Path.Combine(BackendRoot(), "Specs", $"{api}.yaml"));
            var operations = YamlSpecIndex.ParseOperations(api, yaml).ToList();

            Assert.NotEmpty(operations);
            Assert.All(operations, operation =>
                Assert.NotNull(ModuleContentCatalog.OperationCoverage(api, operation.OperationId)));
        }
    }

    // The compact base prompt is universal. Platform-wide documents are only safe when
    // every capability they describe exists; partial deployments use focused skills.
    [Theory]
    [InlineData(null, true, true)]
    [InlineData("core", false, false)]
    [InlineData("chat,ai-studio,integrations,secrets", false, true)]
    public void Platform_wide_skills_do_not_advertise_disabled_capabilities(
        string? configuredModules, bool allEnabled, bool baseEnabled)
    {
        var selection = ModuleSelection.Parse(configuredModules);

        Assert.Equal(baseEnabled, ModuleContentCatalog.IsSkillAvailable("base.md", selection));
        Assert.Equal(allEnabled, ModuleContentCatalog.IsSkillAvailable("nashira.md", selection));
        Assert.Equal(allEnabled, ModuleContentCatalog.IsSkillAvailable("troubleshooting.md", selection));
    }

    [Fact]
    public void A_skill_of_a_disabled_capability_is_not_offered()
    {
        var coreOnly = ModuleSelection.Parse("core");

        Assert.False(ModuleContentCatalog.IsSkillAvailable("git.md", coreOnly));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("inventory.md", coreOnly));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("workflows.md", coreOnly));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("email.md", coreOnly));
        Assert.True(ModuleContentCatalog.IsSkillAvailable("git-direct.md", ModuleSelection.Parse("git")));
    }

    [Fact]
    public void Mixed_skills_are_replaced_by_focused_guidance_in_partial_deployments()
    {
        var automationOnly = ModuleSelection.Parse("automation");
        var gitOnly = ModuleSelection.Parse("git");

        Assert.True(ModuleContentCatalog.IsSkillAvailable("snippets-automation.md", automationOnly));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("snippets.md", automationOnly));
        Assert.True(ModuleContentCatalog.IsSkillAvailable("git-direct.md", gitOnly));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("git.md", gitOnly));
    }

    [Theory]
    [InlineData("na_git")]
    [InlineData("NA_GIT")]
    [InlineData("na_admin_readonly")]
    public void Shipped_spec_names_are_reserved_even_when_their_module_is_disabled(string api)
    {
        Assert.True(ModuleContentCatalog.IsBuiltinSpec(api));
        Assert.False(ModuleContentCatalog.IsBuiltinSpec($"operator_{api}"));
    }

    [Fact]
    public void Mixed_content_exposes_only_the_enabled_capability()
    {
        var artifacts = ModuleSelection.Parse("artifacts");
        var communications = ModuleSelection.Parse("chat,ai-studio,integrations,communications");

        Assert.True(ModuleContentCatalog.IsSkillAvailable("exports.md", artifacts));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("exports.md", communications));
        Assert.True(ModuleContentCatalog.IsSkillAvailable("email.md", communications));
        Assert.False(ModuleContentCatalog.IsSkillAvailable("email.md", artifacts));

        // The stable spec row is relevant to both deployments, but each operation has
        // independent ownership and the other half never enters the agent catalogue.
        Assert.True(ModuleContentCatalog.IsSpecAvailable("na_notifications", artifacts));
        Assert.True(ModuleContentCatalog.IsOperationAvailable(
            "na_notifications", "notifications_listReports", artifacts));
        Assert.False(ModuleContentCatalog.IsOperationAvailable(
            "na_notifications", "notifications_listEmailChannels", artifacts));
        Assert.True(ModuleContentCatalog.IsOperationAvailable(
            "na_notifications", "notifications_listEmailChannels", communications));
        Assert.False(ModuleContentCatalog.IsOperationAvailable(
            "na_notifications", "notifications_listReports", communications));
    }

    [Theory]
    [InlineData(
        "na_ai_meta",
        "aimeta_listConversations",
        "ai-studio,integrations",
        "chat,ai-studio,integrations")]
    [InlineData("na_inventory", "inventory_listCredentials", "fleet", "secrets")]
    [InlineData("na_snippets", "snippets_listVendorCommands", "automation", "fleet")]
    [InlineData("na_governance", "governance_listPolicies", "core", "governance")]
    [InlineData("na_governance", "governance_listSecrets", "governance", "secrets")]
    public void Mixed_operations_follow_their_owning_capability(
        string api, string operationId, string withoutOwner, string withOwner)
    {
        Assert.False(ModuleContentCatalog.IsOperationAvailable(
            api, operationId, ModuleSelection.Parse(withoutOwner)));
        Assert.True(ModuleContentCatalog.IsOperationAvailable(
            api, operationId, ModuleSelection.Parse(withOwner)));
    }

    // A spec an admin wrote through /api/ai/specs is theirs; nothing here classifies it,
    // and a deployment hiding it would be deciding about the operator's own material.
    [Fact]
    public void Content_nobody_classified_stays_visible()
    {
        var coreOnly = ModuleSelection.Parse("core");

        Assert.True(ModuleContentCatalog.IsSpecAvailable("acme_billing", coreOnly));
        Assert.True(ModuleContentCatalog.IsSkillAvailable("something-new.md", coreOnly));
    }

    // The runtime index, over rows an all-enabled deployment already wrote. This is the
    // case that matters: the filter is on reading, so the row is still in the table and
    // comes back the moment the capability does.
    [Fact]
    public async Task The_index_hides_a_stored_spec_of_a_disabled_capability()
    {
        var database = $"spec-index-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(database));
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiApiSpecs.AddRange(
                Spec("na_git", "gitList"),
                Spec("acme_billing", "acmeInvoices"));
            await db.SaveChangesAsync();
        }

        var scopes = provider.GetRequiredService<IServiceScopeFactory>();

        var withoutGit = new YamlSpecIndex(
            scopes, ModuleSelection.Parse("core"), NullLogger<YamlSpecIndex>.Instance);
        await withoutGit.ReloadAsync();

        Assert.Null(withoutGit.GetByOperationId("gitList"));
        // The operator's own spec is untouched by any of this.
        Assert.NotNull(withoutGit.GetByOperationId("acmeInvoices"));

        var withGit = new YamlSpecIndex(
            scopes, ModuleSelection.Parse("git"), NullLogger<YamlSpecIndex>.Instance);
        await withGit.ReloadAsync();

        Assert.NotNull(withGit.GetByOperationId("gitList"));

        // Nothing was deleted to achieve any of that.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(2, await db.AiApiSpecs.CountAsync());
        }
    }

    [Fact]
    public async Task The_runtime_index_filters_operations_inside_a_mixed_spec()
    {
        var database = $"mixed-spec-index-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(database));
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiApiSpecs.Add(BuiltinSpec("na_admin_readonly"));
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.AiApiSpecs.CountAsync());
        }

        var selection = ModuleSelection.Parse("chat,ai-studio,integrations,secrets");

        var original = BuiltinSpec("na_admin_readonly").Content;
        var filtered = YamlSpecIndex.FilterContent(
            "na_admin_readonly", original, selection);
        var visibleOperations = YamlSpecIndex.ParseOperations(
            "na_admin_readonly", filtered).Select(operation => operation.OperationId).ToList();
        Assert.Contains("admin_listValidations", visibleOperations);
        Assert.DoesNotContain("admin_listAuditEvents", visibleOperations);
        Assert.DoesNotContain("admin_listAuthEvents", visibleOperations);
        Assert.DoesNotContain("admin_listExports", visibleOperations);

        var index = new YamlSpecIndex(
            provider.GetRequiredService<IServiceScopeFactory>(),
            selection,
            NullLogger<YamlSpecIndex>.Instance);
        await index.ReloadAsync();

        Assert.NotNull(index.GetByOperationId("admin_listValidations"));
        Assert.Null(index.GetByOperationId("admin_listAuditEvents"));
        Assert.Null(index.GetByOperationId("admin_listAuthEvents"));
        Assert.Null(index.GetByOperationId("admin_listExports"));
    }

    [Fact]
    public async Task Spec_tools_cannot_recover_disabled_builtin_content()
    {
        var database = $"spec-tools-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(database));
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AiApiSpecs.AddRange(
            BuiltinSpec("na_admin_readonly"),
            BuiltinSpec("na_git"));
        await db.SaveChangesAsync();

        var selection = ModuleSelection.Parse("chat,ai-studio,integrations,secrets");
        var index = new YamlSpecIndex(
            provider.GetRequiredService<IServiceScopeFactory>(),
            selection,
            NullLogger<YamlSpecIndex>.Instance);
        var user = new FakeUser();

        var listed = await new ListSpecsHandler(db, user, selection, index)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), default);
        var specs = listed.GetProperty("specs").EnumerateArray().ToList();
        var adminSpec = Assert.Single(specs);
        Assert.Equal("na_admin_readonly", adminSpec.GetProperty("api").GetString());
        Assert.Equal(1, adminSpec.GetProperty("operation_count").GetInt32());

        var detail = await new GetSpecHandler(db, user, selection)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { api = "na_admin_readonly" }), default);
        Assert.Equal(1, detail.GetProperty("operation_count").GetInt32());
        var content = detail.GetProperty("content").GetString()!;
        Assert.Contains("admin_listValidations", content);
        Assert.DoesNotContain("admin_listAuditEvents", content);
        Assert.DoesNotContain("admin_listAuthEvents", content);
        Assert.DoesNotContain("admin_listExports", content);

        var disabled = await new GetSpecHandler(db, user, selection)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { api = "na_git" }), default);
        Assert.Equal(
            "api spec not found (pass a valid spec_id or api)",
            disabled.GetProperty("error").GetString());
    }

    // The system prompt itself: the pages about capabilities this deployment does not
    // run are not in it, and the ones about capabilities it does run are.
    [Fact]
    public async Task The_system_prompt_carries_only_the_skills_this_deployment_can_act_on()
    {
        var coreOnly = await LoadPromptAsync("core");
        var withGit = await LoadPromptAsync("chat,ai-studio,integrations,git");
        var requestedMinimum = await LoadPromptAsync("chat,ai-studio,integrations,secrets");
        var automationOnly = await LoadPromptAsync("chat,ai-studio,integrations,automation");

        Assert.Contains("You are Nashira", coreOnly);
        Assert.DoesNotContain("# Skill: git", coreOnly);
        Assert.DoesNotContain("# Skill: inventory", coreOnly);
        Assert.Contains("# Skill: git — direct repository operations", withGit);
        Assert.DoesNotContain("the `git` workflow node", withGit);
        Assert.Contains("# Skill: secrets and credentials", requestedMinimum);
        Assert.Contains("# Skill: integrations", requestedMinimum);
        Assert.DoesNotContain("# Nashira — the platform you are operating", requestedMinimum);
        Assert.DoesNotContain("# Skill: troubleshooting", requestedMinimum);
        Assert.DoesNotContain("# Skill: inventory", requestedMinimum);
        Assert.DoesNotContain("# Skill: git", requestedMinimum);
        Assert.Contains("# Skill: snippets — automation-owned step types", automationOnly);
        Assert.DoesNotContain("`email_send`", automationOnly);
        Assert.DoesNotContain("`git` workflow node", automationOnly);
        Assert.DoesNotContain("git_list_webhooks", automationOnly);
    }

    // Admin → Skills lists what the deployment actually uses. A built-in offered for
    // editing but never loaded is an invitation to tune a prompt nothing reads.
    [Fact]
    public async Task The_builtin_listing_hides_the_skills_the_prompt_left_out()
    {
        var names = (await Loader("chat,ai-studio,integrations").ListBuiltinsAsync(default))
            .Select(skill => skill.Name)
            .ToList();

        Assert.Contains("base.md", names);
        Assert.DoesNotContain("git.md", names);
        Assert.DoesNotContain("workflows.md", names);
    }

    [Fact]
    public async Task A_disabled_builtin_skill_cannot_be_updated_by_direct_name()
    {
        var loader = Loader("chat,ai-studio,integrations,secrets");

        Assert.False(await loader.SaveBuiltinAsync("git.md", "replacement", default));
    }

    private static Task<string> LoadPromptAsync(string configuredModules) =>
        Loader(configuredModules).LoadAsync("(tools)", default);

    private static SkillPromptLoader Loader(string configuredModules)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase($"skills-{Guid.NewGuid()}"));
        var provider = services.BuildServiceProvider();

        return new SkillPromptLoader(
            new FakeEnv(BackendRoot()),
            provider.GetRequiredService<IServiceScopeFactory>(),
            ModuleSelection.Parse(configuredModules),
            NullLogger<SkillPromptLoader>.Instance);
    }

    private static AiApiSpec BuiltinSpec(string api) => new()
    {
        AiApiSpecId = Guid.NewGuid(),
        Api = api,
        Content = File.ReadAllText(Path.Combine(BackendRoot(), "Specs", $"{api}.yaml")),
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    private static AiApiSpec Spec(string api, string operationId) => new()
    {
        AiApiSpecId = Guid.NewGuid(),
        Api = api,
        Content = $"""
            openapi: 3.1.0
            info:
              title: {api}
              version: "1.0"
            paths:
              /api/{api}:
                get:
                  operationId: {operationId}
                  summary: seeded by an earlier deployment
            """,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed class FakeEnv(string root) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public string EnvironmentName { get; set; } = "Testing";
    }

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "module-content-test";
        public IReadOnlyList<string> Roles => ["Admin"];
        public bool IsAuthenticated => true;
    }
}
