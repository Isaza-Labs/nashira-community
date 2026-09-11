using System.Text.Json;
using nashira_backend.Services.Ai.RestExecutor;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Net;
using nashira_backend.Services.Notifications;

namespace nashira_backend.Tests.Conformance;

// What a probe observed the handler ASK THE OUTSIDE WORLD TO DO, as opposed to what the
// step returned.
//
// The distinction is the whole reason these harnesses exist. Every silent-drop defect this
// contract is meant to catch has the same signature: the output is exactly what a correct
// run would produce, and the thing that actually happened is wrong. A threaded Slack reply
// posted at the root of the channel returns `ok: true` with the same fields. A catalogued
// REST call resolved against the wrong specification returns a perfectly well-formed 200. A
// play handed the wrong variables finishes and reports success.
//
// So `output` alone can never fail those vectors. `sent` can.
//
// `sent` is present only when the handler reached outward. A pure function like `transform`
// records nothing, which is what keeps the two existing `exact` probe vectors passing: an
// unconditional `sent: null` would have changed their shape.
internal sealed record ProbeResult(JsonElement Output, JsonElement? Sent = null, string? Logs = null);

// ── Slack ────────────────────────────────────────────────────────────────────

// Records the webhook dispatcher call instead of posting.
//
// This is the path taken when NO Slack messaging channel is configured. It carries `blocks`
// and cannot carry a thread, so a probe on this path should see a refusal rather than a
// recorded call whenever the node declared `thread_ts` — which is what the vector asserts.
internal sealed class RecordingNotificationDispatcher : INotificationDispatcher
{
    public Guid? ChannelId { get; private set; }
    public string? Text { get; private set; }
    public string? BlocksJson { get; private set; }
    public bool Called { get; private set; }

    public Task<NotificationResult> SendAsync(
        Guid channelId, string text, Guid? workflowRunId, CancellationToken ct, string? blocksJson = null)
    {
        Called = true;
        ChannelId = channelId;
        Text = text;
        BlocksJson = blocksJson;
        return Task.FromResult(new NotificationResult(true, 200, null, 1, 0));
    }

    public JsonElement Sent(JsonElement payload) => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
    {
        ["called"] = Called,
        ["text"] = Text,
        ["blocks"] = BlocksJson is null ? null : JsonSerializer.Deserialize<JsonElement>(BlocksJson),
        // Read off the recorded call, never off the payload. This transport has no thread
        // parameter, so it is null however the node was written — and a node that declared
        // one never reaches here at all, because the handler refuses first.
        ["thread_ts"] = null,
    });
}

// ── REST ─────────────────────────────────────────────────────────────────────

// Records which operation the catalogued path resolved, and under which source.
//
// `source` is what makes a same-id collision visible at all: nothing in the step's output
// says which upstream answered, so a node resolved against the wrong specification returns a
// perfectly well-formed 200 with that upstream's credentials already spent.
internal sealed class RecordingRestExecutor : IRestOperationExecutor
{
    public string? OperationId { get; private set; }
    public string? Source { get; private set; }
    public JsonElement PathParams { get; private set; }
    public JsonElement QueryParams { get; private set; }

    public Task<RestExecutionResult> ExecuteAsync(
        string operationId, JsonElement pathParams, JsonElement queryParams, JsonElement body,
        CancellationToken ct, string? source = null)
    {
        OperationId = operationId;
        Source = source;
        PathParams = pathParams;
        QueryParams = queryParams;
        return Task.FromResult(new RestExecutionResult
        {
            StatusCode = 200,
            Body = JsonSerializer.SerializeToElement(new { }),
            Headers = new Dictionary<string, string>(),
        });
    }

    public JsonElement Sent() => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
    {
        ["operation_id"] = OperationId,
        ["source"] = Source,
        ["query_params"] = QueryParams.ValueKind == JsonValueKind.Undefined ? null : (object?)QueryParams,
    });
}

// The raw-URL path is not probed: it opens a socket. These exist so the handler can be
// constructed, and they throw rather than no-op, so a probe that wandered onto that path
// fails loudly instead of reporting a result nobody produced.
internal sealed class UnusedUrlGuard : IUrlGuard
{
    public void EnsureSafe(string url, bool allowPrivate = false) =>
        throw new InvalidOperationException(
            "the rest_call probe reached the RAW url path, which is not harnessed — write the "
            + "vector with `operation_id` (the catalogued form) or add a harness for the raw one.");
}

internal sealed class UnusedSecretResolver : ISecretResolver
{
    public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
        throw new InvalidOperationException("the rest_call probe does not resolve secrets");

    public Task<string> SubstituteAsync(string template, CancellationToken ct) =>
        throw new InvalidOperationException("the rest_call probe does not resolve secrets");

