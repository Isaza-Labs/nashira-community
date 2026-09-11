namespace nashira_backend.Services.Integration;

// What we know about the systems people actually integrate, where knowing it prevents a
// misconfiguration that presents as something else entirely.
//
// `Integration.Type` is deliberately free-form, so this is a lookup of hints rather than
// an enum: an unknown type gets no profile and behaves exactly as before.
//
// The case that produced this file: a NetBox integration configured with `bearer`. The
// token was correct and stored correctly, but NetBox's Django REST Framework only
// recognises the scheme word `Token`. An unrecognised scheme does not read to DRF as a
// bad credential — it reads as no credential, so NetBox answered "authentication
// credentials were not provided". That message describes the request rather than the
// mistake behind it, and it sent everyone looking at the spec, the credential store and
// the agent's tooling instead of at one dropdown.
public sealed record IntegrationTypeProfile(
    string Type,
    // The scheme the upstream will actually accept, as an IntegrationAuthConfig method.
    string AuthMethod,
    // A path that REQUIRES authentication, for the health probe. Probing the base URL
    // is worse than useless against a system with a web UI: NetBox's root answers 302,
    // redirecting an anonymous caller to its login page, and the checker counts
    // 2xx/3xx as healthy. So the integration reported healthy on the strength of a
    // redirect to a login form, while every API call it made was refused.
    string HealthPath,
    string AuthHint)
{
    private static readonly Dictionary<string, IntegrationTypeProfile> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["netbox"] = new(
                "netbox",
                IntegrationAuthConfig.MethodToken,
                // Cheap, authenticated, and stable across NetBox 3.x and 4.x.
                "/api/status/",
                "NetBox authenticates with `Authorization: Token <key>` (Django REST "
                + "Framework). Use the `token` method; `bearer` sends a scheme NetBox does "
                + "not recognise, and it answers as though no credential was sent at all."),

            // Here for the health path alone. ServiceNow accepts `Bearer` for an OAuth
            // access token and Basic for a username and password, so there is no scheme
            // to contradict and AuthMismatch never fires for this type — declaring
            // `bearer` is what keeps it from firing, since anything other than the
            // profile's own method would flag the OAuth setup as a mistake.
            //
            // The probe path is the part that matters. A ServiceNow instance answers
            // 200 to an anonymous GET of its root — that is the login page — so the
            // default "probe the base URL" reports healthy for an instance whose every
            // API call is refused. This is the NetBox trap above, in a system where it
            // is likelier to be hit: instances created since 2026 ship with Basic Auth
            // Restriction enforced, which refuses Basic for any user without an
            // exception, so correct credentials return 401 while the UI login works.
            ["servicenow"] = new(
                "servicenow",
                IntegrationAuthConfig.MethodBearer,
                // Cheap, authenticated, and present on every instance regardless of
                // which plugins are on. sysparm_limit keeps it to one row.
                "/api/now/table/sys_user?sysparm_limit=1",
                "ServiceNow accepts `Bearer <access token>` for OAuth 2.0 and Basic for a "
                + "username and password. Prefer OAuth: instances created since 2026 enforce "
                + "Basic Auth Restriction, which refuses Basic for any user without an explicit "
                + "exception and answers 401 as though no credential was sent."),
        };

    public static IntegrationTypeProfile? For(string? type) =>
        string.IsNullOrWhiteSpace(type) ? null : Known.GetValueOrDefault(type.Trim());

    /// The health probe path to use when the integration does not specify one.
    public static string? DefaultHealthPath(string? type) => For(type)?.HealthPath;

    /// Null when the configured method is fine, otherwise why it cannot work.
    ///
    /// Only an explicitly declared method is checked. Leaving the method empty already
    /// resolves to `token` when a token is present (IntegrationAuthConfig.ResolvedMethod),
    /// which is the right answer here — this catches the case where somebody chose
    /// otherwise on purpose.
    public static string? AuthMismatch(string? type, string? declaredMethod)
    {
        var profile = For(type);
        if (profile is null || string.IsNullOrWhiteSpace(declaredMethod)) return null;

        var method = declaredMethod.Trim().ToLowerInvariant();
        // `none` is a deliberate choice to send nothing, and api_key/basic are shapes
        // this profile has no opinion about. Only contradict a scheme we know is wrong.
        if (method is IntegrationAuthConfig.MethodNone or IntegrationAuthConfig.MethodApiKey)
            return null;
        if (string.Equals(method, profile.AuthMethod, StringComparison.OrdinalIgnoreCase))
            return null;
        if (method is not IntegrationAuthConfig.MethodBearer) return null;

        return profile.AuthHint;
    }

    // Whether an upstream refusal looks like the scheme was the problem rather than the
    // secret. DRF says "authentication credentials were not provided" when it does not
    // recognise the scheme at all — so seeing that phrase *after* we sent an
    // Authorization header is close to a diagnosis on its own.
    private static readonly string[] NotProvidedPhrases =
    [
        "credentials were not provided",
        "authentication credentials were not provided",
    ];

    public static bool LooksLikeUnrecognisedScheme(int statusCode, bool sentAuthorization, string? body)
    {
        if (statusCode is not (401 or 403)) return false;
        if (!sentAuthorization || string.IsNullOrEmpty(body)) return false;

        return NotProvidedPhrases.Any(p => body.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
