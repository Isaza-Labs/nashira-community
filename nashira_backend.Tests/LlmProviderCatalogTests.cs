using System.Runtime.CompilerServices;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Ai.Tools.Handlers;

namespace nashira_backend.Tests;

// The provider type list is validated in two places and built in a third. It used
// to be copied into each, and the copies disagreed: `anthropic` and `ollama` were
// accepted by both validators and then threw NotImplementedException at the
// factory. These tests are what stops that from coming back.
public class LlmProviderCatalogTests
{
    // Uninitialized, as in ToolCatalogTests: ParametersSchema is a static field
    // behind an expression-bodied property, so no ctor is needed.
    private static readonly IToolHandler CreateProviderTool =
        (IToolHandler)RuntimeHelpers.GetUninitializedObject(typeof(CreateAiProviderHandler));

    [Fact]
    public void Every_catalog_type_can_be_offered_to_the_model()
    {
        var enumValues = CreateProviderTool.ParametersSchema
            .GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(LlmProviderCatalog.Types.OrderBy(t => t), enumValues.OrderBy(t => t));
    }

    // Everything except `custom` answers with an endpoint of its own, which is what
    // lets a provider row leave Base URL blank.
    [Fact]
    public void Every_type_but_custom_has_a_default_endpoint()
    {
        foreach (var type in LlmProviderCatalog.Types)
        {
            var url = LlmProviderCatalog.DefaultBaseUrl(type);
            if (type == LlmProviderCatalog.Custom)
            {
                Assert.Null(url);
                Assert.True(LlmProviderCatalog.RequiresBaseUrl(type));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(url), $"{type} has no default base URL");
                Assert.False(LlmProviderCatalog.RequiresBaseUrl(type));
            }
        }
    }

    [Theory]
    [InlineData("openai", true)]
    [InlineData("gemini", true)]
    [InlineData("custom", true)]
    [InlineData("OpenAI", false)] // callers lower-case before validating
    [InlineData("bedrock", false)]
    [InlineData(null, false)]
    public void IsSupported_matches_the_catalog(string? type, bool expected)
        => Assert.Equal(expected, LlmProviderCatalog.IsSupported(type));
}
