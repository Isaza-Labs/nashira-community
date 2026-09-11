using System.Net;
using System.Net.Http.Headers;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// An identity-linked API key belongs to a person who may have access to several
// workspaces, so the key alone does not say which one a request acts in. Anthropic
// answers 400 "anthropic-workspace-id is required when authenticating with an
// identity-linked API key" until the header names one, and no amount of looking at
// the prompt, the tools or the model explains it.
//
// A plain workspace-scoped key carries its own workspace, so the header must stay
// optional: sending an empty one would break every deployment that has no id to
// send.
public class AnthropicWorkspaceHeaderTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestHeaders? Headers { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Headers = request.Headers;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[],"stop_reason":"end_turn","usage":{}}"""),
            });
        }
    }

    private static async Task<HttpRequestHeaders> SendAsync(string? workspaceId)
    {
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(
            new HttpClient(handler), "sk-test", workspaceId: workspaceId);
        await provider.ChatAsync([new LlmMessage { Role = "user", Content = "hi" }],
            "claude-sonnet-5", 0.2, CancellationToken.None);
        Assert.NotNull(handler.Headers);
        return handler.Headers!;
    }

    [Fact]
    public async Task The_workspace_id_is_sent_when_configured()
    {
        var headers = await SendAsync("wrkspc_01ABC");

        Assert.True(headers.TryGetValues("anthropic-workspace-id", out var values),
            "an identity-linked key is rejected until the request names its workspace");
        Assert.Equal("wrkspc_01ABC", Assert.Single(values));
    }

    [Fact]
    public async Task The_header_is_absent_when_no_workspace_is_configured()
    {
        var headers = await SendAsync(null);

        Assert.False(headers.Contains("anthropic-workspace-id"),
            "a workspace-scoped key carries its own workspace; an empty header would be a 400");
    }

    // Config is hand-edited JSON, so blank is what a half-filled field looks like.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_workspace_id_is_treated_as_unset(string blank)
    {
        var headers = await SendAsync(blank);

        Assert.False(headers.Contains("anthropic-workspace-id"));
    }

    [Fact]
    public async Task Surrounding_whitespace_is_trimmed()
    {
        var headers = await SendAsync("  wrkspc_01ABC\t");

        Assert.Equal("wrkspc_01ABC", Assert.Single(headers.GetValues("anthropic-workspace-id")));
    }
}
