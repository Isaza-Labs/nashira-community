using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Audit;

public sealed record AuditVerifyResult(bool Valid, int Count, long? BrokenAtSequence, string? Reason);

// Deterministic hash chain over audit events. The hash binds the
// sequence, the previous hash, the actor, the mutated artifact (entity + action +
// before/after) and the timestamp, so any edit or reordering is detectable.
internal static class AuditChain
{
    // A .NET DateTime counts 100-nanosecond ticks; PostgreSQL's `timestamp with time zone`
    // only keeps microseconds. Hashing a timestamp finer than the column can store meant
    // the value that came back from the database was never the value that was hashed, so
    // the chain reported itself broken at its very first row on every real deployment.
    // The hash is now taken over the timestamp as it will actually be stored.
    private const long TicksPerMicrosecond = 10;

    public static string ComputeHash(
        long sequence, string? prevHash, Guid? userId, string? actor,
        string entityType, Guid? entityId, string action,
        string? beforeJson, string? afterJson, DateTime at,
        int hashVersion = AuditHashVersion.Current)
        => ComputeHashAt(
            sequence, prevHash, userId, actor, entityType, entityId, action,
            beforeJson, afterJson, ToStorageResolution(at), hashVersion);

    // Drops any precision the database cannot keep, so a hash taken before the write
    // still matches the row that comes back from it.
    public static DateTime ToStorageResolution(DateTime at)
    {
        var utc = at.ToUniversalTime();
        return new DateTime(utc.Ticks - (utc.Ticks % TicksPerMicrosecond), DateTimeKind.Utc);
    }

    // Verifies linkage + content for events ordered by Sequence ascending.
    public static AuditVerifyResult Verify(IReadOnlyList<AuditEvent> ordered)
    {
        string? prev = null;
        foreach (var e in ordered)
        {
            if ((e.PrevHash ?? string.Empty) != (prev ?? string.Empty))
                return new AuditVerifyResult(false, ordered.Count, e.Sequence, "prev_hash does not match the previous row");

            if (!ContentMatches(e))
                return new AuditVerifyResult(false, ordered.Count, e.Sequence, "hash does not match the row content");

            // A row may not claim a canonical form this build does not know: an
            // unknown version would otherwise be unverifiable and pass by omission.
            if (e.HashVersion is not (AuditHashVersion.WithoutActor or AuditHashVersion.WithActor))
                return new AuditVerifyResult(false, ordered.Count, e.Sequence,
                    $"row declares hash version {e.HashVersion}, which this build cannot verify");

            // v1 predates Actor, so its canonical form does not cover the field — which
            // means an Actor written onto a v1 row afterwards would be invisible to the
            // hash and the chain would certify it. On rows with no UserId that is the
            // only statement of who acted, so the forgery would be exactly the one that
            // matters. A v1 row can never legitimately carry one.
            if (e.HashVersion == AuditHashVersion.WithoutActor && e.Actor is not null)
                return new AuditVerifyResult(false, ordered.Count, e.Sequence,
                    "row predates the actor field but carries one");

            prev = e.Hash;
        }
        return new AuditVerifyResult(true, ordered.Count, null, null);
    }

    // Rows written before the timestamp fix hashed the full tick value, and the database
    // then discarded its last digit — so for those rows the exact input to the original
    // hash no longer exists anywhere. It only had ten possible values, so they are all
    // tried. A match still proves the row's content agrees with a hash committed when the
    // row was written, which is the entire guarantee; a tampered row matches none of the
    // ten. Re-signing the old rows instead would have made them verify by destroying the
    // evidence they exist to provide.
    private static bool ContentMatches(AuditEvent e)
    {
        var storedTicks = ToStorageResolution(e.At).Ticks;

        for (var remainder = 0L; remainder < TicksPerMicrosecond; remainder++)
        {
            // Each row is checked against the canonical form it says it was signed
            // with, so rows written before Actor existed keep verifying unchanged.
            var candidate = ComputeHashAt(
                e.Sequence, e.PrevHash, e.UserId, e.Actor, e.EntityType, e.EntityId, e.Action,
                e.BeforeJson, e.AfterJson, new DateTime(storedTicks + remainder, DateTimeKind.Utc),
                e.HashVersion);

            // Rows written by the current code match on the first attempt.
            if (string.Equals(candidate, e.Hash, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // Hashes the timestamp exactly as given, with no normalization.
    private static string ComputeHashAt(
        long sequence, string? prevHash, Guid? userId, string? actor,
        string entityType, Guid? entityId, string action,
        string? beforeJson, string? afterJson, DateTime at, int hashVersion)
    {
        // v1 is frozen: it is the form the rows already on disk were signed with, and
        // changing it would invalidate every one of them. v2 appends Actor rather than
        // inserting it, for the same reason — an appended field cannot shift the
        // meaning of the ones before it.
        var fields = new List<string>
        {
            sequence.ToString(CultureInfo.InvariantCulture),
            prevHash ?? string.Empty,
            userId?.ToString("N") ?? string.Empty,
            entityType,
            entityId?.ToString("N") ?? string.Empty,
            action,
            beforeJson ?? string.Empty,
            afterJson ?? string.Empty,
            at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };

        if (hashVersion >= AuditHashVersion.WithActor)
            fields.Add(actor ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', fields))));
    }
}
