using nashira_backend.Services.Scheduler;

namespace nashira_backend.Tests;

// When an edit to a cron trigger moves its next firing.
//
// These exist because of a failure that only showed up some of the time, which is
// the worst kind to chase: the update endpoint re-based NextRunAt whenever the
// request carried a cron expression OR a timezone, and the console serialises the
// whole form on every save. So renaming a trigger rescheduled it. Edit a nightly
// `0 2 * * *` at 01:59 and it silently does not run that night; edit it at 03:00 and
// nothing looks wrong. Every one of these is a case where the old code said
// "changed" and would have moved the firing.
public class TriggerScheduleRulesTests
{
    [Fact]
    public void Resending_the_stored_schedule_unchanged_does_not_reschedule()
    {
        var r = TriggerScheduleRules.Resolve("0 2 * * *", "Europe/Madrid", "0 2 * * *", "Europe/Madrid");

        Assert.False(r.Changed);
        Assert.Equal("0 2 * * *", r.Expression);
        Assert.Equal("Europe/Madrid", r.Timezone);
    }

    // The exact shape of the bug: the form sends both fields on a save that only
    // touched the name.
    [Fact]
    public void An_edit_that_only_touched_other_fields_does_not_reschedule()
    {
        var r = TriggerScheduleRules.Resolve("0 2 * * *", "UTC", "0 2 * * *", "UTC");

        Assert.False(r.Changed);
    }

    [Fact]
    public void Omitting_both_fields_keeps_the_stored_schedule()
    {
        var r = TriggerScheduleRules.Resolve("*/15 * * * *", "Europe/Madrid", null, null);

        Assert.False(r.Changed);
        Assert.Equal("*/15 * * * *", r.Expression);
        Assert.Equal("Europe/Madrid", r.Timezone);
    }

    // A caller that sends only one of the two must not have the other reset to a
    // default — that would move a Madrid schedule to UTC on a save that never
    // mentioned the zone.
    [Fact]
    public void Omitting_the_timezone_keeps_the_stored_one_rather_than_defaulting_it()
    {
        var r = TriggerScheduleRules.Resolve("0 2 * * *", "Europe/Madrid", "0 3 * * *", null);

        Assert.True(r.Changed);
        Assert.Equal("0 3 * * *", r.Expression);
        Assert.Equal("Europe/Madrid", r.Timezone);
    }

    [Fact]
    public void Omitting_the_expression_keeps_the_stored_one()
    {
        var r = TriggerScheduleRules.Resolve("0 2 * * *", "UTC", null, "Europe/Madrid");

        Assert.True(r.Changed);
        Assert.Equal("0 2 * * *", r.Expression);
        Assert.Equal("Europe/Madrid", r.Timezone);
    }

    [Fact]
    public void A_new_expression_reschedules()
    {
        Assert.True(TriggerScheduleRules.Resolve("0 2 * * *", "UTC", "0 4 * * *", "UTC").Changed);
    }

    // The same wall-clock time in a different zone is a different firing, so it is a
    // reschedule even though the expression is untouched.
    [Fact]
    public void A_new_timezone_reschedules()
    {
        Assert.True(TriggerScheduleRules.Resolve("0 2 * * *", "UTC", "0 2 * * *", "Europe/Madrid").Changed);
    }

    // Whitespace the user never typed must not read as an edit: the comparison is
    // ordinal, and untrimmed input would reschedule on every save forever.
    [Theory]
    [InlineData(" 0 2 * * *", " UTC ")]
    [InlineData("0 2 * * *  ", "UTC")]
    public void Surrounding_whitespace_is_not_a_change(string expression, string timezone)
    {
        var r = TriggerScheduleRules.Resolve("0 2 * * *", "UTC", expression, timezone);

        Assert.False(r.Changed);
        Assert.Equal("0 2 * * *", r.Expression);
        Assert.Equal("UTC", r.Timezone);
    }

    // An empty string is how the console represents "this field does not apply",
    // not "clear the schedule".
    [Fact]
    public void An_empty_expression_is_treated_as_omitted()
    {
        var r = TriggerScheduleRules.Resolve("0 2 * * *", "UTC", "", "");

        Assert.False(r.Changed);
        Assert.Equal("0 2 * * *", r.Expression);
    }

    // A row stored before the timezone column had a value compares as UTC, so the
    // first save after that does not read as a zone change.
    [Fact]
    public void A_missing_stored_timezone_compares_as_utc()
    {
        Assert.False(TriggerScheduleRules.Resolve("0 2 * * *", null, "0 2 * * *", "UTC").Changed);
        Assert.False(TriggerScheduleRules.Resolve("0 2 * * *", "", "0 2 * * *", null).Changed);
    }

    // Case matters for both: cron field names and IANA zone ids are case-sensitive
    // where it counts, and quietly treating "europe/madrid" as equal to
    // "Europe/Madrid" would skip a validation the caller needs to fail.
    [Fact]
    public void A_differently_cased_timezone_is_treated_as_a_change_so_it_gets_validated()
    {
        Assert.True(TriggerScheduleRules.Resolve("0 2 * * *", "Europe/Madrid", "0 2 * * *", "europe/madrid").Changed);
    }

    [Fact]
    public void Setting_a_schedule_on_a_trigger_that_had_none_is_a_change()
    {
        var r = TriggerScheduleRules.Resolve(null, null, "0 2 * * *", "UTC");

        Assert.True(r.Changed);
        Assert.Equal("0 2 * * *", r.Expression);
        Assert.Equal("UTC", r.Timezone);
    }
}
