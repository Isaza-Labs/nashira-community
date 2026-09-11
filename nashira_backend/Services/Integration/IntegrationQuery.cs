using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Services.Integration;

// Filters shared by the HTTP list endpoint and the agent's list_integrations tool, so
// the two cannot answer the same question differently.
public static class IntegrationQuery
{
    // `type` is a free-text label an admin may leave empty, while the slug is always
    // derived from the name. Matching either means the obvious query — type=netbox —
    // finds the NetBox integration whether or not anyone filled the field in, instead
    // of returning count: 0 and sending the caller (or the agent) hunting elsewhere.
    public static IQueryable<IntegrationEntity> FilterByType(IQueryable<IntegrationEntity> q, string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return q;
        var t = type.Trim().ToLowerInvariant();
        return q.Where(i => i.Type == t || i.Slug == t);
    }

    // Name lookup, for a caller who knows what it is looking for but not how the
    // admin capitalised it.
    //
    // This used to be exact, case-sensitive equality, and it produced one specific
    // bad outcome over and over: the agent asked list_integrations(name: "netbox"),
    // the integration was called "NetBox", the tool answered with nothing, and the
    // agent told the user NetBox was not configured — while it sat there configured
    // and health-checked. Being wrong about absence is far worse than returning an
    // extra row, so this matches name, slug or type, case-insensitively, as a
    // substring.
    public static IQueryable<IntegrationEntity> FilterByName(IQueryable<IntegrationEntity> q, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return q;
        var n = name.Trim().ToLowerInvariant();
        return q.Where(i =>
            i.Name.ToLower().Contains(n)
            || i.Slug.Contains(n)
            || i.Type.Contains(n));
    }
}
