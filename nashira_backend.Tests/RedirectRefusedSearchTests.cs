using nashira_backend.Services.Net;

namespace nashira_backend.Tests;

// RedirectRefusedException.Find looks through an exception graph, which an HTTP client library
// may have built around the refusal. The search has one shared budget (depth and number of
// exceptions examined) so no shape of graph, deep, wide, shared or cyclic, can make it do
// unbounded work, and a refusal beyond the budget is reported as not found.
public class RedirectRefusedSearchTests
{
    private static readonly RedirectRefusedException Refusal = new(302);

    private static Exception Wrap(Exception inner, int levels)
    {
        for (var i = 0; i < levels; i++) inner = new IOException("example wrapper", inner);
        return inner;
    }

    private static Exception NestAggregates(Exception inner, int levels)
    {
        for (var i = 0; i < levels; i++) inner = new AggregateException(new TimeoutException("example sibling"), inner);
        return inner;
    }

    [Fact]
    public void The_existing_shapes_are_still_found()
    {
        Assert.Same(Refusal, RedirectRefusedException.Find(Refusal));
        Assert.Same(Refusal, RedirectRefusedException.Find(new IOException("example", Refusal)));
        Assert.Same(Refusal, RedirectRefusedException.Find(Wrap(Refusal, 2)));
        Assert.Same(Refusal, RedirectRefusedException.Find(
            new AggregateException(new TimeoutException("example"), Refusal)));
        Assert.Same(Refusal, RedirectRefusedException.Find(
            new InvalidOperationException("example", new AggregateException(new TimeoutException("example"), Refusal))));
    }

    [Fact]
    public void Nothing_found_is_null()
    {
        Assert.Null(RedirectRefusedException.Find(null));
        Assert.Null(RedirectRefusedException.Find(new IOException("example", new TimeoutException("example"))));
        Assert.Null(RedirectRefusedException.Find(new AggregateException(new TimeoutException("example"))));
    }

    [Fact]
    public void A_refusal_at_the_deepest_searched_level_is_found_and_one_level_further_is_not()
    {
        Assert.Same(Refusal, RedirectRefusedException.Find(Wrap(Refusal, RedirectRefusedException.MaxSearchDepth - 1)));
        Assert.Null(RedirectRefusedException.Find(Wrap(Refusal, RedirectRefusedException.MaxSearchDepth)));
    }

    [Fact]
    public void Nested_aggregates_share_the_depth_budget()
    {
        Assert.Same(Refusal, RedirectRefusedException.Find(
            NestAggregates(Refusal, RedirectRefusedException.MaxSearchDepth - 1)));
        Assert.Null(RedirectRefusedException.Find(
            NestAggregates(Refusal, RedirectRefusedException.MaxSearchDepth)));
        // Far deeper than anything real, built without recursion.
        Assert.Null(RedirectRefusedException.Find(NestAggregates(Refusal, 5_000)));
    }

    [Fact]
    public void A_refusal_within_the_node_budget_is_found_and_one_past_it_is_not()
    {
        // The aggregate is the first node examined; each filler is one more, then the refusal.
        var within = RedirectRefusedException.MaxSearchNodes - 2;
        Assert.Same(Refusal, RedirectRefusedException.Find(Wide(within)));
        Assert.Null(RedirectRefusedException.Find(Wide(within + 1)));

        static Exception Wide(int fillers) => new AggregateException(
            Enumerable.Range(0, fillers).Select(_ => (Exception)new TimeoutException("example filler"))
                .Append(Refusal));
    }

    [Fact]
    public void A_very_wide_aggregate_terminates_and_does_not_find_a_refusal_past_the_budget()
    {
        var children = Enumerable.Range(0, 100_000).Select(_ => (Exception)new TimeoutException("example filler"))
            .Append(Refusal);

        Assert.Null(Bounded(() => RedirectRefusedException.Find(new AggregateException(children))));
    }

    [Fact]
    public void A_shared_child_is_examined_once_however_many_paths_lead_to_it()
    {
        // Each level holds the level below it twice, so reaching every leaf by path would take 2^5 visits
        // and push the refusal that follows out of the budget. Examined once, it is found.
        Exception shared = new TimeoutException("example shared");
        for (var i = 0; i < 5; i++) shared = new AggregateException(shared, shared);

        Assert.Same(Refusal, RedirectRefusedException.Find(new AggregateException(shared, Refusal)));
    }

    [Fact]
    public void A_wide_and_deep_graph_terminates_without_a_result()
    {
        Exception Level(int depth) => depth == 0
            ? new TimeoutException("example leaf")
            : new AggregateException(Enumerable.Range(0, 6).Select(_ => Level(depth - 1)).ToArray());

        // 6^6 leaves; the refusal sits behind all of them.
        var graph = new AggregateException(Level(6), Refusal);

        Assert.Null(Bounded(() => RedirectRefusedException.Find(graph)));
    }

    // A search that is not bounded would not return; this turns that into a failure instead of a hang.
    private static T Bounded<T>(Func<T> search)
    {
        var task = Task.Run(search);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the search did not finish");
        return task.Result;
    }
}
