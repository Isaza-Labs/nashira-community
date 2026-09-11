using System.Net;
using System.Text;
using System.Text.Json;

namespace nashira_backend.Tests;

// Fixtures shared by the provider tests, with the same shape as flow-weaver's so
// the two suites can be read side by side (the Gemini suite was ported from
// there wholesale).

internal static class TestJson
{
    // Parse a JSON literal into a JsonElement (deep-cloned so it survives the
    // owning JsonDocument being disposed).
    public static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

// Records every request and answers from a responder, so a fixture can assert on
// the URL, the headers and the body actually sent — and can script a sequence
// (a 429 then a 200) to exercise retry.
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    // Convenience: always answer with the given status + JSON/text body.
    public FakeHttpMessageHandler(HttpStatusCode status, string body = "", string contentType = "application/json")
        : this(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        })
    { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        return _responder(request);
    }
}
