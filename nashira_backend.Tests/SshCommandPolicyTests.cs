using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Settings;
using nashira_backend.Services.Ssh;

namespace nashira_backend.Tests;

// Per-command SSH governance: classification (read/mutation/destructive) and the
// destructive-command block (opt-in via Ssh:AllowDestructiveCommands).
public class SshCommandPolicyTests
{
    // The gate now reads through AppSettingsProvider on every call rather than
    // capturing the flag at construction: it is a singleton, so a captured value could
    // only be changed by restarting the process. With nothing stored, the provider
    // falls through to configuration, which is what this sets.
    private static SshCommandPolicy Policy(bool allowDestructive = false)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseInMemoryDatabase($"ssh-policy-{Guid.NewGuid()}"));
        var sp = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ssh:AllowDestructiveCommands"] = allowDestructive.ToString(),
            })
            .Build();

        var settings = new AppSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<AppSettingsProvider>.Instance);

        return new SshCommandPolicy(settings);
    }

    [Theory]
    [InlineData("show version", SshCommandPolicy.Read)]
    [InlineData("display interfaces terse", SshCommandPolicy.Read)]
    [InlineData("ping 10.0.0.1", SshCommandPolicy.Read)]
    [InlineData("configure terminal", SshCommandPolicy.Mutation)]
    [InlineData("set system host-name r1", SshCommandPolicy.Mutation)]
    [InlineData("write memory", SshCommandPolicy.Mutation)]
    [InlineData("reload", SshCommandPolicy.Destructive)]
    [InlineData("write erase", SshCommandPolicy.Destructive)]
    [InlineData("erase startup-config", SshCommandPolicy.Destructive)]
    [InlineData("delete flash:config.bak", SshCommandPolicy.Destructive)]
    [InlineData("frobnicate the widget", SshCommandPolicy.Mutation)]
    public void Classify_labels_command(string cmd, string expected) =>
        Assert.Equal(expected, Policy().Classify(cmd));

    [Fact]
    public void Destructive_command_is_blocked_by_default()
    {
        var d = Policy().Evaluate(["show version", "reload"]);
        Assert.False(d.Allowed);
        Assert.Contains("reload", d.Blocked);
    }

    [Fact]
    public void Destructive_command_allowed_when_opted_in()
    {
        var d = Policy(allowDestructive: true).Evaluate(["reload"]);
        Assert.True(d.Allowed);
        Assert.Empty(d.Blocked);
    }

    [Fact]
    public void Read_only_batch_is_allowed()
    {
        var d = Policy().Evaluate(["show version", "show ip int brief"]);
        Assert.True(d.Allowed);
        Assert.All(d.Intents, i => Assert.Equal(SshCommandPolicy.Read, i.Intent));
    }
}
