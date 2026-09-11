namespace nashira_backend.Configuration;

// Bound to the "Jwt" section of appsettings. Jwt:Key is the signing secret —
// must be 32+ chars of high-entropy random in production. In dev the value in
// appsettings.json is a placeholder and should be overridden via the Jwt__Key
// environment variable for any non-local deployment.
public class JwtOptions
{
    public const string SectionName = "Jwt";

    // Insecure defaults shipped in appsettings.json / docker-compose.yml. Boot
    // fails if any of these is loaded outside the Development environment.
    public static readonly IReadOnlySet<string> KnownDefaultKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "dev-only-key-replace-in-prod-with-32plus-random-bytes-via-env",
    };

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    public bool IsKnownDefaultKey() => !string.IsNullOrEmpty(Key) && KnownDefaultKeys.Contains(Key);

    // The rule lives in one place, shared between Program.cs and tests. Throws when
    // the section is missing, the key is too short, or a repo placeholder is used
    // outside Development.
    public static void ValidateForBoot(JwtOptions? jwt, string environmentName)
    {
        if (jwt is null)
            throw new InvalidOperationException("Jwt section missing from configuration.");
        if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Key must be 32+ chars. Set Jwt__Key env var in production.");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.Ordinal);
        if (!isDevelopment && jwt.IsKnownDefaultKey())
            throw new InvalidOperationException(
                "Jwt:Key is set to a known development default. Refusing to start outside Development. " +
                "Generate a per-instance key (e.g. `openssl rand -base64 48`) and inject via Jwt__Key.");
    }
}
