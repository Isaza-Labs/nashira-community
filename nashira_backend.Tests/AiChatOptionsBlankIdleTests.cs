using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using nashira_backend.Configuration;

namespace nashira_backend.Tests;

// A blank AiChat:StreamIdleSeconds is read as off. Nothing else in the section may be
// read differently because of it: not another setting's value, not whether another
// setting is there at all, not the error a bad one gives.
//
// The measure is the registration AddAiChatOptions replaced,
// services.Configure<AiChatOptions>(section), on the same configuration with the limit
// written as 0 and with the limit left out. What the two give is compared whole: the
// options read, or the error with its message and the error under it.
public class AiChatOptionsBlankIdleTests
{
    private const string Section = AiChatOptions.SectionName;
    private const string IdleName = nameof(AiChatOptions.StreamIdleSeconds);

    private static readonly string[] Blanks = ["", " \t "];

    // Every setting but the limit, found by reflection, so a setting added later is
    // covered without anyone remembering this file.
    private static readonly PropertyInfo[] Others = typeof(AiChatOptions)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanWrite && property.Name != IdleName)
        .ToArray();

    // The limit as a configuration writes it: left out, or there with a value.
    private readonly record struct Limit(bool Present, string? Value)
    {
        public static readonly Limit LeftOut = default;
        public static Limit Of(string? value) => new(true, value);
    }

    // The one configuration here with a limit in a second, lower source: 60 in
    // appsettings under whatever the environment gives. Leaving the limit out of the
    // environment there uncovers the 60, so "left out" is not what a blank is measured
    // against in it; a blank that wins is off (0).
    private const string NumberBelow = "a number in appsettings, the limit in the environment";

    private static readonly Dictionary<string, Func<Limit, IConfigurationRoot>> Scenarios = BuildScenarios();

    private static Dictionary<string, Func<Limit, IConfigurationRoot>> BuildScenarios()
    {
        var scenarios = new Dictionary<string, Func<Limit, IConfigurationRoot>>
        {
            ["nothing else set"] = limit => Sources(Memory(limit)),
            ["every other setting changed"] = limit => Sources(Memory(limit, EveryOtherSettingChanged())),
            ["an unknown key, and keys under settings"] = limit => Sources(Memory(limit, new()
            {
                [$"{Section}:NotAnOption"] = "ignored",
                [$"{Section}:MaxIterations"] = "7",
                [$"{Section}:MaxIterations:Note"] = "why",
                [$"{Section}:HistoryCharBudget:Note"] = "no value beside it",
                [$"{Section}:{IdleName}:Comment"] = "optional",
                [$"Other:{IdleName}"] = "9",
            })),
            ["the section itself has a value"] = limit => Sources(Memory(limit, new()
            {
                [Section] = "text",
                [$"{Section}:MaxIterations"] = "7",
            })),
            ["keys in lower case"] = limit => Sources(Memory(limit, new()
            {
                ["aichat:maxiterations"] = "7",
                ["aichat:historycharbudget"] = "8",
            }, idleKey: "aichat:streamidleseconds")),
            ["an empty value over a number"] = limit => Sources(
                Memory(Limit.LeftOut, new() { [$"{Section}:MaxIterations"] = "7" }),
                Memory(limit, new() { [$"{Section}:MaxIterations"] = "" })),
            ["a number over an empty value"] = limit => Sources(
                Memory(Limit.LeftOut, new() { [$"{Section}:MaxIterations"] = "" }),
                Memory(limit, new() { [$"{Section}:MaxIterations"] = "7" })),
            ["no value over a number"] = limit => Sources(
                Memory(Limit.LeftOut, new() { [$"{Section}:HistoryCharBudget"] = "7" }),
                AppSettings(Limit.LeftOut, new() { ["HistoryCharBudget"] = null }),
                Memory(limit)),
            ["a number over no value"] = limit => Sources(
                AppSettings(Limit.LeftOut, new() { ["HistoryCharBudget"] = null }),
                Memory(limit, new() { [$"{Section}:HistoryCharBudget"] = "7" })),
            ["the limit and an empty value in one appsettings file"] = limit => Sources(
                AppSettings(limit, new() { ["MaxIterations"] = "" })),
            ["the limit and an empty deadline in one appsettings file"] = limit => Sources(
                AppSettings(limit, new() { ["StreamDeadlineSeconds"] = "" })),
            ["the limit and no value in one appsettings file"] = limit => Sources(
                AppSettings(limit, new() { ["HistoryCharBudget"] = null })),
            [NumberBelow] = limit => Sources(
                AppSettings(Limit.Of("60"), new() { ["MaxIterations"] = 7, ["ScopedPromptAfterUserMessage"] = true }),
                Memory(limit)),
            ["the limit in appsettings and again in the environment"] = limit => Sources(
                AppSettings(limit, new() { ["MaxIterations"] = 7 }),
                Memory(limit)),
            ["settings from a configuration added whole"] = limit => Sources(
                Whole(Sources(Memory(Limit.LeftOut, new()
                {
                    [$"{Section}:MaxIterations"] = "",
                    [$"{Section}:HistoryCharBudget"] = "7",
                }))),
                Memory(limit)),
        };
        foreach (var other in Others)
        {
            var name = other.Name;
            scenarios[$"{name} changed"] = limit => Sources(Memory(limit, new() { [$"{Section}:{name}"] = Changed(other) }));
            scenarios[$"{name} empty"] = limit => Sources(Memory(limit, new() { [$"{Section}:{name}"] = "" }));
            scenarios[$"{name} whitespace"] = limit => Sources(Memory(limit, new() { [$"{Section}:{name}"] = "  " }));
            scenarios[$"{name} not a value it can hold"] = limit => Sources(Memory(limit, new() { [$"{Section}:{name}"] = "2.5x" }));
            scenarios[$"{name} with no value in memory"] = limit => Sources(Memory(limit, new() { [$"{Section}:{name}"] = null }));
            scenarios[$"{name} with no value in appsettings"] = limit => Sources(AppSettings(limit, new() { [name] = null }));
        }
        return scenarios;
    }

    public static TheoryData<string, string> EveryScenarioWithABlank()
    {
        var data = new TheoryData<string, string>();
        foreach (var scenario in Scenarios.Keys)
            foreach (var blank in Blanks)
                data.Add(scenario, blank);
        return data;
    }

    public static TheoryData<string, bool, string?> EveryScenarioWithoutABlank()
    {
        var data = new TheoryData<string, bool, string?>();
        foreach (var scenario in Scenarios.Keys)
        {
            data.Add(scenario, false, null);
            data.Add(scenario, true, null);
            data.Add(scenario, true, "0");
            data.Add(scenario, true, "60");
            data.Add(scenario, true, "2.5");
        }
        return data;
    }

    public static TheoryData<string, string> EveryOtherSettingWithABlank()
    {
        var data = new TheoryData<string, string>();
        foreach (var other in Others)
            foreach (var blank in Blanks)
                data.Add(other.Name, blank);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryScenarioWithABlank))]
    public void With_a_blank_limit_the_section_is_read_as_the_plain_binding_reads_it_with_the_limit_off(string scenario, string blank)
    {
        var configured = Scenarios[scenario];

        var read = Outcome(() => Ours(configured(Limit.Of(blank))));

        Assert.Equal(Outcome(() => Plain(configured(Limit.Of("0")))), read);
        if (scenario != NumberBelow)
            Assert.Equal(Outcome(() => Plain(configured(Limit.LeftOut))), read);
    }

    // The limit absent, with no value, or anything that is not blank goes to the plain
    // binding untouched: the same options, or the same error.
    [Theory]
    [MemberData(nameof(EveryScenarioWithoutABlank))]
    public void Without_a_blank_limit_the_section_is_read_as_the_plain_binding_reads_it(string scenario, bool present, string? value)
    {
        var configured = Scenarios[scenario];
        var limit = present ? Limit.Of(value) : Limit.LeftOut;

        Assert.Equal(Outcome(() => Plain(configured(limit))), Outcome(() => Ours(configured(limit))));
    }

    // The comparisons above would also pass if both sides were wrong the same way, so
    // these say outright what a blank limit must not change.

    [Theory]
    [MemberData(nameof(EveryOtherSettingWithABlank))]
    public void An_empty_value_in_another_setting_is_still_refused_and_named_in_full(string setting, string blank)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Ours(Scenarios[$"{setting} empty"](Limit.Of(blank))));

        Assert.Contains($"'{Section}:{setting}'", error.Message);
    }

    [Theory]
    [MemberData(nameof(EveryOtherSettingWithABlank))]
    public void A_value_another_setting_cannot_hold_is_still_refused_and_named_in_full(string setting, string blank)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Ours(Scenarios[$"{setting} not a value it can hold"](Limit.Of(blank))));

        Assert.Contains($"'{Section}:{setting}'", error.Message);
    }

    // A setting written with no value (null in appsettings) is there, and the binder
    // gives it the empty value of its kind: 0 for a number, false for a switch. Left
    // out, it keeps its default. A blank limit must not turn the first into the second.
    [Theory]
    [MemberData(nameof(EveryOtherSettingWithABlank))]
    public void Another_setting_written_with_no_value_is_still_read_as_written_not_as_left_out(string setting, string blank)
    {
        var property = Others.Single(other => other.Name == setting);
        var empty = property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;

        foreach (var source in new[] { "appsettings", "memory" })
        {
            var read = Ours(Scenarios[$"{setting} with no value in {source}"](Limit.Of(blank)));

            Assert.Equal(empty, property.GetValue(read));
            Assert.Equal(0, read.StreamIdleSeconds);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Every_other_setting_is_read_when_the_limit_is_blank(string blank)
    {
        var defaults = new AiChatOptions();

        var read = Ours(Scenarios["every other setting changed"](Limit.Of(blank)));

        Assert.NotEmpty(Others);
        foreach (var other in Others)
            Assert.NotEqual(other.GetValue(defaults), other.GetValue(read));
        Assert.Equal(0, read.StreamIdleSeconds);
    }

    // The three configurations the review of f9b2c0d named, as it wrote them.
    [Fact]
    public void A_blank_limit_does_not_hide_an_empty_iteration_cap()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Ours(Json("""{"AiChat":{"StreamIdleSeconds":"","MaxIterations":""}}""")));

        Assert.Contains("'AiChat:MaxIterations'", error.Message);
    }

    [Fact]
    public void A_whitespace_limit_does_not_hide_an_empty_deadline()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Ours(Json("""{"AiChat":{"StreamIdleSeconds":" \t ","StreamDeadlineSeconds":""}}""")));

        Assert.Contains("'AiChat:StreamDeadlineSeconds'", error.Message);
    }

    [Fact]
    public void A_blank_limit_does_not_bring_back_a_history_budget_written_with_no_value()
    {
        var read = Ours(Json("""{"AiChat":{"StreamIdleSeconds":"","HistoryCharBudget":null}}"""));

        Assert.Equal(0, read.HistoryCharBudget);
        Assert.Equal(0, read.StreamIdleSeconds);
    }

    // Reading a blank asks the application's sources and nothing more: no source is
    // written to, and the configuration holds the same keys and values afterwards.
    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Reading_a_blank_limit_writes_nothing_to_the_configuration(string blank)
    {
        var source = new RefusesWrites(new()
        {
            [$"{Section}:{IdleName}"] = blank,
            [$"{Section}:MaxIterations"] = "7",
            [$"{Section}:HistoryCharBudget"] = null,
        });
        var configuration = new ConfigurationRoot(new List<IConfigurationProvider> { source });
        var before = Snapshot(configuration);

        var read = Ours(configuration);

        Assert.Equal(7, read.MaxIterations);
        Assert.Equal(0, read.StreamIdleSeconds);
        Assert.Equal(0, source.Writes);
        Assert.Equal(before, Snapshot(configuration));
        Assert.Equal(blank, configuration[$"{Section}:{IdleName}"]);
    }

    // What is built to read a blank is thrown away after the read. The application's
    // sources are not closed with it, and it does not ask them to be told of changes.
    [Fact]
    public void Reading_a_blank_limit_leaves_the_configurations_sources_open_and_unwatched()
    {
        var source = new Watched(new() { [$"{Section}:{IdleName}"] = "", [$"{Section}:MaxIterations"] = "7" });
        var configuration = new ConfigurationRoot(new List<IConfigurationProvider> { source });
        var watchersBefore = source.TokensAsked;

        for (var i = 0; i < 3; i++)
            Assert.Equal(7, Ours(configuration).MaxIterations);

        Assert.False(source.Closed);
        Assert.Equal(watchersBefore, source.TokensAsked);
        Assert.Equal("7", configuration[$"{Section}:MaxIterations"]);
    }

    // An options monitor reads again after a reload. A number, then a blank, then a
    // number again, with another setting changing beside it: each read is of what the
    // configuration holds then.
    [Fact]
    public void A_monitor_reads_a_number_then_a_blank_then_a_number_as_the_configuration_changes()
    {
        var source = new MemoryConfigurationProvider(new MemoryConfigurationSource());
        source.Set($"{Section}:{IdleName}", "60");
        source.Set($"{Section}:MaxIterations", "7");
        var configuration = new ConfigurationRoot(new List<IConfigurationProvider> { source });
        var services = new ServiceCollection();
        services.AddAiChatOptions(configuration);
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AiChatOptions>>();
        Assert.Equal(60, monitor.CurrentValue.StreamIdleSeconds);

        source.Set($"{Section}:{IdleName}", "  ");
        source.Set($"{Section}:MaxIterations", "8");
        configuration.Reload();
        Assert.Equal(0, monitor.CurrentValue.StreamIdleSeconds);
        Assert.Equal(8, monitor.CurrentValue.MaxIterations);

        source.Set($"{Section}:{IdleName}", "90");
        source.Set($"{Section}:MaxIterations", "9");
        configuration.Reload();
        Assert.Equal(90, monitor.CurrentValue.StreamIdleSeconds);
        Assert.Equal(9, monitor.CurrentValue.MaxIterations);
    }

    // ── what is compared ──────────────────────────────────────────

    private static AiChatOptions Ours(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddAiChatOptions(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AiChatOptions>>().Value;
    }

    private static AiChatOptions Plain(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.Configure<AiChatOptions>(configuration.GetSection(Section));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AiChatOptions>>().Value;
    }

    private static string Outcome(Func<AiChatOptions> read)
    {
        try { return "read " + JsonSerializer.Serialize(read()); }
        catch (Exception error) { return "failed " + Describe(error); }
    }

    private static string Describe(Exception error) =>
        $"{error.GetType().FullName}: {error.Message}" + (error.InnerException is { } inner ? " <- " + Describe(inner) : "");

    private static List<string> Snapshot(IConfigurationRoot configuration) =>
        configuration.AsEnumerable().OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={(pair.Value is null ? "<no value>" : $"[{pair.Value}]")}").ToList();

    // ── configurations ────────────────────────────────────────────

    // A value for a setting that is not its default, by the setting's kind. A kind
    // this does not know fails the test, so a new kind of setting gets a value here.
    private static string Changed(PropertyInfo setting)
    {
        var initial = setting.GetValue(new AiChatOptions());
        return initial switch
        {
            int number => (number + 7).ToString(System.Globalization.CultureInfo.InvariantCulture),
            bool flag => flag ? "false" : "true",
            _ => throw new InvalidOperationException(
                $"AiChatOptions.{setting.Name} is a {setting.PropertyType.Name}: give this test a value for that kind."),
        };
    }

    private static Dictionary<string, string?> EveryOtherSettingChanged() =>
        Others.ToDictionary(other => $"{Section}:{other.Name}", other => (string?)Changed(other));

    private static IConfigurationRoot Sources(params IConfigurationSource[] sources)
    {
        var builder = new ConfigurationBuilder();
        foreach (var source in sources) builder.Add(source);
        return builder.Build();
    }

    // Settings as the environment or a command line gives them: text, by full key.
    private static IConfigurationSource Memory(Limit limit, Dictionary<string, string?>? settings = null, string? idleKey = null)
    {
        var data = settings is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(settings);
        if (limit.Present) data[idleKey ?? $"{Section}:{IdleName}"] = limit.Value;
        return new MemoryConfigurationSource { InitialData = data };
    }

    // Settings as appsettings.json gives them, where a value has a shape and a
    // setting can be written with no value (null).
    private static IConfigurationSource AppSettings(Limit limit, Dictionary<string, JsonNode?> settings)
    {
        var section = new JsonObject();
        if (limit.Present) section[IdleName] = limit.Value;
        foreach (var (name, value) in settings) section[name] = value?.DeepClone();
        var json = new JsonObject { [Section] = section }.ToJsonString();
        return new Microsoft.Extensions.Configuration.Json.JsonStreamConfigurationSource
        {
            Stream = new MemoryStream(Encoding.UTF8.GetBytes(json)),
        };
    }

    // Another configuration added whole (AddConfiguration), as a host adds one.
    private static IConfigurationSource Whole(IConfigurationRoot configuration) =>
        new ChainedConfigurationSource { Configuration = configuration };

    private static IConfigurationRoot Json(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private sealed class RefusesWrites(Dictionary<string, string?> settings)
        : MemoryConfigurationProvider(new MemoryConfigurationSource { InitialData = settings })
    {
        public int Writes { get; private set; }

        public override void Set(string key, string? value)
        {
            Writes++;
            throw new InvalidOperationException($"Something wrote {key} into the configuration.");
        }
    }

    private sealed class Watched(Dictionary<string, string?> settings)
        : MemoryConfigurationProvider(new MemoryConfigurationSource { InitialData = settings }), IConfigurationProvider, IDisposable
    {
        public int TokensAsked { get; private set; }
        public bool Closed { get; private set; }

        IChangeToken IConfigurationProvider.GetReloadToken()
        {
            TokensAsked++;
            return GetReloadToken();
        }

        public void Dispose() => Closed = true;
    }
}
