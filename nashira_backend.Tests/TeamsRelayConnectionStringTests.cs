using nashira_backend.Exceptions;
using nashira_backend.Services.Messaging;
using Xunit;

namespace nashira_backend.Tests;

// A wrong Relay credential is invisible everywhere except the backend log: the
// listener retries forever and the channel row keeps saying "Azure Relay · no
// ingress". These cover the save-time rejection that makes it visible.
public class TeamsRelayConnectionStringTests
{
    private const string Valid =
        "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=listen;"
        + "SharedAccessKey=abc123=;EntityPath=teams-bot";

    [Fact]
    public void Accepts_a_hybrid_connection_string()
    {
        TeamsRelayConnectionString.Validate(Valid);
    }

    // Whitespace around a pasted credential is the norm, not the exception.
    [Fact]
    public void Accepts_a_padded_connection_string()
    {
        TeamsRelayConnectionString.Validate($"  {Valid}\n");
    }

    // The field is optional: a Teams channel without one receives over the public
    // webhook instead, which is a valid configuration.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_is_not_an_error(string? value)
    {
        TeamsRelayConnectionString.Validate(value);
    }

    // The mistake the error text exists for.
    [Theory]
    [InlineData("https://ns.servicebus.windows.net/teams-bot")]
    [InlineData("http://ns.servicebus.windows.net/teams-bot")]
    public void Rejects_the_hybrid_connection_url(string url)
    {
        var ex = Assert.Throws<ValidationException>(() => TeamsRelayConnectionString.Validate(url));
        Assert.Equal("teams_relay_url_not_connection_string", ex.Code);
    }

    // A namespace-level policy string parses as a connection string but names no
    // Hybrid Connection, so the listener attaches to nothing.
    [Fact]
    public void Rejects_a_namespace_string_with_no_entity_path()
    {
        var ex = Assert.Throws<ValidationException>(() => TeamsRelayConnectionString.Validate(
            "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=listen;SharedAccessKey=abc123="));
        Assert.Equal("teams_relay_connection_string_invalid", ex.Code);
        Assert.Contains("EntityPath", ex.Message);
    }

    [Fact]
    public void Rejects_a_string_with_no_key()
    {
        var ex = Assert.Throws<ValidationException>(() => TeamsRelayConnectionString.Validate(
            "Endpoint=sb://ns.servicebus.windows.net/;EntityPath=teams-bot"));
        Assert.Equal("teams_relay_connection_string_invalid", ex.Code);
        Assert.Contains("SharedAccessKey", ex.Message);
    }

    // Reporting one missing part at a time turns a single bad paste into a
    // sequence of failed saves.
    [Fact]
    public void Names_every_missing_part_at_once()
    {
        var ex = Assert.Throws<ValidationException>(
            () => TeamsRelayConnectionString.Validate("SharedAccessKeyName=listen"));
        Assert.Contains("Endpoint=sb://", ex.Message);
        Assert.Contains("SharedAccessKey", ex.Message);
        Assert.Contains("EntityPath", ex.Message);
    }
}
