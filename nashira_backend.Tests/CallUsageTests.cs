using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// Token counts per model call, from the "done" events a provider's stream reports.
public class CallUsageTests
{
    private static ChatStreamEvent Done(int? input = null, int? output = null, int? read = null, int? write = null) =>
        new() { Type = "done", InputTokens = input, OutputTokens = output, CacheReadTokens = read, CacheWriteTokens = write };

    // An OpenAI-compatible server that sends the same totals on the last content chunk
    // and again in the usage-only chunk, followed by the [DONE] event with no counts.
    [Fact]
    public void The_same_totals_reported_twice_count_once()
    {
        var u = new CallUsage();
        u.Add(Done(6038, 8, 4096));
        u.Add(Done(6038, 8, 4096));
        u.Add(Done());

        Assert.Equal((6038, 8, 4096, 0), (u.Input, u.Output, u.CacheRead, u.CacheWrite));
    }

    // A server that reports a growing running total on every chunk.
    [Fact]
    public void A_growing_running_total_counts_its_last_value()
    {
        var u = new CallUsage();
        u.Add(Done(900, 1, 512));
        u.Add(Done(900, 40, 512));
        u.Add(Done(900, 97, 512));

        Assert.Equal((900, 97, 512), (u.Input, u.Output, u.CacheRead));
    }

    // Anthropic, Gemini and Ollama report a call once: nothing changes for them.
    [Fact]
    public void A_single_report_is_taken_as_it_is()
    {
        var u = new CallUsage();
        u.Add(Done(53297, 412, 51200, 2048));

        Assert.Equal((53297, 412, 51200, 2048), (u.Input, u.Output, u.CacheRead, u.CacheWrite));
    }

    [Fact]
    public void No_report_counts_zero()
    {
        var u = new CallUsage();
        u.Add(Done());

        Assert.Equal((0, 0, 0, 0), (u.Input, u.Output, u.CacheRead, u.CacheWrite));
    }

    // What a caller adds to its running totals at each report: the growth only, so
    // the sum of the increments is the call's final count, and whatever arrived before
    // a stream broke off is already counted.
    [Fact]
    public void Each_report_adds_only_what_it_grew_by()
    {
        var u = new CallUsage();
        var total = (In: 0, Out: 0, Read: 0, Write: 0);
        void Count((int Input, int Output, int CacheRead, int CacheWrite) d) =>
            total = (total.In + d.Input, total.Out + d.Output, total.Read + d.CacheRead, total.Write + d.CacheWrite);

        Count(u.Add(Done(6038, 8, 4096)));
        Assert.Equal((6038, 8, 4096, 0), total);   // a stream that broke off here keeps this much

        Count(u.Add(Done(6038, 8, 4096)));          // the same totals again
        Count(u.Add(Done(6038, 30, 4096)));         // more output
        Count(u.Add(Done()));                       // [DONE], no counts
        Assert.Equal((6038, 30, 4096, 0), total);
        Assert.Equal((u.Input, u.Output, u.CacheRead, u.CacheWrite), total);
    }

    // A provider that reports input and output in separate events.
    [Fact]
    public void Counts_reported_in_separate_events_are_both_kept()
    {
        var u = new CallUsage();
        Assert.Equal((900, 0, 512, 0), u.Add(Done(input: 900, read: 512)));
        Assert.Equal((0, 97, 0, 0), u.Add(Done(output: 97)));
        Assert.Equal((900, 97, 512, 0), (u.Input, u.Output, u.CacheRead, u.CacheWrite));
    }
}
