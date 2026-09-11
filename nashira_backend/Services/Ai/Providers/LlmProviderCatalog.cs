namespace nashira_backend.Services.Ai.Providers;

// The provider types a tenant can configure, in one place. The list used to be
// copied into the controller, the create_ai_provider tool and the factory, so a
// new type meant finding all three — and the two that validate would happily
// accept a type the third could not build.
//
// Most of these are OpenAI-compatible endpoints and differ only by URL; the
// entries that need their own wire format (Anthropic, Ollama) say so by having no
// default OpenAI path. "custom" is the escape hatch: any OpenAI-compatible
// endpoint, which is why it is the one type whose base URL is required.
public static class LlmProviderCatalog
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string DeepSeek = "deepseek";
    public const string Kimi = "kimi";
    public const string Ollama = "ollama";
    public const string Custom = "custom";

    public static readonly string[] Types =
        [OpenAi, Anthropic, Gemini, DeepSeek, Kimi, Ollama, Custom];

    public static bool IsSupported(string? type)
        => type is not null && Types.Contains(type);

    // "custom" points at an endpoint only the tenant knows. Everything else has a
    // documented default, so a blank base URL is the normal case there.
    public static bool RequiresBaseUrl(string? type)
        => string.Equals(type, Custom, StringComparison.OrdinalIgnoreCase);

    // The endpoint used when a provider row leaves BaseURL blank. Null for custom:
    // there is nothing to fall back to, which is what RequiresBaseUrl enforces at
    // the edge.
    public static string? DefaultBaseUrl(string type) => type switch
    {
        OpenAi => "https://api.openai.com",
        Anthropic => "https://api.anthropic.com",
        Gemini => "https://generativelanguage.googleapis.com",
        DeepSeek => "https://api.deepseek.com",
        Kimi => "https://api.moonshot.ai",
        Ollama => "http://localhost:11434",
        _ => null,
    };
}
