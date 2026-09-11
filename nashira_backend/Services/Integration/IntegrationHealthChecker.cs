using System.Diagnostics;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Net;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Services.Integration;

public sealed record IntegrationHealth(string Status, int? StatusCode, int ElapsedMs, string? Error);

public interface IIntegrationHealthChecker
{
    Task<IntegrationHealth> CheckAsync(IntegrationEntity integration, CancellationToken ct);
}

// Probes an integration with a real, authenticated GET so the result answers the
// question an operator actually has: "will a call from Nashira work right now?"
//
// An unauthenticated ping would go green on a 401, which is the single most common
// way an integration is broken, so credentials are applied exactly as they are for
// a normal call.
public sealed class IntegrationHealthChecker : IIntegrationHealthChecker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IIntegrationAuthApplier _auth;
    private readonly ISecretResolver _secrets;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IntegrationHealthChecker> _logger;

    public IntegrationHealthChecker(
        IIntegrationAuthApplier auth, ISecretResolver secrets, IUrlGuard urlGuard,
        IHttpClientFactory httpFactory, ILogger<IntegrationHealthChecker> logger)
    {
        _auth = auth;
        _secrets = secrets;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    // base_url + health_check_path, except that an ABSOLUTE health_check_path is used
    // as-is. Pasting the full probe URL into that field is the single most common way
    // to configure it, and concatenating it onto the base URL produced a nonsense
    // address whose 404 read as "the integration is down" — the fault was three fields
    // away from where the operator was told to look.
    public static string BuildProbeUrl(string baseUrl, string? healthCheckPath)
        => BuildProbeUrl(baseUrl, healthCheckPath, null);

    // `type` supplies the fallback path for systems we know. Probing the base URL is
    // worse than useless against anything with a web UI: NetBox's root answers 302 to
    // an anonymous caller, redirecting to its login page, and 3xx counts as healthy
    // below. So an integration whose every API call was refused still reported
    // healthy — which is exactly how a wrong auth scheme stayed hidden.
    public static string BuildProbeUrl(string baseUrl, string? healthCheckPath, string? type)
    {
        var path = (healthCheckPath ?? string.Empty).Trim();
        if (path.Length == 0) path = (IntegrationTypeProfile.DefaultHealthPath(type) ?? string.Empty).Trim();
        if (path.Length == 0) return baseUrl;

        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return path;

        return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    public async Task<IntegrationHealth> CheckAsync(IntegrationEntity integration, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var baseUrl = (await _secrets.SubstituteAsync(integration.BaseUrl ?? string.Empty, ct)).TrimEnd('/');
            if (baseUrl.Length == 0)
                return new IntegrationHealth(IntegrationEntity.StatusUnreachable, null, 0, "base_url is not configured");

            var url = BuildProbeUrl(baseUrl, integration.HealthCheckPath, integration.Type);

            _urlGuard.EnsureSafe(url, allowPrivate: integration.AllowPrivateNetwork);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            await _auth.ApplyAsync(request, integration, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var client = _httpFactory.CreateClient(
                integration.VerifySsl ? IntegrationHttpClients.Secure : IntegrationHttpClients.Insecure);

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();

            var code = (int)response.StatusCode;
            // 2xx/3xx is healthy. 401/403/404 are all "the host answered", which is a
            // distinct and actionable state from "unreachable" — the latter sends an
            // operator to check the network when the real fault is a credential or a
            // mistyped health_check_path.
            var status = code switch
            {
                >= 200 and < 400 => IntegrationEntity.StatusHealthy,
                401 or 403 or 404 => IntegrationEntity.StatusDegraded,
                _ => IntegrationEntity.StatusUnreachable,
            };
            var error = status switch
            {
                IntegrationEntity.StatusHealthy => null,
                IntegrationEntity.StatusDegraded when code == 404 =>
                    $"reachable, but the probe returned HTTP 404 for {url} — check health_check_path",
                IntegrationEntity.StatusDegraded => $"reachable but rejected the credentials (HTTP {code})",
                _ => $"HTTP {code}",
            };

            return new IntegrationHealth(status, code, (int)sw.ElapsedMilliseconds, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new IntegrationHealth(
                IntegrationEntity.StatusUnreachable, null, (int)sw.ElapsedMilliseconds,
                $"timed out after {Timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "integration.health.failed integration={Integration}", integration.Name);
            return new IntegrationHealth(
                IntegrationEntity.StatusUnreachable, null, (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
