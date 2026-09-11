namespace nashira_backend.Services.Messaging;

// Role math for channel turns. The effective role is the MORE RESTRICTIVE of the
// linked user's real role and the channel's MaxRole ceiling — a channel can only
// narrow privileges, never widen them. There is no escalation by transport: a
// non-admin can no more run an admin tool from Slack than from the web.
public static class MessagingRoles
{
    private static readonly Dictionary<string, int> Rank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["viewer"] = 1,
        ["operator"] = 2,
        ["admin"] = 3,
    };

    // min(userRole, maxRole). Null/empty maxRole = no ceiling (the user's role wins).
    //
    // An unknown user role defaults to the LOWEST rank and an unknown ceiling to the
    // highest, so a typo in either column can only ever grant less, never more.
    public static string Effective(string userRole, string? maxRole)
    {
        if (string.IsNullOrWhiteSpace(maxRole)) return Normalize(userRole);
        var u = Rank.GetValueOrDefault(userRole, 1);
        var m = Rank.GetValueOrDefault(maxRole, int.MaxValue);
        return u <= m ? Normalize(userRole) : Normalize(maxRole);
    }

    private static string Normalize(string role) => role.ToLowerInvariant();
}
