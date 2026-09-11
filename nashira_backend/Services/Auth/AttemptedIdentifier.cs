using System.Security.Cryptography;
using System.Text;

namespace nashira_backend.Services.Auth;

// How a failed sign-in records the username that was tried.
//
// The value has to be kept for one reason: without it, twenty failures are twenty lines
// saying "someone could not sign in". With it you can see that `root`, `admin`,
// `administrator` and `oracle` were tried in a row from one address, which is account
// enumeration and is the whole point of keeping the row.
//
// But the login form has two boxes and people put the password in the wrong one. That
// submission misses (no such user), takes this path, and the password lands verbatim in
// an append-only table an administrator reads on screen.
//
// So: a short prefix, the length, and a keyed hash.
//   - The prefix keeps the operational answer. "adm…(5)" is recognisably `admin`.
//   - The hash keeps the correlation: the same input always produces the same value, so
//     repetition and enumeration patterns survive intact.
//   - A mistyped password gives up three characters and its length instead of itself.
//
// The hash is KEYED, and that is load-bearing. Usernames are low-entropy; a plain
// SHA-256 of one is reversible from a wordlist in seconds, and so is a plain hash of a
// weak password. Keyed, the digest is useless off this deployment.
public static class AttemptedIdentifier
{
    private const int PrefixChars = 3;
    private const int DigestChars = 12;

    public sealed record Redacted(string Prefix, int Length, string Hash);

    public static Redacted Redact(string? value, byte[] key)
    {
        var raw = value ?? string.Empty;
        var prefix = raw.Length <= PrefixChars ? raw : raw[..PrefixChars];

        using var hmac = new HMACSHA256(key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(raw));

        return new Redacted(prefix, raw.Length, Convert.ToHexStringLower(digest)[..DigestChars]);
    }

    // Derived from the JWT signing key rather than adding a second secret to configure.
    //
    // Derived, not reused: the label separates this from token signing, so a digest here
    // can never be replayed as anything else and knowing one tells you nothing about the
    // other. The key is already required at boot and is per-deployment, which is exactly
    // the property the digest needs.
    //
    // Rotating the signing key rotates these too, so hashes stop correlating across the
    // rotation. That is the right trade: correlation matters over days, and key rotation
    // is rare.
    public static byte[] DeriveKey(string jwtSigningKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(jwtSigningKey + "|nashira-auth-audit-v1"));
}
