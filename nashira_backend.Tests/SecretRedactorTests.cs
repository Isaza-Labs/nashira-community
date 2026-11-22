using System.Text.Json;
using nashira_backend.Services.Ai.Secrets;

namespace nashira_backend.Tests;

public class SecretRedactorTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_value_is_masked_wherever_it_appears_in_a_string()
    {
        Assert.Equal(
            "Bearer [redacted] and [redacted]",
            SecretRedactor.Redact("Bearer s3cret-value and s3cret-value", ["s3cret-value"]));
    }

    [Fact]
    public void Text_without_a_secret_is_returned_as_it_was()
    {
        const string text = "show version\nuptime 4 days";
        Assert.Same(text, SecretRedactor.Redact(text, ["s3cret-value"]));
        Assert.Same(text, SecretRedactor.Redact(text, []));
        Assert.Null(SecretRedactor.Redact(null, ["s3cret-value"]));
    }

    [Fact]
    public void Empty_values_are_ignored()
    {
        Assert.Equal("keep this", SecretRedactor.Redact("keep this", ["", ""]));
    }

    [Fact]
    public void The_form_a_value_takes_in_a_json_string_is_masked_too()
    {
        // What print(json.dumps({"p": value})) writes into a script's logs.
        const string value = "pa\"ss\\wörd-1";
        const string printed = "{\"p\": \"pa\\\"ss\\\\w\\u00f6rd-1\"}";

        Assert.Equal("{\"p\": \"[redacted]\"}", SecretRedactor.Redact(printed, [value]));
    }

    [Fact]
    public void The_json_escape_matches_what_python_writes()
    {
        Assert.Equal("a\\\"b\\\\c\\n\\t\\u0001\\u00e9z", SecretRedactor.JsonEscaped("a\"b\\c\n\t\u0001éz"));
    }

    [Fact]
    public void A_short_value_is_masked_only_when_it_is_the_whole_string()
    {
        Assert.Equal("[redacted]", SecretRedactor.Redact("abc", ["abc"]));
        Assert.Equal("abcdef abc", SecretRedactor.Redact("abcdef abc", ["abc"]));
    }

    [Fact]
    public void A_value_that_contains_another_is_masked_whole()
    {
        Assert.Equal("[redacted]", SecretRedactor.Redact("token-abcd-1234", ["abcd", "token-abcd-1234"]));
    }

    [Fact]
    public void A_value_that_is_part_of_the_mask_does_not_mask_the_mask()
    {
        Assert.Equal("x [redacted] y [redacted]", SecretRedactor.Redact("x redacted y redacted", ["redacted"]));
    }

    [Fact]
    public void Json_is_masked_in_values_keys_arrays_and_nested_objects()
    {
        var redacted = SecretRedactor.Redact(
            Json("""
                 {"a":"tok-12345","tok-12345":1,"list":["x","tok-12345",{"deep":"pre tok-12345 post"}],
                  "n":51234,"b":true,"z":null}
                 """),
            ["tok-12345"]);

        Assert.Equal("[redacted]", redacted.GetProperty("a").GetString());
        Assert.Equal(1, redacted.GetProperty("[redacted]").GetInt32());
        Assert.Equal("[redacted]", redacted.GetProperty("list")[1].GetString());
        Assert.Equal("pre [redacted] post", redacted.GetProperty("list")[2].GetProperty("deep").GetString());
        Assert.DoesNotContain("tok-12345", redacted.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_booleans_and_nulls_are_never_masked()
    {
        // A four-digit secret must not turn a port number into a mask.
        var redacted = SecretRedactor.Redact(Json("""{"port":51234,"ok":true,"none":null}"""), ["1234"]);

        Assert.Equal(51234, redacted.GetProperty("port").GetInt32());
        Assert.True(redacted.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, redacted.GetProperty("none").ValueKind);
    }

    [Fact]
    public void An_undefined_element_passes_through()
    {
        Assert.Equal(JsonValueKind.Undefined, SecretRedactor.Redact(default(JsonElement), ["s3cret-value"]).ValueKind);
    }
}
