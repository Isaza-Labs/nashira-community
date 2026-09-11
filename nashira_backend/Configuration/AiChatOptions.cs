namespace nashira_backend.Configuration;

// Bound to the "AiChat" section. The wall-clock and iteration budget of a single
// agent turn.
//
// These were constants in AgentConversationRunner, which made them impossible to
// tune per environment — and one of them was silently overruled anyway: the "llm"
// HttpClient carried .NET's default 100s timeout, twenty seconds under the turn
// deadline, so every long turn died at the transport with a raw
// TaskCanceledException instead of the deadline's own message and partial text.
// The HttpClient timeout is now a backstop well above this budget (Program.cs), so
// the numbers here are the ones that actually decide when a turn ends.
public class AiChatOptions
{
    public const string SectionName = "AiChat";

    // Hard wall-clock on a turn. Exceeding it ends the turn as `timeout`, with
    // whatever text had streamed so far already persisted.
    //
    // Sized for the work, not for a single question: building a workflow and its
    // schedule is a chain of tool calls, each a round-trip to the model, and a
    // budget tuned to "what is the status of X" starves it half-built.
    //
    // 240, up from 120: every tool round re-sends the whole prompt, the system prompt
    // alone is tens of thousands of tokens, each REST call may take up to 15s, and a
    // real answer (a table of an inventory) takes the model a while to write. At 120s
    // such turns died as `timeout` with a half-written answer on screen. 240 leaves
    // the 60s margin under TransportTimeout the tests insist on. If a proxy sits in
    // front of the backend, its read timeout has to clear this number too, or the
    // cut simply moves there — silently. Override with `AiChat__StreamDeadlineSeconds`.
    public int StreamDeadlineSeconds { get; set; } = 240;

    // Percent of the deadline at which the runner logs a warning naming the turn and
    // its tool-call count. It is the signal that arrives while the deadline can still
    // be tuned, rather than after somebody reports a truncated answer.
    public int DeadlineWarnPercent { get; set; } = 75;

    // Cap on tool-calling rounds. Runaway protection only — the deadline above is
    // what ends a turn in practice, and it is the honest limit because iterations
    // vary wildly in cost. Set high enough that a legitimately deep task is not cut
    // off mid-chain by a number that has nothing to do with how long it took.
    public int MaxIterations { get; set; } = 50;

    // Cap on the characters of a single tool result echoed back to the model as the
    // `tool` message. Beyond it the result is cut with a marker; the model still gets
    // the head and can narrow the call.
    //
    // This was a private 8_000 constant in AgentConversationRunner — a regression
    // from flow-weaver, where the same cap ships at 100_000 and is configurable. At
    // 8_000 a single execute_operation page (an Action1 or NetBox list with a few
    // dozen rows) never fit, so the model was routinely handed a JSON body cut in
    // half and had to guess at the rest; that is the "truncated response" people
    // reported, and no size of skill text works around it. 100_000 chars is about
    // 25k tokens: large enough for a real page, small enough that the model still
    // has room to answer. Override with `AiChat__MaxToolResultChars`.
    public int MaxToolResultChars { get; set; } = 100_000;

    // Cap on the characters of conversation history replayed to the model each turn.
    // The newest messages that fit are sent, cut on turn boundaries; the model is
    // told how many earlier messages were left out. 0 or less disables the window
    // and replays everything, which is what happened before.
    //
    // Every turn (and every tool round inside it) re-sends the whole thread, so an
    // unbounded history grows until the provider rejects the request or a gateway
    // trims it from the top — where the system prompt is. 60_000 chars is about 15k
    // tokens: a dozen ordinary exchanges, or two or three heavy ones with tables in
    // them, which is as far back as an operator's follow-up questions usually reach.
    // Override with `AiChat__HistoryCharBudget`.
    public int HistoryCharBudget { get; set; } = 60_000;

    // The "llm" HttpClient's own timeout, applied in Program.cs. Not part of the
    // budget and deliberately not configurable: it exists only so a wedged socket
    // cannot hold a turn open forever, and its one requirement is that it stay well
    // clear of StreamDeadlineSeconds. Underneath it, the transport wins every race
    // and the deadline above becomes decoration — which is precisely the bug that
    // .NET's default 100s produced here.
    public static readonly TimeSpan TransportTimeout = TimeSpan.FromMinutes(5);
}
