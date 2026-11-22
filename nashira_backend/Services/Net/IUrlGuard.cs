namespace nashira_backend.Services.Net;

// SSRF guard. EnsureSafe throws for disallowed targets. allowPrivate lets
// RFC-1918 ranges through (on-prem integrations legitimately live there) but
// loopback, IPv6 local ranges and link-local/metadata are never bypassed by that flag.
public interface IUrlGuard
{
    void EnsureSafe(string url, bool allowPrivate = false);
}
