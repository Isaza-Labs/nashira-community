namespace nashira_backend.Services.Common;

// Shared bounds for limit/offset across every list endpoint.
public static class Pagination
{
    public const int MaxLimit = 200;

    public static (int limit, int offset) Clamp(int limit, int offset) =>
        (Math.Clamp(limit, 1, MaxLimit), Math.Max(0, offset));
}
