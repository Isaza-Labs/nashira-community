using System.Text.Json;

namespace nashira_backend.Services.Ai.Providers;

// POST-with-retry, shared by the HTTP providers. Every remote here fails the same
// way — a transient 5xx, a 429 with a Retry-After, a socket that dies mid-connect —
// so the backoff lives once and each caller passes the `tag` that names it in the
// logs. The request is built per attempt because an HttpRequestMessage cannot be
// sent twice.
internal static class LlmHttp
{
    // Joins a configured base URL with a provider's endpoint path without repeating
    // a segment the base already ends with.
    //
    // This exists because of what the vendors publish. DeepSeek documents its base
    // URL as https://api.deepseek.com/v1, Gemini's as
    // https://generativelanguage.googleapis.com/v1beta/openai, Moonshot's with /v1
    // — the OpenAI SDK expects the version in the base — so pasting the documented
    // value into the Base URL field is the normal case, not a mistake. Concatenating
    // then produces /v1/v1/chat/completions: a 404 that reads like a bad key or a
    // dead endpoint, and the field looks correct because it is what the vendor's own
    // page says.
    //
    // The longest overlap wins, so /v1beta/openai is matched before /v1.
    public static string CombineUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = "/" + path.Trim('/');
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var take = segments.Length - 1; take >= 1; take--)
        {
            var prefix = "/" + string.Join('/', segments.Take(take));
            if (b.EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return b + p[prefix.Length..];
        }
        return b + p;
    }

    // Retry only transient upstream codes; 4xx (bad request / auth / model
    // not found) are fatal on the first try. 408 included because gateways
    // occasionally surface timeouts as 408 instead of 504.
    private static readonly HashSet<int> RetryableStatusCodes = [408, 429, 500, 502, 503, 504];

    // Backoff 0.5s, 1s, 2s (worst-case ~3.5s) with jitter to stagger parallel retries.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    public static async Task<HttpResponseMessage> PostAsync(
        HttpClient http, Func<HttpRequestMessage> buildRequest, string tag, ILogger? logger, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; ; attempt++)
        {
            response?.Dispose();

            using var req = buildRequest();

            try
            {
                response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                logger?.LogWarning("{Tag}.post.network_retry attempt={Attempt} error={Error}", tag, attempt + 1, ex.Message);
                await DelayWithJitterAsync(RetryDelays[attempt], ct);
                continue;
            }

            if ((int)response.StatusCode < 400)
                return response;

            var status = (int)response.StatusCode;
            if (!RetryableStatusCodes.Contains(status) || attempt >= RetryDelays.Length)
            {
                // EnsureSuccessStatusCode throws away the body, and the body is the
                // only part that says what was wrong: every provider answers a 4xx
                // with a specific reason ("max_tokens: 64000 > 32000", "tools.7.name:
                // invalid"), and the operator saw "Response status code does not
                // indicate success: 400 (Bad Request)" instead — a message that
                // cannot be acted on and sends people to check their API key.
                throw await ProviderErrorAsync(response, tag, status, logger, ct);
            }

            var delay = RetryDelays[attempt];
            if (response.Headers.RetryAfter?.Delta is { } ra && ra > TimeSpan.Zero)
                delay = ra > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : ra;

            logger?.LogWarning("{Tag}.post.retry attempt={Attempt} status={Status} delay_ms={Delay}",
                tag, attempt + 1, status, (int)delay.TotalMilliseconds);
            await DelayWithJitterAsync(delay, ct);
        }
    }

    // How much of an error body is worth keeping. Providers answer with a short JSON
    // object; anything longer is an HTML error page from something in between.
    private const int MaxErrorBodyChars = 2000;

    // Turns a provider's 4xx into an exception that names the reason.
    //
    // The shapes differ but all four put the text in the same two places:
    //   Anthropic  {"type":"error","error":{"type":"invalid_request_error","message":"…"}}
    //   OpenAI     {"error":{"message":"…","type":"…","code":"…"}}
    //   Gemini     {"error":{"code":400,"message":"…","status":"INVALID_ARGUMENT"}}
    //   Ollama     {"error":"…"}
    private static async Task<HttpRequestException> ProviderErrorAsync(
        HttpResponseMessage response, string tag, int status, ILogger? logger, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            // Never let reading the diagnosis replace the failure with a different one.
            logger?.LogWarning(ex, "{Tag}.post.error_body_unreadable status={Status}", tag, status);
            body = string.Empty;
        }

        if (body.Length > MaxErrorBodyChars) body = body[..MaxErrorBodyChars] + "…";
        var detail = ExtractMessage(body) ?? Collapse(body);

        // The full body at Warning: the extracted message is what the user sees, and
        // the rest (error type, request id) is what a support conversation needs.
        logger?.LogWarning("{Tag}.post.failed status={Status} body={Body}", tag, status, body);

        return new HttpRequestException(
            string.IsNullOrWhiteSpace(detail)
                ? $"{tag} returned HTTP {status} with no error body."
                : $"{tag} returned HTTP {status}: {detail}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("error", out var error)) return null;

            // Ollama's `error` is the message itself; the rest nest it one deeper.
            if (error.ValueKind == JsonValueKind.String) return Collapse(error.GetString());
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = Collapse(message.GetString());
                // Anthropic and OpenAI both carry a type worth keeping: it separates
                // "your request is malformed" from "your credit ran out".
                if (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                    return $"{text} ({type.GetString()})";
                return text;
            }
        }
        catch (JsonException)
        {
            // Not JSON: an HTML error page from a proxy, most likely. The collapsed
            // body is still better than the status line on its own.
        }
        return null;
    }

    private static string? Collapse(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static Task DelayWithJitterAsync(TimeSpan baseDelay, CancellationToken ct)
    {
        var jitterMs = (int)(baseDelay.TotalMilliseconds * 0.4);
        var offset = Random.Shared.Next(-jitterMs, jitterMs + 1);
        var final = baseDelay + TimeSpan.FromMilliseconds(offset);
        if (final < TimeSpan.Zero) final = TimeSpan.Zero;
        return Task.Delay(final, ct);
    }
}
