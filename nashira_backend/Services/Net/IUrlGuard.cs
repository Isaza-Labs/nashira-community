namespace nashira_backend.Services.Net;

// SSRF guard. EnsureSafe throws for disallowed targets. allowPrivate lets
// RFC-1918 ranges through (on-prem integrations legitimately live there) but
// loopback + link-local/metadata (169.254.169.254) are NEVER bypassed.
public interface IUrlGuard
{
    void EnsureSafe(string url, bool allowPrivate = false);
}
