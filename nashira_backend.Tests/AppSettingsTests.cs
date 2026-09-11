using nashira_backend.Configuration.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Settings;

namespace nashira_backend.Tests;

// Platform settings.
//
// The failure mode worth guarding against is a control that does not control anything:
// a value stored in the database that the code never reads, or a value the code reads
// once at startup so the screen appears to work and nothing changes.
public class AppSettingsTests
{
    private static readonly string DbName = $"settings-{Guid.NewGuid()}";

    private static ServiceProvider NewProvider(
        string database, Dictionary<string, string?>? config = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
        return services.BuildServiceProvider();
    }

    private static AppSettingsProvider NewSettings(
        ServiceProvider sp, Dictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? [])
            .Build();
        return new AppSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<AppSettingsProvider>.Instance);
    }

    // Every key on the screen must have a read site. A definition without one is a
    // control that controls nothing, which is what porting flow-weaver's four flags
    // verbatim would have produced.
    [Fact]
    public void Every_definition_is_complete_and_uniquely_keyed()
    {
        Assert.NotEmpty(AppSettingDefinition.All);
        Assert.Equal(
            AppSettingDefinition.All.Count,
            AppSettingDefinition.All.Select(d => d.Key).Distinct().Count());

        Assert.All(AppSettingDefinition.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayName), d.Key);
            Assert.False(string.IsNullOrWhiteSpace(d.Description), d.Key);
            Assert.False(string.IsNullOrWhiteSpace(d.Category), d.Key);
            Assert.True(
                d.InputType is AppSettingDefinition.TypeBool or AppSettingDefinition.TypeInt,
                $"'{d.Key}' has input type '{d.InputType}', which the screen cannot render");
        });
    }

    // A default that does not parse as its own type would be a setting nobody can reset.
    [Fact]
    public void Every_default_parses_as_its_declared_type()
    {
        Assert.All(AppSettingDefinition.All, d =>
        {
            if (d.InputType == AppSettingDefinition.TypeBool)
                Assert.True(bool.TryParse(d.Default, out _), $"'{d.Key}' default '{d.Default}'");
            else
                Assert.True(long.TryParse(d.Default, out _), $"'{d.Key}' default '{d.Default}'");
        });
    }

    [Fact]
    public async Task Seeding_writes_the_catalog_without_choosing_values()
    {
        var db = $"seed-{Guid.NewGuid()}";
        await using var sp = NewProvider(db);
        var settings = NewSettings(sp);

        await settings.SeedAsync();

        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = ctx.SystemSettings.Where(s => s.Provider == AppSettingDefinition.Provider).ToList();

        Assert.Equal(AppSettingDefinition.All.Count, rows.Count);
        // Null, not the default: an unset row means "configuration still decides", which
        // is not the same statement as "an admin chose this value".
        Assert.All(rows, r => Assert.Null(r.SettingValue));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.DisplayName)));
    }

    // Seeding runs on every boot. It must not undo what an admin set.
    [Fact]
    public async Task Seeding_again_does_not_overwrite_a_stored_value()
    {
        var db = $"reseed-{Guid.NewGuid()}";
        await using var sp = NewProvider(db);
        var settings = NewSettings(sp);
        await settings.SeedAsync();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = ctx.SystemSettings.First(s => s.SettingKey == "Ssh:AllowDestructiveCommands");
            row.SettingValue = "true";
            await ctx.SaveChangesAsync();
        }

        await settings.SeedAsync();

        Assert.True(settings.GetBool("Ssh:AllowDestructiveCommands", false));
    }

    // The fallback chain, in order. Configuration must still win where nothing is
    // stored, so an operator who set an environment variable is not silently ignored.
    [Fact]
    public async Task A_stored_value_wins_over_configuration_which_wins_over_the_default()
    {
        var db = $"chain-{Guid.NewGuid()}";
        await using var sp = NewProvider(db);
        var settings = NewSettings(sp, new Dictionary<string, string?>
        {
            ["Ssh:AllowDestructiveCommands"] = "true",
        });
        await settings.SeedAsync();

        // Nothing stored: configuration decides.
        Assert.True(settings.GetBool("Ssh:AllowDestructiveCommands", false));

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = ctx.SystemSettings.First(s => s.SettingKey == "Ssh:AllowDestructiveCommands");
            row.SettingValue = "false";
            await ctx.SaveChangesAsync();
        }
        await settings.RefreshAsync();

        // Stored now: the admin's choice wins over the environment.
        Assert.False(settings.GetBool("Ssh:AllowDestructiveCommands", true));
    }

    // Clearing a row must hand the setting back rather than pin it to the built-in
    // default, or an operator's environment variable would stop working after one edit.
    [Fact]
    public async Task Clearing_a_value_returns_the_setting_to_configuration()
    {
        var db = $"clear-{Guid.NewGuid()}";
        await using var sp = NewProvider(db);
        var settings = NewSettings(sp, new Dictionary<string, string?>
        {
            ["Python:PipInstallTimeoutSeconds"] = "900",
        });
        await settings.SeedAsync();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = ctx.SystemSettings.First(s => s.SettingKey == "Python:PipInstallTimeoutSeconds");
            row.SettingValue = "60";
            await ctx.SaveChangesAsync();
        }
        await settings.RefreshAsync();
        Assert.Equal(60, settings.GetInt("Python:PipInstallTimeoutSeconds", 300));

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = ctx.SystemSettings.First(s => s.SettingKey == "Python:PipInstallTimeoutSeconds");
            row.SettingValue = null;
            await ctx.SaveChangesAsync();
        }
        await settings.RefreshAsync();

        Assert.Equal(900, settings.GetInt("Python:PipInstallTimeoutSeconds", 300));
    }

    // A stored value that does not parse must not take the platform with it. Falling
    // back is the only safe answer: refusing to read would break every call site.
    [Fact]
    public async Task A_corrupt_stored_value_falls_back_instead_of_throwing()
    {
        var db = $"corrupt-{Guid.NewGuid()}";
        await using var sp = NewProvider(db);
        var settings = NewSettings(sp);
        await settings.SeedAsync();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = ctx.SystemSettings.First(s => s.SettingKey == "Git:MaxFileBytes");
            row.SettingValue = "not a number";
            await ctx.SaveChangesAsync();
        }
        await settings.RefreshAsync();

        Assert.Equal(5 * 1024 * 1024, settings.GetLong("Git:MaxFileBytes", 5 * 1024 * 1024));
    }

    // Reading before the first refresh must not throw: the SSH gate consults this on
    // every command, including any that run before the seeding service has finished.
    [Fact]
    public void Reading_before_the_first_load_uses_the_fallback()
    {
        var sp = NewProvider($"empty-{Guid.NewGuid()}");
        var settings = NewSettings(sp);

        Assert.False(settings.GetBool("Ssh:AllowDestructiveCommands", false));
        Assert.Equal(300, settings.GetInt("Python:PipInstallTimeoutSeconds", 300));
    }

    // A setting whose read site belongs to a capability this deployment does not run is
    // a control that controls nothing — the same argument that built the catalog in the
    // first place, applied to the deployment rather than to the codebase.
    [Fact]
    public void A_deployment_only_shows_settings_it_can_act_on()
    {
        var coreOnly = ModuleSelection.Parse("core");

        Assert.Empty(AppSettingDefinition.For(coreOnly));
        Assert.Equal(
            AppSettingDefinition.All.Count,
            AppSettingDefinition.For(ModuleSelection.Parse(null)).Count);
    }

    [Theory]
    [InlineData("fleet", "Ssh:AllowDestructiveCommands")]
    [InlineData("automation", "Python:PackageProvisioningEnabled")]
    [InlineData("automation", "Python:PipInstallTimeoutSeconds")]
    [InlineData("git", "Git:MaxFileBytes")]
    public void A_setting_appears_exactly_where_its_capability_is_enabled(
        string configuredModules, string key)
    {
        Assert.Contains(
            AppSettingDefinition.For(ModuleSelection.Parse(configuredModules)),
            definition => definition.Key == key);
        Assert.DoesNotContain(
            AppSettingDefinition.For(ModuleSelection.Parse("core")),
            definition => definition.Key == key);
    }

    // Hiding the control is not enough: writing the key is refused too, so a value
    // cannot be planted for a capability that is off and then take effect the day
    // somebody turns it on.
    [Fact]
    public void A_setting_of_a_disabled_capability_cannot_be_resolved_for_writing()
    {
        Assert.Null(AppSettingDefinition.Find("Git:MaxFileBytes", ModuleSelection.Parse("core")));
        Assert.NotNull(AppSettingDefinition.Find("Git:MaxFileBytes", ModuleSelection.Parse("git")));
        // The unfiltered lookup still answers: the exhaustiveness checks above read it.
        Assert.NotNull(AppSettingDefinition.Find("Git:MaxFileBytes"));
    }
}
