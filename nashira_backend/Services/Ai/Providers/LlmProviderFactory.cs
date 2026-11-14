using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Ai;
using nashira_backend.Data.Models;
using nashira_backend.Services.Security;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Providers;

// A resolved provider together with the identity of the row it came from. The
// chat needs more than the client and the model name: it persists the choice on
// the conversation and tells the browser which provider actually answered, and
// neither is recoverable from IStreamingToolCallingLlmProvider.
//
// GuidanceAfterQuestion is the provider's own choice of where the guidance message
// goes (Config.guidance_after_question), or null when the row leaves it to the
// server-wide AiChat setting; see AiChatOptions.ScopedPromptAfterUserMessage.
public sealed record ResolvedLlm(
    IStreamingToolCallingLlmProvider Provider,
    Guid ProviderId,
    string ProviderName,
    string ProviderType,
    string Model,
    bool? GuidanceAfterQuestion = null);

// Resolves a streaming provider from a tenant's AIProvider row: decrypts the API
// key, picks the implementation by Type, injects the base URL. Scoped (uses the
// current tenant + DbContext).
public class LlmProviderFactory
{
    // Config key for a provider's own guidance placement: true sends the guidance
    // message behind the user's message, false keeps it ahead, absent defers to the
    // server-wide AiChat:ScopedPromptAfterUserMessage.
    public const string GuidanceAfterQuestionConfigKey = "guidance_after_question";

    // Config key for a provider's own quiet-stream limit, in seconds: how long its
    // stream may send nothing once the model has started its reply. 0 turns it off
    // for this provider; absent defers to the server-wide AiChat:StreamIdleSeconds,
    // which is off unless set. See StreamLineReader for when it is safe to set.
    public const string StreamIdleSecondsConfigKey = "stream_idle_seconds";

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ISecretProtector _crypto;
    private readonly ILoggerFactory _loggerFactory;

    public LlmProviderFactory(
        AppDbContext db, ICurrentUser user, IHttpClientFactory httpFactory,
        ISecretProtector crypto, ILoggerFactory loggerFactory)
    {
        _db = db;
        _user = user;
        _httpFactory = httpFactory;
        _crypto = crypto;
        _loggerFactory = loggerFactory;
    }

    // Resolve a specific provider by id (model falls back to the provider default).
    //
    // `Enabled` is part of the lookup, not just of Build: chat requests carry a
    // provider id chosen by the client, and a conversation can hold the id of a
    // provider an admin has since switched off. Both must read as "that provider
    // is not available", not as a disabled row quietly being used anyway.
    public async Task<ResolvedLlm> ResolveAsync(
        Guid providerId, string? modelOverride, CancellationToken ct)
    {
        var row = await _db.AIProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.AIProviderId == providerId && p.IsActive && p.Enabled, ct);
        if (row is not null) return Resolve(row, modelOverride);

