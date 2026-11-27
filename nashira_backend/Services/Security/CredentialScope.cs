using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Security;

// The hosts a stored credential may be sent to, as an Admin defined them.
//
// A scope is a list of entries, each one of:
//   an IP address        10.0.0.9, fd00::1
//   a CIDR network       10.0.0.0/24, fd00::/8   (stored as its network address)
//   an exact hostname    core-sw.lab.example     (case-insensitive, no wildcards)
// Stored canonical: entries joined by '\n', in the order first written, de-duplicated.
// Null (or blank) is "unscoped": the credential behaves as it did before scopes.
//
// Matching never resolves names. A device addressed by IP is matched only against IP
// and CIDR entries; a device addressed by name only against hostname entries. That is
// deliberately literal: the question is "may this secret be sent to the address the
// device row holds", and DNS answering it would put the decision in someone else's
// hands.
//
// A scope that covers everything (0.0.0.0/0, ::/0, any /0) is refused: it would make a
// credential "scoped", and so assignable by Operators, while restricting nothing.
public static partial class CredentialScope
{
    public const string OutOfScopeCode = "credential_out_of_scope";

    // RFC 1123 labels; underscore tolerated because internal zones use it.
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?)(\.[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?)*$")]
    private static partial Regex HostnamePattern();

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,2})(\.(0|[1-9][0-9]{0,2})){3}$")]
    private static partial Regex StrictIpv4Pattern();

    [GeneratedRegex(@"^[0-9.]+$")]
    private static partial Regex DigitsAndDots();

    private static readonly char[] Separators = [',', '\n', '\r', ' ', '\t', ';'];

    /// <summary>Thrown by <see cref="Normalize"/>; the message names the bad entry.</summary>
    public sealed class ScopeFormatException(string message) : Exception(message);

    /// <summary>
    /// Parses what an Admin typed (comma, newline or whitespace separated) into the
    /// canonical stored form, or null when nothing was given. Throws
    /// <see cref="ScopeFormatException"/> naming the first entry that is not valid.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<string>();
        foreach (var token in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseEntry(token, out var entry, out var error) is false)
                throw new ScopeFormatException($"allowed_hosts: '{token}' is not valid — {error}");
            var canonical = entry!.ToString();
            if (seen.Add(canonical)) entries.Add(canonical);
        }
        return entries.Count == 0 ? null : string.Join('\n', entries);
    }

    /// <summary><see cref="Normalize"/> without the exception: the error names the bad entry.</summary>
    public static bool TryNormalize(string? raw, out string? normalized, out string? error)
    {
        try
        {
            normalized = Normalize(raw);
            error = null;
            return true;
        }
        catch (ScopeFormatException ex)
        {
            normalized = null;
            error = ex.Message;
            return false;
        }
    }

    public static bool IsScoped(Credential credential) => !string.IsNullOrWhiteSpace(credential.AllowedHosts);

    /// <summary>True when the credential may be sent to <paramref name="host"/>; an unscoped credential may.</summary>
    public static bool Permits(Credential credential, string? host) => Covers(credential.AllowedHosts, host);

    /// <summary>
    /// Whether <paramref name="host"/> (a device address: IP or hostname) falls inside
    /// <paramref name="allowedHosts"/>. Null/blank scope covers everything; a scope
    /// covers nothing it does not literally list. Entries that do not parse are ignored,
    /// so a damaged row fails closed.
    /// </summary>
    public static bool Covers(string? allowedHosts, string? host)
    {
        if (string.IsNullOrWhiteSpace(allowedHosts)) return true;
        var target = (host ?? string.Empty).Trim();
        if (target.Length == 0) return false;

        var ip = ParseIp(target);
        var name = ip is null ? NormalizeHostname(target) : null;

        foreach (var token in allowedHosts.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseEntry(token, out var entry, out _)) continue;
            if (ip is not null)
            {
                if (entry!.Network is { } net && Contains(net, entry.PrefixLength, ip)) return true;
            }
            else if (name is not null && entry!.Hostname is { } h
                     && string.Equals(h, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The send-time refusal. Names the credential and the device, never the scope.</summary>
    public static string OutOfScopeMessage(string credentialName, string deviceName) =>
        $"credential '{credentialName}' may not be sent to device '{deviceName}': "
        + "the device's address is outside the hosts an Admin allowed for this credential";

    // ── parsing ───────────────────────────────────────────────────────────

    // One parsed entry: an address with a prefix (a bare IP is a full-length prefix)
    // or a hostname.
    private sealed record Entry(IPAddress? Network, int PrefixLength, string? Hostname)
    {
        public override string ToString()
        {
            if (Hostname is not null) return Hostname;
            var full = Network!.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            return PrefixLength == full ? Network.ToString() : $"{Network}/{PrefixLength}";
        }
    }

    private static bool TryParseEntry(string token, out Entry? entry, out string? error)
    {
        entry = null;
        error = null;
        var slash = token.IndexOf('/');
        if (slash >= 0)
        {
            var addrPart = token[..slash];
            var prefixPart = token[(slash + 1)..];
            var addr = ParseIp(addrPart);
            if (addr is null)
            {
                error = "a CIDR network needs an IP address before the '/'";
                return false;
            }
            if (addrPart.Contains(':') && IPAddress.TryParse(addrPart, out var raw6) && raw6.IsIPv4MappedToIPv6)
            {
                error = "write an IPv4 network in IPv4 form (e.g. 10.0.0.0/24)";
                return false;
            }
            var max = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (!int.TryParse(prefixPart, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var prefix)
                || prefixPart.Length > 3 || prefix > max)
            {
                error = $"the prefix length must be a number from 1 to {max}";
                return false;
            }
            if (prefix == 0)
            {
                error = "a /0 network covers every address, which is not a scope";
                return false;
            }
            entry = new Entry(Mask(addr, prefix), prefix, null);
            return true;
        }

        var ip = ParseIp(token);
        if (ip is not null)
        {
            entry = new Entry(ip, ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128, null);
            return true;
        }

        if (token.Contains(':'))
        {
            error = "not a valid IPv6 address";
            return false;
        }
        if (DigitsAndDots().IsMatch(token))
        {
            error = "not a valid IPv4 address (four decimal octets, no leading zeros)";
            return false;
        }
        var name = NormalizeHostname(token);
        if (name is null)
        {
            error = "expected an IP address, a CIDR network or an exact hostname (no wildcards)";
            return false;
        }
        entry = new Entry(null, 0, name);
        return true;
    }

    // Strict: IPv4 only as four decimal octets (IPAddress.TryParse alone accepts "10",
    // "10.1" and octal forms), IPv6 only with a ':' and without a zone id. An
    // IPv4-mapped IPv6 address is returned as the IPv4 address it maps.
    private static IPAddress? ParseIp(string text)
    {
        if (text.Length == 0) return null;
        if (text.Contains(':'))
        {
            if (text.Contains('%')) return null;
            if (!IPAddress.TryParse(text, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return null;
            return v6.IsIPv4MappedToIPv6 ? v6.MapToIPv4() : v6;
        }
        if (!StrictIpv4Pattern().IsMatch(text)) return null;
        if (!IPAddress.TryParse(text, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork) return null;
        foreach (var octet in text.Split('.'))
            if (int.Parse(octet, System.Globalization.CultureInfo.InvariantCulture) > 255) return null;
        return v4;
    }

    private static string? NormalizeHostname(string text)
    {
        var name = text.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0 || DigitsAndDots().IsMatch(name)) return null;
        return HostnamePattern().IsMatch(name) ? name : null;
    }

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsHere = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bitsHere));
        }
        return new IPAddress(bytes);
    }

    // Same family only: an IPv4 host never matches an IPv6 network and vice versa
    // (IPv4-mapped hosts were already turned into IPv4 by ParseIp).
    private static bool Contains(IPAddress network, int prefix, IPAddress host)
    {
        if (network.AddressFamily != host.AddressFamily) return false;
        return Mask(host, prefix).Equals(network);
    }
}
