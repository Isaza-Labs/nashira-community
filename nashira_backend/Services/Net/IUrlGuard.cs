namespace nashira_backend.Services.Net;

// SSRF guard. EnsureSafe throws for disallowed targets. allowPrivate lets
// RFC-1918 ranges through (on-prem integrations legitimately live there) but
// loopback, IPv6 local ranges and link-local/metadata are never bypassed by that flag.
public interface IUrlGuard
{
    void EnsureSafe(string url, bool allowPrivate = false);

    // The same decision for an address a remote server supplied (a redirect target): true when it
    // would pass, with nothing logged and nothing thrown that repeats the address. An implementation
    // that does not log can rely on this default.
    bool IsAllowedQuietly(string url, bool allowPrivate = false)
    {
        try
        {
            EnsureSafe(url, allowPrivate);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
