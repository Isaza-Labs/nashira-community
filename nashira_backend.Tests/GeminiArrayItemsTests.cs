using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// Gemini requires `items` on every ARRAY and rejects the whole request without
// one, naming the offender only by index:
//
//   GenerateContentRequest.tools[0].function_declarations[87]
//     .parameters.properties[edges].items: missing field.
//
// One under-specified property in one of a hundred tools therefore takes down
// every turn, and the index is not a thing anyone can look up. OpenAI and
// Anthropic accept the same schema, so a tool written against them passes review
// and only fails here.
public class GeminiArrayItemsTests
{
    private static GeminiProvider Build(FakeHttpMessageHandler handler)
        => new(new HttpClient(handler, disposeHandler: false), "AIza-test", null,
            NullLogger<GeminiProvider>.Instance);

    private static List<LlmMessage> UserSays()
        => [new LlmMessage { Role = "user", Content = "hi" }];

    private static async Task<JsonElement> ParametersForAsync(string schemaJson)
    {
        var handler = new FakeHttpMessageHandler(
            System.Net.HttpStatusCode.OK,
            """{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""",
            "application/json");

        var tools = new List<ToolDefinition>
        {
            new() { Name = "create_workflow", Description = "Creates", ParametersSchema = TestJson.Element(schemaJson) },
        };
        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "gemini-2.5-pro", 0.2, default);

        var body = JsonDocument.Parse(handler.RequestBodies[^1]).RootElement.Clone();
        return body.GetProperty("tools").EnumerateArray().First()
            .GetProperty("functionDeclarations").EnumerateArray().First()
            .GetProperty("parameters");
    }

    [Fact]
    public async Task An_array_property_without_items_still_gets_one()
    {
        var parameters = await ParametersForAsync(
            """{"type":"object","properties":{"edges":{"type":"array","description":"workflow.v1 edges"}}}""");

        var edges = parameters.GetProperty("properties").GetProperty("edges");
        Assert.Equal("ARRAY", edges.GetProperty("type").GetString());
        Assert.True(edges.TryGetProperty("items", out var items),
            "Gemini rejects the entire request when an ARRAY has no items");
        Assert.Equal("OBJECT", items.GetProperty("type").GetString());
    }

    // The net must not overwrite a schema that says what it holds.
    [Fact]
    public async Task A_declared_items_schema_is_left_alone()
    {
        var parameters = await ParametersForAsync(
            """{"type":"object","properties":{"tags":{"type":"array","items":{"type":"string"}}}}""");

        var items = parameters.GetProperty("properties").GetProperty("tags").GetProperty("items");
        Assert.Equal("STRING", items.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Nested_arrays_are_covered_too()
    {
        var parameters = await ParametersForAsync("""
            {"type":"object","properties":{
              "graph":{"type":"object","properties":{"nodes":{"type":"array"}}}
            }}
            """);

        var nodes = parameters.GetProperty("properties").GetProperty("graph")
            .GetProperty("properties").GetProperty("nodes");
        Assert.Equal("OBJECT", nodes.GetProperty("items").GetProperty("type").GetString());
    }

    // A non-array keeps its shape: `items` on an OBJECT is not valid either.
    [Fact]
    public async Task Object_properties_do_not_gain_items()
    {
        var parameters = await ParametersForAsync(
            """{"type":"object","properties":{"metadata":{"type":"object","description":"free form"}}}""");

        var metadata = parameters.GetProperty("properties").GetProperty("metadata");
        Assert.Equal("OBJECT", metadata.GetProperty("type").GetString());
        Assert.False(metadata.TryGetProperty("items", out _));
    }
}