    public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct) =>
        throw new InvalidOperationException("the rest_call probe does not resolve secrets");
}

internal sealed class UnusedHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        throw new InvalidOperationException("the rest_call probe does not open sockets");
}

// ── Slack, bot transport ─────────────────────────────────────────────────────

// Records the chat.postMessage call. This is the transport that CAN thread, so it is the
// one a vector about threading has to reach: the webhook path refuses a thread rather than
// flattening it, which is correct behaviour and not the behaviour the contract asserts.
internal sealed class RecordingSlackProvider : nashira_backend.Services.Messaging.IMessagingProvider
{
    private string? _threadId;
    private string? _text;
    private string? _blocksJson;
    private bool _called;

    public string Provider => nashira_backend.Data.Models.MessagingChannel.ProviderSlack;

    public Task<nashira_backend.Services.Messaging.WebhookVerifyResult> VerifyAsync(
        nashira_backend.Data.Models.MessagingChannel channel,
        nashira_backend.Services.Messaging.MessagingHttpRequest request,
        string? decryptedSigningSecret, CancellationToken ct) => throw new NotSupportedException();

    public nashira_backend.Services.Messaging.InboundMessage? ParseInbound(
        nashira_backend.Data.Models.MessagingChannel channel,
        nashira_backend.Services.Messaging.MessagingHttpRequest request) => throw new NotSupportedException();

    public Task SendAsync(
        nashira_backend.Data.Models.MessagingChannel channel, string? decryptedBotToken,
        nashira_backend.Services.Messaging.OutboundMessage message, CancellationToken ct)
    {
        _called = true;
        _threadId = message.ExternalThreadId;
        _text = message.Text;
        _blocksJson = message.BlocksJson;
        return Task.CompletedTask;
    }

    // The provider addresses a destination as "channel" or "channel:thread_ts". Splitting it
    // back out is what lets a vector assert the THREAD rather than the opaque address —
    // asserting the address would pass just as well for a message posted at the channel root
    // by a handler that had dropped the thread and rebuilt the string.
    public JsonElement? Sent()
    {
        if (!_called) return null;
        var sep = _threadId?.LastIndexOf(':') ?? -1;
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["called"] = true,
            ["text"] = _text,
            ["channel"] = sep > 0 ? _threadId![..sep] : _threadId,
            ["thread_ts"] = sep > 0 ? _threadId![(sep + 1)..] : null,
            ["blocks"] = _blocksJson is null ? null : JsonSerializer.Deserialize<JsonElement>(_blocksJson),
        });
    }
}

internal sealed class OneProviderResolver(nashira_backend.Services.Messaging.IMessagingProvider p)
    : nashira_backend.Services.Messaging.IMessagingProviderResolver
{
    public nashira_backend.Services.Messaging.IMessagingProvider? Resolve(string? provider) => p;
}

// ── email ────────────────────────────────────────────────────────────────────

// Records the message handed to a NAMED channel, which is the path that can set a sender.
internal sealed class RecordingEmailSender : nashira_backend.Services.Email.IEmailChannelSender
{
    private nashira_backend.Services.Email.EmailChannelMessage? _message;

    public Task SendAsync(
        nashira_backend.Data.Models.EmailChannel channel, IReadOnlyList<string> to,
        string subject, string body, CancellationToken ct) =>
        SendAsync(channel, new nashira_backend.Services.Email.EmailChannelMessage
        {
            To = to, Subject = subject, TextBody = body,
        }, ct);

    public Task SendAsync(
        nashira_backend.Data.Models.EmailChannel channel,
        nashira_backend.Services.Email.EmailChannelMessage message, CancellationToken ct)
    {
        _message = message;
        return Task.CompletedTask;
    }

    public JsonElement? Sent() => _message is null ? null : JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>
        {
            ["called"] = true,
            ["from_address"] = _message.FromAddress,
            ["from_name"] = _message.FromName,
            ["subject"] = _message.Subject,
        });
}

// The deployment relay. It has no sender parameter at all, which is the capability
// difference the contract's sender rule turns on — a vector reaching here should be
// asserting a refusal, not a delivery.
internal sealed class RecordingDefaultRelay : nashira_backend.Services.Email.IEmailService
{
    private bool _called;

    public bool IsConfigured => true;

    public Task SendAsync(
        IReadOnlyList<string> to, string subject, string body, bool isHtml,
        IReadOnlyList<string>? cc, nashira_backend.Services.Email.EmailAttachment? attachment,
        CancellationToken ct)
    {
        _called = true;
        return Task.CompletedTask;
    }

    public JsonElement? Sent() => _called
        ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["called"] = true,
            // The relay cannot set one. Present and null so a vector can assert that a
            // delivery through this transport carried no declared sender.
            ["from_address"] = null,
        })
        : null;
}
