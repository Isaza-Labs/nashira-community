using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Security;
using nashira_backend.Services.Identity;
using AIProviderEntity = nashira_backend.Data.Models.AIProvider;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Creates an LLM provider (admin). The API key is encrypted and never returned.
// Write → single_confirm. Mirrors AIProviderController.Post.
public sealed class CreateAiProviderHandler : IToolHandler
{
    // The enum below is spelled out because the schema is a literal the model reads;
    // LlmProviderCatalog.Types is what actually decides, and the test asserts the two
    // agree.
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string"},
          "type":{"type":"string","enum":["openai","anthropic","gemini","deepseek","kimi","ollama","custom"]},
          "default_model":{"type":"string"},
          "base_url":{"type":"string"},
          "api_key":{"type":"string"},
          "enabled":{"type":"boolean","default":true}
        },"required":["name","type","default_model"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ISecretProtector _crypto;
    private readonly ICurrentUser _user;

    public CreateAiProviderHandler(AppDbContext db, ISecretProtector crypto, ICurrentUser user)
    {
        _db = db;
        _crypto = crypto;
        _user = user;
    }

    public string Name => "create_ai_provider";
    public string Description =>
        "Creates an LLM provider (admin): name, type (openai/anthropic/gemini/deepseek/kimi/ollama/custom), " +
        "default_model, optional base_url and api_key. `custom` is any OpenAI-compatible endpoint and requires " +
        "base_url; every other type defaults to its vendor endpoint. The api_key is encrypted and never returned.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("name is required");
        var type = (Str(args, "type") ?? string.Empty).Trim().ToLowerInvariant();
        if (!LlmProviderCatalog.IsSupported(type))
            return Err($"type must be one of: {string.Join(", ", LlmProviderCatalog.Types)}");
        var defaultModel = Str(args, "default_model")?.Trim();
        if (string.IsNullOrWhiteSpace(defaultModel)) return Err("default_model is required");
        var baseUrl = Str(args, "base_url");
        if (LlmProviderCatalog.RequiresBaseUrl(type) && string.IsNullOrWhiteSpace(baseUrl))
            return Err("base_url is required for a custom provider");

        if (await _db.AIProviders.AnyAsync(p => p.Name == name && p.IsActive, ct))
            return Err("a provider with this name already exists");

        var now = DateTime.UtcNow;
        var row = new AIProviderEntity
        {
            AIProviderId = Guid.NewGuid(),
            Name = name,
            Type = type,
            BaseURL = baseUrl,
            DefaultModel = defaultModel,
            EncryptedApiKey = _crypto.Encrypt(Str(args, "api_key")),
            Enabled = !(args.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AIProviders.Add(row);
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            ai_provider_id = row.AIProviderId,
            name = row.Name,
            type = row.Type,
            has_api_key = row.EncryptedApiKey is { Length: > 0 },
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
