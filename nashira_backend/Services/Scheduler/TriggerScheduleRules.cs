namespace nashira_backend.Services.Scheduler;

// When an edit to a cron trigger should move its next firing, and when it must not.
//
// This exists because of a specific failure, and it is the kind that only shows up
// some of the time. The update endpoint re-based NextRunAt whenever the request
// carried a cron expression OR a timezone — and the console always sends both,
// because it serialises the whole form. So renaming a trigger, or ticking "enabled",
// silently rescheduled it: edit a nightly `0 2 * * *` at 01:59 and it does not run
// that night, edit it at 03:00 and nothing appears wrong. Worse, an edit landing in
// the seconds between a firing coming due and the 30-second sweep noticing would
// move the due time forward and drop that run entirely.
//
// Renaming a schedule is not rescheduling it. Only a change to the expression or the
// zone is.
public static class TriggerScheduleRules
{
    public const string DefaultTimezone = "UTC";

    // The schedule an edit results in, and whether that differs from the stored one.
    // Both fields are normalised the same way they are stored, so the comparison is
    // not defeated by trailing whitespace the caller never typed.
    public readonly record struct Resolved(string Expression, string Timezone, bool Changed);

    // `newExpression` / `newTimezone` are null when the caller left them out. An
    // explicit value that matches what is stored counts as unchanged — that is the
    // whole point.
    public static Resolved Resolve(
        string? currentExpression, string? currentTimezone,
        string? newExpression, string? newTimezone)
    {
        var current = Normalize(currentExpression);
        var currentZone = NormalizeZone(currentTimezone);

        var expression = string.IsNullOrWhiteSpace(newExpression) ? current : Normalize(newExpression);
        var zone = string.IsNullOrWhiteSpace(newTimezone) ? currentZone : NormalizeZone(newTimezone);

        var changed = !string.Equals(expression, current, StringComparison.Ordinal)
                      || !string.Equals(zone, currentZone, StringComparison.Ordinal);

        return new Resolved(expression, zone, changed);
    }

    private static string Normalize(string? raw) => (raw ?? string.Empty).Trim();

    private static string NormalizeZone(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? DefaultTimezone : raw.Trim();
}