        // The message reaches the person in the chat, who cannot act on a GUID.
        // A conversation pinned to a provider an admin has since switched off is
        // the common case, and it is fixed by naming the provider and saying so —
        // the second query only ever runs on this path.
        var name = await _db.AIProviders.AsNoTracking()
            .Where(p => p.AIProviderId == providerId && p.IsActive)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);
        throw new InvalidOperationException(name is null
            ? "That AI provider no longer exists. Pick another model to continue."
            : $"The AI provider '{name}' is disabled. Enable it, or pick another model to continue.");
    }

    // Resolve the tenant's default: the oldest enabled provider. Used for a
    // conversation that has never named one.
    public async Task<ResolvedLlm> ResolveDefaultAsync(
        string? modelOverride, CancellationToken ct)
    {
        var row = await _db.AIProviders.AsNoTracking()
            .Where(p => p.IsActive && p.Enabled)
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("no enabled AI provider configured for this tenant");
        return Resolve(row, modelOverride);
    }

    // A string value from a provider's Config, or null when absent or the wrong
    // shape. Config is operator-editable free-form JSON, so a wrong type here is a
    // typo to ignore, not a reason to refuse to build the provider.
    private static string? ConfigString(System.Text.Json.JsonElement config, string key)
    {
        if (config.ValueKind != System.Text.Json.JsonValueKind.Object
            || !config.TryGetProperty(key, out var node)
            || node.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;
        var value = node.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // A true/false value from a provider's Config, or null when absent or not a
    // boolean (the same typo rule as ConfigString).
    internal static bool? ConfigBool(System.Text.Json.JsonElement config, string key)
    {
        if (config.ValueKind != System.Text.Json.JsonValueKind.Object
            || !config.TryGetProperty(key, out var node))
            return null;
        return node.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => null,
        };
    }

    // A provider's own quiet-stream limit from its Config, or null when absent or not
    // a number of seconds from 0 up (the same typo rule as ConfigString), which
    // leaves the server-wide setting in charge.
    internal static TimeSpan? ConfigStreamIdle(System.Text.Json.JsonElement config)
    {
        if (config.ValueKind != System.Text.Json.JsonValueKind.Object
            || !config.TryGetProperty(StreamIdleSecondsConfigKey, out var node)
            || node.ValueKind != System.Text.Json.JsonValueKind.Number
            || !node.TryGetDouble(out var seconds)
            || !(seconds >= 0) || seconds > MaxStreamIdleSeconds)
            return null;
        return TimeSpan.FromSeconds(seconds);
    }

    // A day. Nothing longer is a limit on a chat turn, and it keeps the value inside
    // what a timer accepts.
    internal const double MaxStreamIdleSeconds = 86_400;

    private ResolvedLlm Resolve(AIProvider row, string? modelOverride) => new(
        Build(row),
        row.AIProviderId,
        row.Name,
        row.Type,
        string.IsNullOrWhiteSpace(modelOverride) ? row.DefaultModel : modelOverride.Trim(),
        ConfigBool(row.Config, GuidanceAfterQuestionConfigKey));

    private IStreamingToolCallingLlmProvider Build(AIProvider provider)
    {
        if (!provider.Enabled)
            throw new InvalidOperationException($"AI provider '{provider.Name}' is disabled");

        var apiKey = _crypto.Decrypt(provider.EncryptedApiKey) ?? string.Empty;
        var http = _httpFactory.CreateClient("llm");
        var type = provider.Type.ToLowerInvariant();
        // Config.model_limits, when the row has one; null keeps each provider's own
        // default. Read here, once per resolve, so the providers stay config-free.
        var limits = ModelLimits.FromConfig(provider.Config);
        // Null leaves each provider on the server-wide default.
        var streamIdle = ConfigStreamIdle(provider.Config);

        // A blank base URL means "the vendor's own endpoint"; a filled one is a
        // proxy, a self-hosted deployment, or — for `custom` — the only address
        // there is.
        var baseUrl = string.IsNullOrWhiteSpace(provider.BaseURL)
            ? LlmProviderCatalog.DefaultBaseUrl(type)
            : provider.BaseURL;

        if (LlmProviderCatalog.RequiresBaseUrl(type) && string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                $"AI provider '{provider.Name}' is a custom endpoint and has no base URL configured");

        return type switch
        {
            LlmProviderCatalog.Anthropic => new AnthropicProvider(
                http, apiKey, baseUrl, _loggerFactory.CreateLogger<AnthropicProvider>(), limits,
                workspaceId: ConfigString(provider.Config, AnthropicProvider.WorkspaceIdConfigKey),
                streamIdle: streamIdle),
            LlmProviderCatalog.Ollama => new OllamaProvider(
                http, apiKey, baseUrl, _loggerFactory.CreateLogger<OllamaProvider>(), limits, streamIdle),

            // Gemini has its own wire format (`contents`, `systemInstruction`, a
            // narrower schema dialect), and its own provider rather than the
            // OpenAI-compatibility endpoint — see GeminiProvider.
            LlmProviderCatalog.Gemini => new GeminiProvider(
                http, apiKey, baseUrl, _loggerFactory.CreateLogger<GeminiProvider>(), limits, streamIdle),

            // DeepSeek, Kimi and whatever a tenant runs behind `custom` are
            // OpenAI-compatible: same body, same SSE, different host. The type is
            // passed through so the logs name the provider that was actually called.
            LlmProviderCatalog.OpenAi or LlmProviderCatalog.DeepSeek
                or LlmProviderCatalog.Kimi or LlmProviderCatalog.Custom => new OpenAiProvider(
                    http, apiKey, baseUrl, _loggerFactory.CreateLogger<OpenAiProvider>(),
                    providerType: type, limits: limits, streamIdle: streamIdle),

            _ => throw new InvalidOperationException($"unsupported provider type: {provider.Type}"),
        };
    }
}
