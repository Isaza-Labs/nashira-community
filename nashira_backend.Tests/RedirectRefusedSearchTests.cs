using nashira_backend.Services.Net;

namespace nashira_backend.Tests;

// RedirectRefusedException.Search looks through an exception graph, which an HTTP client library
// may have built around the refusal. It has one budget for the whole walk (depth, distinct
// exceptions examined and child links followed), visits children in order, searches an exception
// again when a shorter path reaches it, and answers with one of three outcomes: found, not found
// after examining everything, or incomplete because a limit stopped it. Incomplete is never read
// as "no refusal".
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

    private static Exception Filler() => new TimeoutException("example filler");

    private static void AssertFound(Exception? graph, RedirectRefusedException? expected = null)
    {
        var result = RedirectRefusedException.Search(graph);
        Assert.Equal(RedirectSearchOutcome.Found, result.Outcome);
        Assert.Same(expected ?? Refusal, result.Refusal);
        Assert.False(result.IsIncomplete);
    }

    private static void AssertNotFound(Exception? graph)
    {
        var result = RedirectRefusedException.Search(graph);
        Assert.Equal(RedirectSearchOutcome.NotFound, result.Outcome);
        Assert.Null(result.Refusal);
        Assert.False(result.IsIncomplete);
    }

    private static void AssertIncomplete(Exception? graph)
    {
        var result = RedirectRefusedException.Search(graph);
        Assert.Equal(RedirectSearchOutcome.Incomplete, result.Outcome);
        Assert.Null(result.Refusal);
        Assert.True(result.IsIncomplete);
    }

    [Fact]
    public void The_existing_shapes_are_still_found()
    {
        AssertFound(Refusal);
        AssertFound(new IOException("example", Refusal));
        AssertFound(Wrap(Refusal, 2));
        AssertFound(new AggregateException(Filler(), Refusal));
        AssertFound(new InvalidOperationException("example", new AggregateException(Filler(), Refusal)));
    }

    [Fact]
    public void A_completed_search_with_no_refusal_is_not_found()
    {
        AssertNotFound(null);
        AssertNotFound(new IOException("example", Filler()));
        AssertNotFound(new AggregateException(Filler()));
        AssertNotFound(new AggregateException(Filler(), new IOException("example", Filler())));
        // The last exception sits on the deepest searched level and has nothing below it.
        AssertNotFound(Wrap(Filler(), RedirectRefusedException.MaxSearchDepth - 1));
    }

    [Fact]
    public void A_refusal_at_the_deepest_searched_level_is_found_and_one_level_further_is_incomplete()
    {
        AssertFound(Wrap(Refusal, RedirectRefusedException.MaxSearchDepth - 1));
        AssertIncomplete(Wrap(Refusal, RedirectRefusedException.MaxSearchDepth));
        // Not a refusal anywhere, but exceptions below the limit were never examined.
        AssertIncomplete(Wrap(Filler(), RedirectRefusedException.MaxSearchDepth));
    }

    [Fact]
    public void Nested_aggregates_share_the_depth_budget()
    {
        AssertFound(NestAggregates(Refusal, RedirectRefusedException.MaxSearchDepth - 1));
        AssertIncomplete(NestAggregates(Refusal, RedirectRefusedException.MaxSearchDepth));
        // Far deeper than anything real, built without recursion.
        AssertIncomplete(NestAggregates(Refusal, 5_000));
    }

    [Fact]
    public void A_refusal_within_the_node_budget_is_found_and_one_past_it_is_incomplete()
    {
        // The aggregate is the first node examined; each filler is one more, then the refusal.
        var within = RedirectRefusedException.MaxSearchNodes - 2;
        AssertFound(Wide(within));
        AssertIncomplete(Wide(within + 1));

        static Exception Wide(int fillers) => new AggregateException(
            Enumerable.Range(0, fillers).Select(_ => Filler()).Append(Refusal));
    }

    [Fact]
    public void A_very_wide_aggregate_terminates_and_is_incomplete()
    {
        var children = Enumerable.Range(0, 100_000).Select(_ => Filler()).Append(Refusal);

        var result = Bounded(() => RedirectRefusedException.Search(new AggregateException(children)));

        Assert.Equal(RedirectSearchOutcome.Incomplete, result.Outcome);
    }

    [Fact]
    public void A_wide_and_deep_graph_terminates_and_is_incomplete()
    {
        Exception Level(int depth) => depth == 0
            ? Filler()
            : new AggregateException(Enumerable.Range(0, 6).Select(_ => Level(depth - 1)).ToArray());

        // 6^6 leaves; the refusal sits behind all of them.
        var graph = new AggregateException(Level(6), Refusal);

        Assert.Equal(RedirectSearchOutcome.Incomplete, Bounded(() => RedirectRefusedException.Search(graph)).Outcome);
    }

    [Fact]
    public void A_shared_child_is_examined_once_however_many_paths_lead_to_it()
    {
        // Each level holds the level below it twice, so reaching every leaf by path would take 2^5 visits
        // and push the refusal that follows out of the budget. Examined once, it is found.
        Exception shared = Filler();
        for (var i = 0; i < 5; i++) shared = new AggregateException(shared, shared);

        AssertFound(new AggregateException(shared, Refusal));
    }

    // The first path to a shared exception can be a long one that ends before its children are reached.
    // A shorter path to the same exception must still search them.
    [Fact]
    public void A_shared_exception_first_reached_deep_is_searched_again_from_a_shallower_path()
    {
        var shared = new IOException("example shared", Refusal);
        var graph = new AggregateException(Wrap(shared, 6), shared);

        // `shared` is at level 7 down the first child and at level 1 down the second.
        AssertFound(graph);
    }

    [Fact]
    public void An_exception_cut_short_on_one_path_and_searched_in_full_on_another_does_not_leave_the_result_open()
    {
        var shared = new IOException("example shared", Filler());

        // Only the deep path reaches it: its child is below the limit, so the answer is open.
        AssertIncomplete(Wrap(shared, RedirectRefusedException.MaxSearchDepth - 1));
        // The shallow path reaches it too and searches everything beneath it.
        AssertNotFound(new AggregateException(Wrap(shared, 6), shared));
    }

    [Fact]
    public void The_first_child_is_searched_before_its_siblings_and_is_not_starved_by_them()
    {
        // A wrapper first, then 63 ordinary siblings: the refusal is the third exception examined.
        var siblings = Enumerable.Range(0, RedirectRefusedException.MaxSearchNodes - 1).Select(_ => Filler());
        AssertFound(new AggregateException(new Exception[] { new AggregateException(Refusal) }.Concat(siblings)));

        // The earlier child wins when both would be found.
        var first = new RedirectRefusedException(301);
        var second = new RedirectRefusedException(307);
        AssertFound(new AggregateException(new IOException("example", first), second), first);
    }

    [Fact]
    public void A_list_of_the_same_exception_repeated_is_not_a_wide_list_of_distinct_ones()
    {
        var repeated = Filler();

        // 64 references to one exception and then a refusal: three distinct exceptions.
        AssertFound(new AggregateException(
            Enumerable.Repeat(repeated, RedirectRefusedException.MaxSearchNodes).Append(Refusal)));
    }

    [Fact]
    public void Links_are_bounded_separately_so_a_huge_repeated_list_is_incomplete_and_terminates()
    {
        var repeated = Filler();
        var graph = new AggregateException(Enumerable.Repeat(repeated, 200_000).Append(Refusal));

        Assert.Equal(RedirectSearchOutcome.Incomplete, Bounded(() => RedirectRefusedException.Search(graph)).Outcome);
    }

    // A search that is not bounded would not return; this turns that into a failure instead of a hang.
    private static T Bounded<T>(Func<T> search)
    {
        var task = Task.Run(search);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the search did not finish");
        return task.Result;
    }
}
