using nashira_backend.Data.Models;
using nashira_backend.Services.Messaging;
using Xunit;

namespace nashira_backend.Tests;

// The two free-form JSON columns are read on the inbound webhook path, where a
// throw is the difference between "the bot ignored me" and a useful error. These
// cover the degradation rules rather than the happy path alone.
public class MessagingChannelConfigTests
{
    private static MessagingChannel Channel(string? config = null, string? allowed = null) => new()
    {
        MessagingChannelId = Guid.NewGuid(),
        Provider = MessagingChannel.ProviderTeams,
        Name = "ops",
        ExternalConfigJson = config,
        AllowedExternalIdsJson = allowed,
    };

    [Fact]
    public void Reads_string_values()
    {
        var c = Channel("""{"app_id":"abc","tenant_id":"def"}""");
        Assert.Equal("abc", MessagingChannelConfig.External(c, "app_id"));
        Assert.Equal("def", MessagingChannelConfig.External(c, "tenant_id"));
    }

    // A hand-written number or boolean still has to reach the provider intact,
    // rather than silently becoming "not configured".
    [Fact]
    public void Non_string_values_survive_as_their_raw_text()
    {
        var c = Channel("""{"graph_version":21,"strict":true}""");
        Assert.Equal("21", MessagingChannelConfig.External(c, "graph_version"));
        Assert.Equal("true", MessagingChannelConfig.External(c, "strict"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]      // an array is not a config object
    [InlineData("\"a string\"")] // nor is a bare scalar
    public void A_broken_or_absent_blob_degrades_to_empty_instead_of_throwing(string? json)
    {
        var c = Channel(json);
        Assert.Empty(MessagingChannelConfig.External(c));
        Assert.Null(MessagingChannelConfig.External(c, "app_id"));
    }

    [Fact]
    public void Blank_and_null_values_are_treated_as_absent()
    {
        var c = Channel("""{"app_id":"","tenant_id":null,"ok":"yes"}""");
        Assert.Null(MessagingChannelConfig.External(c, "app_id"));
        Assert.Null(MessagingChannelConfig.External(c, "tenant_id"));
        Assert.Equal("yes", MessagingChannelConfig.External(c, "ok"));
    }

    [Fact]
    public void Lookup_is_case_insensitive()
        => Assert.Equal("abc", MessagingChannelConfig.External(Channel("""{"App_Id":"abc"}"""), "app_id"));

    // Blanks are dropped on write so an untouched optional field trips the
    // provider's "missing" branch rather than its "empty" one.
    [Fact]
    public void Serialize_drops_blank_entries_and_trims()
    {
        var json = MessagingChannelConfig.Serialize(new Dictionary<string, string?>
        {
            ["app_id"] = "  abc  ",
            ["tenant_id"] = "",
            ["other"] = null,
            ["  spaced  "] = "v",
        });

        var parsed = MessagingChannelConfig.Parse(json);
        Assert.Equal("abc", parsed["app_id"]);
        Assert.Equal("v", parsed["spaced"]);
        Assert.False(parsed.ContainsKey("tenant_id"));
        Assert.False(parsed.ContainsKey("other"));
    }

    [Fact]
    public void Serialize_returns_null_when_nothing_survives()
    {
        Assert.Null(MessagingChannelConfig.Serialize(null));
        Assert.Null(MessagingChannelConfig.Serialize(new Dictionary<string, string?> { ["a"] = "  " }));
    }

    [Fact]
    public void Allowlist_round_trips_and_deduplicates()
    {
        var json = MessagingChannelConfig.SerializeIdList(["U1", " U2 ", "U1", "", "  "]);
        var ids = MessagingChannelConfig.ParseIdList(json);
        Assert.Equal(["U1", "U2"], ids);
    }

    // Empty means "no allowlist", not "nobody" — the ingest only enforces the list
    // when it has entries, so a broken blob must not silently lock everyone out.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{\"not\":\"an array\"}")]
    public void A_broken_or_absent_allowlist_means_no_allowlist(string? json)
        => Assert.Empty(MessagingChannelConfig.ParseIdList(json));

    [Fact]
    public void An_empty_allowlist_serializes_to_null()
        => Assert.Null(MessagingChannelConfig.SerializeIdList([]));
}
