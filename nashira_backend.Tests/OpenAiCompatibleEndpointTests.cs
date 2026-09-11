using System.Net;
using System.Text;
using System.Text.Json;
using nashira_backend.Data.DTos.Ai;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// `OpenAiProvider` now serves four provider types (openai, deepseek, kimi and
// custom), which puts two things under test that did not matter while it only
// served OpenAI: the URL it builds from a tenant-supplied base, and what it does
// when an endpoint does not understand a request field. The /v1beta/openai cases
// below are Gemini's compatibility route — nothing routes there any more, but it is
// the sharpest case for the path-join rule, so it stays covered.
public class OpenAiCompatibleEndpointTests
{
    // Replies 400 to the first request and 200 to the rest, recording each URL and
    // body, so a one-shot fallback is observable.
    private sealed class SequencedHandler(string okBody, int badRequests = 0) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            if (Bodies.Count <= badRequests)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"unknown field: stream_options\"}}", Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private const string DoneSse = "data: [DONE]\n";

    private static async Task Drain(OpenAiProvider provider)
    {
        await foreach (var _ in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "m", 0.0, CancellationToken.None)) { }
    }

    // The vendors publish base URLs with the version already on them — DeepSeek's is
    // https://api.deepseek.com/v1, Google's compatibility route ends in
    // /v1beta/openai — so pasting the documented value is the normal case, and it
    // must not double the segment.
    [Theory]
    [InlineData(null, "/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com", "/v1/chat/completions", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1", "/v1/chat/completions", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.moonshot.ai/v1/", "/v1/chat/completions", "https://api.moonshot.ai/v1/chat/completions")]
    [InlineData("https://generativelanguage.googleapis.com", "/v1beta/openai/chat/completions",
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "/v1beta/openai/chat/completions",
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData("https://gw.internal/openai/v1", "/v1/chat/completions", "https://gw.internal/openai/v1/chat/completions")]
    public async Task Request_url_does_not_repeat_a_segment_the_base_url_already_has(
        string? baseUrl, string chatPath, string expected)
    {
        var handler = new SequencedHandler(DoneSse);
        using var http = new HttpClient(handler);
        await Drain(new OpenAiProvider(http, "k", baseUrl, null, "custom", chatPath));

        Assert.Equal(expected, Assert.Single(handler.Urls));
    }

    // An endpoint that rejects `stream_options` used to take the whole provider down
    // with a non-retryable 400. Now the field is dropped and the turn runs.
    [Fact]
    public async Task A_400_on_stream_options_is_retried_once_without_it()
    {
        var handler = new SequencedHandler(DoneSse, badRequests: 1);
        using var http = new HttpClient(handler);
        await Drain(new OpenAiProvider(http, "k", "https://gw.internal", null, "custom"));

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("stream_options", handler.Bodies[0]);
        Assert.DoesNotContain("stream_options", handler.Bodies[1]);
    }

    // And it is not asked for again for the rest of the turn: the same instance serves
    // every tool-calling round, so a second round must not pay the extra request.
    [Fact]
    public async Task Once_rejected_stream_options_is_not_sent_again_by_that_instance()
    {
        var handler = new SequencedHandler(DoneSse, badRequests: 1);
        using var http = new HttpClient(handler);
        var provider = new OpenAiProvider(http, "k", "https://gw.internal", null, "custom");

        await Drain(provider);
        await Drain(provider);

        Assert.Equal(3, handler.Bodies.Count); // 400 + fallback + second round
        Assert.DoesNotContain("stream_options", handler.Bodies[2]);
    }

    // A 400 with an unrelated cause costs one extra request and then surfaces, rather
    // than being swallowed into a turn that silently does nothing.
    [Fact]
    public async Task A_400_that_is_not_about_stream_options_still_fails()
    {
        var handler = new SequencedHandler(DoneSse, badRequests: 99);
        using var http = new HttpClient(handler);
        var provider = new OpenAiProvider(http, "k", "https://gw.internal", null, "custom");

        await Assert.ThrowsAsync<HttpRequestException>(async () => await Drain(provider));
    }

    // Usage still rides on stream_options where the endpoint accepts it.
    [Fact]
    public async Task Stream_options_is_requested_on_the_first_attempt()
    {
        var handler = new SequencedHandler(DoneSse);
        using var http = new HttpClient(handler);
        await Drain(new OpenAiProvider(http, "k"));

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    // The non-streaming call never sends the field, so it has nothing to fall back on.
    [Fact]
    public async Task Non_streaming_requests_do_not_send_stream_options()
    {
        var handler = new SequencedHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        using var http = new HttpClient(handler);
        await new OpenAiProvider(http, "k").ChatAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], "m", 0.0, CancellationToken.None);

        Assert.DoesNotContain("stream_options", Assert.Single(handler.Bodies));
    }

    // GPT-5 and the o-series reject any temperature but their own default with a
    // 400, and this provider used to send it on every request — so the models a
    // tenant is most likely to configure today failed outright. A model from another
    // vendor never matches those prefixes and keeps its temperature.
    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-5.5", false)]
    [InlineData("o3-mini", false)]
    [InlineData("deepseek-chat", true)]
    public async Task Temperature_is_sent_only_to_models_that_accept_it(string model, bool expected)
    {
        var handler = new SequencedHandler(DoneSse);
        using var http = new HttpClient(handler);
        await foreach (var _ in new OpenAiProvider(http, "k").ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], model, 0.4, CancellationToken.None)) { }

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(expected, body.RootElement.TryGetProperty("temperature", out _));
    }

    // OpenAI's own default output cap is the model's maximum, so nothing is sent
    // unless Config.model_limits asks for one — and the reasoning family spells the
    // field differently.
    [Theory]
    [InlineData("gpt-4o", "max_tokens")]
    [InlineData("gpt-5.5", "max_completion_tokens")]
    public async Task A_configured_output_cap_uses_the_field_the_model_family_expects(string model, string field)
    {
        var handler = new SequencedHandler(DoneSse);
        using var http = new HttpClient(handler);
        var limits = ModelLimits.FromConfig(JsonDocument.Parse(
            "{\"model_limits\":{\"context_window\":128000,\"max_output_tokens\":4096}}").RootElement);

        await foreach (var _ in new OpenAiProvider(http, "k", null, null, "openai",
            OpenAiProvider.DefaultChatPath, limits).ChatWithToolsStreamAsync(
                [new LlmMessage { Role = "user", Content = "hi" }], [], model, 0.4, CancellationToken.None)) { }

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(4096, body.RootElement.GetProperty(field).GetInt32());
    }

    [Fact]
    public async Task No_output_cap_is_sent_when_none_is_configured()
    {
        var handler = new SequencedHandler(DoneSse);
        using var http = new HttpClient(handler);
        await Drain(new OpenAiProvider(http, "k"));

        var body = Assert.Single(handler.Bodies);
        Assert.DoesNotContain("max_tokens", body);
        Assert.DoesNotContain("max_completion_tokens", body);
    }

    [Theory]
    [InlineData("https://host", "/api/chat", "https://host/api/chat")]
    [InlineData("https://host/api", "/api/chat", "https://host/api/chat")]
    [InlineData("https://host/api/", "api/chat", "https://host/api/chat")]
    [InlineData("https://host/v1beta", "/v1beta/openai/chat/completions", "https://host/v1beta/openai/chat/completions")]
    [InlineData("https://host/x", "/v1/messages", "https://host/x/v1/messages")]
    public void CombineUrl_matches_the_longest_shared_prefix(string baseUrl, string path, string expected)
        => Assert.Equal(expected, LlmHttp.CombineUrl(baseUrl, path));
}
