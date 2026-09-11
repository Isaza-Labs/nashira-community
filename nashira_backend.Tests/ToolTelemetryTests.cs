using System.Text.Json;
using nashira_backend.Services.Ai.Conversation;

namespace nashira_backend.Tests;

// What gets written into the forensic record. Two things must hold: a secret must
// never survive the trip, and one enormous tool result must never decide whether the
// row can be written at all.
public class ToolTelemetryTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static string Redacted(string raw) =>
        ToolTelemetry.Redact(Json(raw))?.ToJsonString() ?? "null";

    // set_secret carries the value, create_credential carries the password. Recording
    // them verbatim would put plaintext into the table built to be read by an auditor.
    [Theory]
    [InlineData("""{"provider":"netbox","key":"api_token","value":"88e1c697"}""", "88e1c697")]
    [InlineData("""{"name":"svc","password":"hunter2"}""", "hunter2")]
    [InlineData("""{"token":"t-123"}""", "t-123")]
    [InlineData("""{"api_key":"k-123"}""", "k-123")]
    [InlineData("""{"client_secret":"cs-123"}""", "cs-123")]
    [InlineData("""{"private_key":"-----BEGIN OPENSSH PRIVATE KEY-----"}""", "BEGIN OPENSSH")]
    public void Secret_arguments_never_reach_the_record(string args, string secret)
    {
        var json = Redacted(args);
        Assert.DoesNotContain(secret, json);
        Assert.Contains(ToolTelemetry.RedactedMarker, json);
    }

    [Fact]
    public void Secrets_are_redacted_at_any_depth()
    {
        var json = Redacted("""{"outer":{"list":[{"password":"hunter2"}]}}""");
        Assert.DoesNotContain("hunter2", json);
    }

    // The whole point of the record is being able to see what went out. Over-redacting
    // hides the field an operator is trying to diagnose: `api_key_header` names a
    // header, `key` names a setting, and neither is secret material.
    [Fact]
    public void Non_secret_arguments_survive_intact()
    {
        var json = Redacted("""{"provider":"netbox","key":"api_token","api_key_header":"Authorization"}""");
        Assert.Contains("netbox", json);
        Assert.Contains("api_token", json);
        Assert.Contains("Authorization", json);
        Assert.DoesNotContain(ToolTelemetry.RedactedMarker, json);
    }

    [Fact]
    public void An_absent_argument_object_records_nothing()
    {
        Assert.Null(ToolTelemetry.Redact(Json("null")));
    }

    [Fact]
    public void A_call_is_recorded_with_its_name_outcome_and_timing()
    {
        var json = ToolTelemetry.Serialize([
            new ToolTelemetryEntry("list_integrations", Json("{}"), true, Json("""{"count":2}"""), 42),
        ]);

        using var doc = JsonDocument.Parse(json);
        var call = doc.RootElement[0];
        Assert.Equal("list_integrations", call.GetProperty("name").GetString());
        Assert.True(call.GetProperty("ok").GetBoolean());
        Assert.Equal(42, call.GetProperty("elapsed_ms").GetInt32());
        Assert.Equal(2, call.GetProperty("result").GetProperty("count").GetInt32());
    }

    // A NetBox page with three hundred devices on it is a legitimate tool result and a
    // terrible database row. The sequence of calls is what has to survive.
    [Fact]
    public void An_oversized_turn_keeps_the_sequence_and_drops_the_payloads()
    {
        var big = Json($$"""{"data":"{{new string('x', 200_000)}}"}""");
        var json = ToolTelemetry.Serialize([
            new ToolTelemetryEntry("execute_operation", Json("{}"), true, big, 10),
            new ToolTelemetryEntry("mcp_call", Json("{}"), true, big, 20),
        ]);

        Assert.True(json.Length < 10_000);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("execute_operation", doc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("mcp_call", doc.RootElement[1].GetProperty("name").GetString());
    }

    [Fact]
    public void No_calls_records_an_empty_array_rather_than_null()
    {
        Assert.Equal("[]", ToolTelemetry.Serialize([]));
    }

    [Fact]
    public void Long_text_is_capped_with_a_visible_marker()
    {
        var capped = ToolTelemetry.Cap(new string('a', ToolTelemetry.MaxTextChars + 500));
        Assert.Contains("truncated", capped);
        Assert.True(capped.Length < ToolTelemetry.MaxTextChars + 100);
    }
}
