using Cronos;

namespace nashira_backend.Services.Scheduler;

// Cron parsing and next-occurrence calculation, in the trigger's own timezone.
//
// A maintenance window is a wall-clock time in a place, not an offset from UTC.
// Evaluating "02:00 daily" in UTC shifts it by an hour twice a year, which is
// exactly when someone is asleep and expecting it not to.
public static class CronSchedule
{
    // Five fields is the standard form. Six is accepted because it is what people
    // paste from tools that include seconds, and rejecting it with "invalid" is
    // less useful than just handling it.
    public static bool TryParse(string? expression, out CronExpression? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "cron_expression is required";
            return false;
        }

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        try
        {
            parsed = CronExpression.Parse(expression.Trim(), format);
            return true;
        }
        catch (CronFormatException ex)
        {
            error = $"invalid cron expression: {ex.Message}";
            return false;
        }
    }

    public static bool IsValidTimezone(string? id, out TimeZoneInfo? zone, out string? error)
    {
        zone = null;
        error = null;
        var value = string.IsNullOrWhiteSpace(id) ? "UTC" : id.Trim();
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(value);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = $"unknown timezone '{value}'";
            return false;
        }
    }

    // Next fire strictly after `afterUtc`.
    //
    // Returns null when the expression has no future occurrence — Cronos reports
    // that for a DST-skipped local time that never happens, and for expressions
    // pinned to a date already past. Null has to mean "never fires again" rather
    // than "fire now", or a schedule that cannot occur would fire on every tick.
    public static DateTime? NextOccurrence(CronExpression expression, TimeZoneInfo zone, DateTime afterUtc)
    {
        var after = afterUtc.Kind == DateTimeKind.Utc
            ? afterUtc
            : DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        return expression.GetNextOccurrence(after, zone, inclusive: false);
    }

    public static DateTime? NextOccurrence(string expression, string timezone, DateTime afterUtc)
    {
        if (!TryParse(expression, out var parsed, out _) || parsed is null) return null;
        if (!IsValidTimezone(timezone, out var zone, out _) || zone is null) return null;
        return NextOccurrence(parsed, zone, afterUtc);
    }
}
