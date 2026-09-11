using nashira_backend.Services.Scheduler;

namespace nashira_backend.Tests;

// A maintenance window is a wall-clock time in a place. Evaluating it in UTC
// shifts it by an hour twice a year, which is precisely when nobody is watching.
public class CronScheduleTests
{
    [Theory]
    [InlineData("0 2 * * *")]        // 5-field standard
    [InlineData("*/15 * * * *")]
    [InlineData("0 0 1 * *")]
    [InlineData("0 0 2 * * *")]      // 6-field with seconds
    public void Valid_expressions_parse(string expression)
    {
        Assert.True(CronSchedule.TryParse(expression, out var parsed, out var error));
        Assert.NotNull(parsed);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cron")]
    [InlineData("99 * * * *")]
    public void Invalid_expressions_are_rejected_with_a_reason(string? expression)
    {
        Assert.False(CronSchedule.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void An_unknown_timezone_is_rejected()
    {
        Assert.False(CronSchedule.IsValidTimezone("Mars/Olympus", out _, out var error));
        Assert.Contains("Mars/Olympus", error);
    }

    [Fact]
    public void A_blank_timezone_defaults_to_utc()
    {
        Assert.True(CronSchedule.IsValidTimezone(null, out var zone, out _));
        Assert.NotNull(zone);
    }

    [Fact]
    public void The_next_occurrence_is_strictly_after_the_reference_time()
    {
        // Inclusive would re-fire the occurrence that just ran, because the sweep
        // re-bases from "now" the moment it fires.
        var at2am = new DateTime(2026, 6, 1, 2, 0, 0, DateTimeKind.Utc);
        var next = CronSchedule.NextOccurrence("0 2 * * *", "UTC", at2am);

        Assert.NotNull(next);
        Assert.True(next > at2am);
        Assert.Equal(new DateTime(2026, 6, 2, 2, 0, 0, DateTimeKind.Utc), next);
    }

    // The reason the timezone is stored at all: the same expression resolves to a
    // different UTC instant depending on the zone's offset.
    [Fact]
    public void The_same_expression_resolves_differently_per_timezone()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var utc = CronSchedule.NextOccurrence("0 2 * * *", "UTC", from);
        var madrid = CronSchedule.NextOccurrence("0 2 * * *", "Europe/Madrid", from);

        Assert.NotNull(utc);
        Assert.NotNull(madrid);
        Assert.NotEqual(utc, madrid);
    }

    [Fact]
    public void An_unparseable_expression_yields_no_occurrence_rather_than_now()
    {
        // Null must mean "never fires again". Falling back to "now" would make an
        // unschedulable trigger fire on every single tick.
        Assert.Null(CronSchedule.NextOccurrence("garbage", "UTC", DateTime.UtcNow));
        Assert.Null(CronSchedule.NextOccurrence("0 2 * * *", "Mars/Olympus", DateTime.UtcNow));
    }
}
