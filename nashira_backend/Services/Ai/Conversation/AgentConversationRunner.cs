using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Chat;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Trace;

namespace nashira_backend.Services.Ai.Conversation;

public sealed record AgentTurnRequest(
    Guid? ConversationId,
    string Message,
    IReadOnlyList<string>? Approvals = null,
    IReadOnlyList<ChatAttachment>? Attachments = null,
    // Which provider should answer this turn, and optionally which of its models.
    // Both null means "whatever this conversation already used", falling back to
    // the tenant default for a conversation that has never named one.
    Guid? ProviderId = null,
    string? Model = null);

// The agent tool-calling loop: stream the LLM -> emit text to the sink + collect
// tool calls -> dispatch each -> feed results back -> repeat until the model
// answers with no tool calls (or a cap/deadline is hit). Scoped (per turn), so
// the injected ToolDispatcher's mutation budget resets each turn.
public sealed class AgentConversationRunner
{
    private const double Temperature = 0.2;

    private readonly AppDbContext _db;
    private readonly LlmProviderFactory _providerFactory;
    private readonly ToolRegistry _registry;
    private readonly ToolDispatcher _dispatcher;
    private readonly ISkillPromptLoader _skills;
    private readonly ScopedSkillCatalog _scopedSkills;
    private readonly UserProfileContextLoader _profiles;
    private readonly ICurrentUser _user;
    private readonly AgentTurnScope _turnScope;
    private readonly ILogger<AgentConversationRunner> _logger;
    private readonly ITraceLogger _trace;
    private readonly AiChatOptions _chat;

    public AgentConversationRunner(
        AppDbContext db, LlmProviderFactory providerFactory, ToolRegistry registry,
        ToolDispatcher dispatcher, ISkillPromptLoader skills, ScopedSkillCatalog scopedSkills,
        UserProfileContextLoader profiles, ICurrentUser user, AgentTurnScope turnScope,
        ILogger<AgentConversationRunner> logger, ITraceLogger trace, IOptions<AiChatOptions> chat)
    {
        _chat = chat.Value;
        _scopedSkills = scopedSkills;
        _profiles = profiles;
        _trace = trace;
        _db = db;
        _providerFactory = providerFactory;
        _registry = registry;
        _dispatcher = dispatcher;
        _skills = skills;
        _user = user;
        _turnScope = turnScope;
        _logger = logger;
    }

    public async Task RunAsync(AgentTurnRequest request, IAgentEventSink sink, CancellationToken ct)
    {
        var conversationId = request.ConversationId ?? Guid.NewGuid();
        var isNew = !request.ConversationId.HasValue;
        await sink.ConversationAsync(conversationId, isNew, ct);

        // Publish it for the turn so tool handlers can attribute what they create to
        // this conversation. Set before any dispatch; the scope dies with the turn.
        _turnScope.ConversationId = conversationId;

        var deadlineSeconds = _chat.StreamDeadlineSeconds > 0 ? _chat.StreamDeadlineSeconds : 240;
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(TimeSpan.FromSeconds(deadlineSeconds));
        var streamCt = deadlineCts.Token;

        ResolvedLlm resolved;
        try
        {
            resolved = await ResolveProviderAsync(request, streamCt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai.chat.resolve_failed");
            await sink.ErrorAsync(ex.Message, "no_provider", ct);
            return;
        }
        var llm = resolved.Provider;
        var model = resolved.Model;
        await sink.ModelAsync(resolved.ProviderId, resolved.ProviderName, model, ct);

        var tools = _registry.ToDefinitions();
        var toolListText = tools.Count == 0
            ? "(none)"
            : string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}"));
        var basePrompt = await _skills.LoadAsync(toolListText, streamCt);

        // Integration skills: the index always, the text when the integration is in
        // play. "In play" at the start of a turn means loaded earlier in this
        // conversation or named in this message; API calls during the turn and the
        // load_skill tool add to the set as it goes (see AutoLoadSkillAsync).
        var scopedSkills = await _scopedSkills.ListAsync(streamCt);
        var previouslyLoaded = scopedSkills.Count == 0
            ? []
            : await LoadLoadedSkillsAsync(request.ConversationId, streamCt);
        foreach (var id in previouslyLoaded) _turnScope.MarkSkillLoaded(id);
        foreach (var id in ScopedSkillCatalog.MatchMentions(request.Message, scopedSkills)) _turnScope.MarkSkillLoaded(id);
        var systemPrompt = ScopedSkillCatalog.ComposePrompt(
            basePrompt, scopedSkills, new HashSet<Guid>(_turnScope.LoadedSkillIntegrationIds));

        // The user's persona (assigned profile + own text) goes in as a second
        // system message, never into the shared prompt: that one is cached
        // process-wide. Failing to read it costs the persona for this turn only.
        var profile = await LoadUserProfileAsync(streamCt);
        var profileMessage = profile?.Render();

        // Consent lasts for the conversation it was given in. Approving the same tool on
        // every message taught people to approve without reading; a fresh chat still
        // starts from nothing, so the scope of the permission stays something a user can
        // actually hold in their head.
        var previouslyApproved = await LoadApprovedToolsAsync(request.ConversationId, streamCt);
        var approvals = new HashSet<string>(previouslyApproved, StringComparer.OrdinalIgnoreCase);
        foreach (var name in request.Approvals ?? []) approvals.Add(name);
        _dispatcher.ApproveTools(approvals);
        var newlyApproved = approvals.Count > previouslyApproved.Count;
        var composedMessage = ComposeUserMessage(request.Message, request.Attachments);
        var messages = await BuildInitialMessagesAsync(systemPrompt, profileMessage, composedMessage, request.ConversationId, streamCt);

        var finalText = new StringBuilder();
        var totalIn = 0;
        var totalOut = 0;
        var iterations = 0;
        var errored = false;
        var awaitingConfirmation = false;
        var outputCutOff = false;

        // Forensic record of the turn. Collected as it happens because the interesting
        // case is the turn that ends badly, and by then the tool results are gone.
        var startedAt = DateTime.UtcNow;
        var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var toolCalls = new List<ToolTelemetryEntry>();
        var turnStatus = AgentTurn.StatusCompleted;
        string? turnError = null;

        var maxIterations = _chat.MaxIterations > 0 ? _chat.MaxIterations : 50;
        var maxToolResultChars = _chat.MaxToolResultChars > 0 ? _chat.MaxToolResultChars : 100_000;

        _logger.LogInformation(
            "ai.chat.begin conversation={ConversationId} model={Model} tools={ToolCount} "
            + "messages={MessageCount} deadline_s={DeadlineSeconds} max_iterations={MaxIterations} "
            + "max_tool_result_chars={MaxToolResultChars} prompt_chars={PromptChars} "
            + "scoped_skills={ScopedSkills} skills_loaded={SkillsLoaded} profile={Profile}",
            conversationId, model, tools.Count, messages.Count, deadlineSeconds, maxIterations, maxToolResultChars,
            systemPrompt.Length, scopedSkills.Count, _turnScope.LoadedSkillIntegrationIds.Count,
            profile is null ? "none" : profile.ProfileName ?? "custom-text");

        // The warning that arrives while the deadline is still tunable. A turn that
        // busts its budget is reported by whoever was waiting on it, long after the
        // context is gone; this fires at a fraction of the way through and names what
        // the turn was doing, so a deadline that is simply too small for the work is
        // visible before it truncates an answer.
        using var deadlineWarning = ArmDeadlineWarning(
            conversationId, deadlineSeconds, () => (iterations, toolCalls.Count));

        // The turn is already recorded in full by RecordTurnAsync, which /admin/sessions
        // reads. This row is not that: it puts the turn on the same timeline as the HTTP
        // request that carried it and the jobs its tools enqueued, joined by request id.
        // A turn that hangs shows up here as `started` while the session record does not
        // exist yet at all.
        using var trace = _trace.Begin(TraceEvent.CategoryAi, "ai.chat.turn",
            new { conversation_id = conversationId, model, tools = tools.Count });

        try
        {
            for (var iter = 0; iter < maxIterations && !streamCt.IsCancellationRequested; iter++)
            {
                iterations = iter + 1;
                var content = new StringBuilder();
                var pending = new List<ToolCallResult>();
                string? stopReason = null;

                await foreach (var evt in llm.ChatWithToolsStreamAsync(messages, tools, model, Temperature, streamCt))
                {
                    switch (evt.Type)
                    {
                        case "text_delta":
                            content.Append(evt.TextDelta);
                            finalText.Append(evt.TextDelta);
                            await sink.TextAsync(evt.TextDelta ?? string.Empty, ct);
                            break;
                        case "tool_call":
                            if (evt.ToolCall is not null) pending.Add(evt.ToolCall);
                            break;
                        case "done":
                            if (evt.InputTokens is int ti) totalIn += ti;
                            if (evt.OutputTokens is int to) totalOut += to;
                            if (evt.StopReason is not null) stopReason = evt.StopReason;
                            break;
                    }
                }

                if (OutputCutOff(stopReason))
                {
                    // The model ran out of output tokens mid-answer. Nothing in the text
                    // says so — a cut-off answer reads like a short one — and any tool
                    // call it was in the middle of is not one it meant to run, so the
                    // turn ends here: the text as streamed, a notice the user can read,
                    // and a status the session log can filter on.
                    _logger.LogWarning(
                        "ai.chat.output_cut_off conversation={ConversationId} iteration={Iteration} "
                        + "stop_reason={StopReason} pending_tool_calls={PendingToolCalls}",
                        conversationId, iterations, stopReason, pending.Count);
                    outputCutOff = true;
                    finalText.Append(CutOffNotice);
                    await sink.TextAsync(CutOffNotice, ct);
                    break;
                }

                if (pending.Count == 0)
                    break; // model answered with no tool calls -> final

                messages.Add(new LlmMessage { Role = "assistant", Content = content.ToString(), ToolCalls = pending });

                foreach (var tc in pending)
                {
                    // Governance C: a single_confirm/elevated_confirm tool the user hasn't
                    // approved this turn pauses the turn for confirmation. The client
                    // re-sends the request with the tool in `approvals` to proceed.
                    var tier = await _dispatcher.RequiresConfirmationAsync(tc.Name, tc.Arguments, streamCt);
                    if (tier is not null)
                    {
                        // A model that calls a tool usually streams no prose alongside it, so
                        // the turn would end on `confirmation_required` + `done` with an empty
                        // assistant message: over the API that reads as the agent going silent,
                        // and an empty turn is what gets persisted into the history. Say what
                        // is being asked for, in the same channel as any other answer.
                        if (content.Length == 0)
                        {
                            var ask = ConfirmationPrompt(tc.Name, tier);
                            finalText.Append(ask);
                            await sink.TextAsync(ask, ct);
                        }
                        await sink.ConfirmationRequiredAsync(tc.Name, tc.Arguments, tier, ct);
                        awaitingConfirmation = true;
                        break;
                    }

                    await sink.ToolStartAsync(tc.Name, tc.Arguments, ct);
                    var callWatch = System.Diagnostics.Stopwatch.StartNew();
                    var output = await _dispatcher.DispatchAsync(tc.Name, tc.Arguments, streamCt);
                    callWatch.Stop();
                    toolCalls.Add(new ToolTelemetryEntry(
                        tc.Name, tc.Arguments, output.Success, output.Result, (int)callWatch.ElapsedMilliseconds));
                    await sink.ToolResultAsync(tc.Name, output, ct);
                    messages.Add(new LlmMessage
                    {
                        Role = "tool",
                        Content = Cap(output.Result.GetRawText(), maxToolResultChars),
                        ToolCallId = tc.Id,
                    });

                    // A call against an integration's API is the strongest signal that
                    // its skill is needed; the text goes in right behind the result so
                    // the model reads the rules before it interprets the data.
                    if (scopedSkills.Count > 0
                        && await AutoLoadSkillAsync(tc.Name, tc.Arguments, scopedSkills, streamCt) is { } skillMessage)
                        messages.Add(skillMessage);
                }

                if (awaitingConfirmation) break;
            }

            await sink.DoneAsync(totalIn, totalOut, iterations, ct);
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("ai.chat.timeout conversation={ConversationId} iterations={Iterations}", conversationId, iterations);
            errored = true;
            turnStatus = AgentTurn.StatusTimeout;
            turnError = $"the turn exceeded the {deadlineSeconds}s deadline";
            await SafeErrorAsync(sink, turnError, "timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.chat.error conversation={ConversationId}", conversationId);
            errored = true;
            turnStatus = AgentTurn.StatusError;
            turnError = ex.Message;
            await SafeErrorAsync(sink, ex.Message, null);
        }
        finally
        {
            turnStopwatch.Stop();
            if (turnStatus == AgentTurn.StatusCompleted && outputCutOff)
                turnStatus = AgentTurn.StatusTruncated;
            else if (turnStatus == AgentTurn.StatusCompleted && awaitingConfirmation)
                turnStatus = AgentTurn.StatusAwaitingConfirmation;

            var assistantText = errored && finalText.Length == 0 ? "[error]" : finalText.ToString();
            var newlyLoaded = _turnScope.LoadedSkillIntegrationIds.Count > previouslyLoaded.Count;
            await PersistAsync(
                conversationId, request.Message, assistantText, totalIn, totalOut,
                newlyApproved ? approvals : null,
                request.Attachments?.Select(a => a.Filename).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                newlyLoaded ? _turnScope.LoadedSkillIntegrationIds : null,
                resolved.ProviderId, model);
            await RecordTurnAsync(new TurnRecord(
                conversationId, model, request.Message, assistantText, toolCalls, totalIn, totalOut,
                iterations, turnStatus, turnError, startedAt, (int)turnStopwatch.ElapsedMilliseconds));

            var summary = new
            {
                conversation_id = conversationId, model, status = turnStatus,
                iterations, tool_calls = toolCalls.Count,
                tokens_in = totalIn, tokens_out = totalOut,
            };
            if (turnError is null) trace.Complete(summary); else trace.Fail(turnError, summary);
        }
    }

    // The text that stands in for the model's own words when it asks for nothing and
    // simply calls a gated tool. Deliberately names the tool and how to proceed: the
    // `confirmation_required` frame carries the same facts, but a client that only
    // renders assistant text (or a plain API consumer) would otherwise see a blank turn.
    // One-shot timer at DeadlineWarnPercent of the budget. Disabled by a percentage
    // outside 1..99, which is how a deployment turns it off — 0 or 100 both mean
    // "there is no point at which warning is still useful".
    //
    // `progress` is read from the timer thread while the turn keeps writing to those
    // locals. Both reads are of a single field, so the worst case is a count one
    // behind the truth, and a log line is not worth a lock on the turn's hot path.
    private IDisposable? ArmDeadlineWarning(
        Guid conversationId, int deadlineSeconds, Func<(int Iterations, int ToolCalls)> progress)
    {
        var percent = _chat.DeadlineWarnPercent;
        if (percent <= 0 || percent >= 100) return null;

        var at = TimeSpan.FromSeconds(deadlineSeconds * percent / 100.0);
        return new Timer(_ =>
        {
            var (iterations, toolCalls) = progress();
            _logger.LogWarning(
                "ai.chat.deadline_warning conversation={ConversationId} elapsed_s={ElapsedSeconds} "
                + "deadline_s={DeadlineSeconds} iterations={Iterations} tool_calls={ToolCalls}",
                conversationId, (int)at.TotalSeconds, deadlineSeconds, iterations, toolCalls);
        }, null, at, Timeout.InfiniteTimeSpan);
    }

    // What the user reads when the model stops mid-answer for lack of output tokens.
    // Appended to the answer rather than sent as an error frame: an error would paint
    // the whole turn red and hide the text that did arrive, and going through the
    // answer means it is persisted with it, so on the next turn the model also sees
    // that it was cut off and can pick up instead of restarting.
    internal const string CutOffNotice =
        "\n\n[Answer cut off: the model hit its output-token limit before finishing. " +
        "Ask me to continue, or narrow the request.]";

    // OpenAI-compatible endpoints say "length"; Anthropic-style ones say "max_tokens".
    // Either means "not finished", which is the only fact the runner needs.
    internal static bool OutputCutOff(string? stopReason) =>
        string.Equals(stopReason, "length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase);

    private static string ConfirmationPrompt(string toolName, string tier)
    {
        var elevated = string.Equals(tier, Permissions.PermissionClassifier.TierElevatedConfirm,
            StringComparison.OrdinalIgnoreCase);
        return elevated
            ? $"I need your explicit approval before running `{toolName}` — it makes changes that cannot be " +
              "undone from here. Re-send your message with this tool approved to go ahead."
            : $"I need your confirmation before running `{toolName}`. Re-send your message with this tool " +
              "approved to go ahead.";
    }

    // Loads the skills of the integration a tool call is about, once per
    // conversation, as a system message placed right after the tool result. Null
    // when the call names no integration or its skills are already in.
    private async Task<LlmMessage?> AutoLoadSkillAsync(
        string toolName, JsonElement args, IReadOnlyList<ScopedSkill> scopedSkills, CancellationToken ct)
    {
        Guid? integrationId;
        try
        {
            integrationId = await _scopedSkills.IntegrationForToolCallAsync(toolName, args, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not worth a turn: the call already ran; the skill just arrives later.
            _logger.LogWarning(ex, "ai.chat.skill_autoload_failed tool={Tool}", toolName);
            return null;
        }
        if (integrationId is not { } id) return null;

        var skills = scopedSkills.Where(s => !s.AlwaysLoaded && s.IntegrationId == id).ToList();
        if (skills.Count == 0 || !_turnScope.MarkSkillLoaded(id)) return null;

        _logger.LogInformation(
            "ai.chat.skill_autoloaded conversation={ConversationId} tool={Tool} integration={Integration} skills={Skills}",
            _turnScope.ConversationId, toolName, skills[0].IntegrationSlug, string.Join(",", skills.Select(s => s.Name)));

        return new LlmMessage
        {
            Role = "system",
            Content = string.Join("\n\n---\n\n", skills.Select(ScopedSkillCatalog.RenderLoaded)),
        };
    }

    // The user's profile context, or null when there is none — or when the read
    // failed, which is logged and costs the persona for this turn, not the turn.
    private async Task<UserProfileContext?> LoadUserProfileAsync(CancellationToken ct)
    {
        try
        {
            return await _profiles.LoadAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ai.chat.profile_read_failed user={UserId}", _user.UserId);
            return null;
        }
    }

    // Which provider and model answer this turn: what the request asked for, over
    // what the conversation already used, over the tenant default.
    //
    //  - An explicit provider replaces the whole choice, model included. Carrying
    //    the conversation's old model across would ask Anthropic for a GPT id and
    //    fail at the vendor, where the error means nothing to the user.
    //  - A model with no provider is the picker offering another model of the
    //    provider already in play, so the provider is kept.
    //  - Neither: whatever the conversation used last, so a follow-up does not
    //    change brains mid-thread. A conversation that never named one — every
    //    row written before this existed — resolves to the tenant default.
    //
    // A null provider means "no choice on record"; the caller reads that as the
    // default rather than as an error.
    internal static (Guid? ProviderId, string? Model) ChooseModel(
        Guid? requestedProvider, string? requestedModel, Guid? storedProvider, string? storedModel)
    {
        if (requestedProvider is { } picked) return (picked, requestedModel);
        if (!string.IsNullOrWhiteSpace(requestedModel)) return (storedProvider, requestedModel);
        return (storedProvider, storedModel);
    }

    private async Task<ResolvedLlm> ResolveProviderAsync(AgentTurnRequest request, CancellationToken ct)
    {
        var (storedProvider, storedModel) = await LoadModelChoiceAsync(request.ConversationId, ct);
        var (providerId, model) = ChooseModel(request.ProviderId, request.Model, storedProvider, storedModel);

        return providerId is { } id
            ? await _providerFactory.ResolveAsync(id, model, ct)
            : await _providerFactory.ResolveDefaultAsync(model, ct);
    }

    // The provider and model this conversation last used. A conversation that does
    // not exist yet, or that predates the selector, has neither.
    private async Task<(Guid? ProviderId, string? Model)> LoadModelChoiceAsync(
        Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is not { } id) return (null, null);
        try
        {
            var row = await _db.AIConversations.AsNoTracking()
                .Where(c => c.AIConversationId == id && c.UserId == _user.UserId && c.IsActive)
                .Select(c => new { c.AIProviderId, c.Model })
                .FirstOrDefaultAsync(ct);
            return (row?.AIProviderId, row?.Model);
        }
        catch (Exception ex)
        {
            // Failing open means this turn uses the tenant default, which is what
            // every conversation did before the selector existed.
            _logger.LogWarning(ex, "ai.chat.model_choice_read_failed conversation={ConversationId}", id);
            return (null, null);
        }
    }

    // The integrations whose skills this conversation has already loaded. A
    // conversation that does not exist yet has none.
    private async Task<IReadOnlyCollection<Guid>> LoadLoadedSkillsAsync(Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is not { } id) return [];
        try
        {
            var json = await _db.AIConversations.AsNoTracking()
                .Where(c => c.AIConversationId == id && c.UserId == _user.UserId && c.IsActive)
                .Select(c => c.LoadedSkillsJson)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (Exception ex)
        {
            // Failing closed means the skill loads again on the next mention or API
            // call, which costs a round-trip and nothing else.
            _logger.LogWarning(ex, "ai.chat.loaded_skills_read_failed conversation={ConversationId}", id);
            return [];
        }
    }

    private async Task<List<LlmMessage>> BuildInitialMessagesAsync(
        string systemPrompt, string? profileMessage, string userMessage, Guid? conversationId, CancellationToken ct)
    {
        var msgs = new List<LlmMessage> { new() { Role = "system", Content = systemPrompt } };
        if (profileMessage is not null)
            msgs.Add(new LlmMessage { Role = "system", Content = profileMessage });

        if (conversationId.HasValue)
        {
            var conv = await _db.AIConversations.AsNoTracking().FirstOrDefaultAsync(
                c => c.AIConversationId == conversationId.Value && c.IsActive, ct);
            var history = new List<LlmMessage>();
            if (conv is not null)
                foreach (var m in DeserializeMessages(conv.MessagesJson))
                    if (m.Role != "system")
                    {
                        // Earlier turns' attachments persist per conversation, so the
                        // note points at the tool instead of at the user. (Rows can be
                        // evicted past the per-conversation cap; parse_file's error
                        // then names what is still there.)
                        var content = m.Attachments is { Count: > 0 }
                            ? m.Content + "\n[files attached to this message: "
                                + string.Join(", ", m.Attachments)
                                + " — still readable via parse_file, e.g. {\"format\": \"xlsx\", \"attachment\": \""
                                + m.Attachments[0] + "\"}]"
                            : m.Content;
                        history.Add(new LlmMessage { Role = m.Role, Content = content });
                    }

            var (kept, omitted) = WindowHistory(history, _chat.HistoryCharBudget);
            if (omitted > 0)
            {
                _logger.LogInformation(
                    "ai.chat.history_windowed conversation={ConversationId} sent={Sent} omitted={Omitted} budget_chars={Budget}",
                    conversationId, kept.Count, omitted, _chat.HistoryCharBudget);
                msgs.Add(new LlmMessage { Role = "system", Content = HistoryOmittedNote(omitted) });
            }
            msgs.AddRange(kept);
        }

        msgs.Add(new LlmMessage { Role = "user", Content = userMessage });
        return msgs;
    }

    // The newest messages that fit `charBudget`, cut on turn boundaries, oldest
    // first. `Omitted` is how many were left out. The most recent exchange is always
    // sent, budget or no budget: the model has to see what was just said. A budget
    // of zero or less disables the window.
    internal static (List<LlmMessage> Kept, int Omitted) WindowHistory(IReadOnlyList<LlmMessage> history, int charBudget)
    {
        if (charBudget <= 0 || history.Count == 0) return (history.ToList(), 0);

        var start = history.Count;
        long used = 0;
        while (start > 0)
        {
            var length = history[start - 1].Content?.Length ?? 0;
            if (used + length > charBudget && history.Count - start >= 2) break;
            used += length;
            start--;
        }
        // Never open on the agent's half of a turn: an answer without its question
        // reads as the model talking to itself.
        while (start < history.Count && history[start].Role == "assistant") start++;

        return (history.Skip(start).ToList(), start);
    }

    // Told to the model when history was windowed, so it asks about what it cannot
    // see instead of assuming it remembers.
    internal static string HistoryOmittedNote(int omitted) =>
        $"[Context note: the {omitted} earliest messages of this conversation were left out to fit the "
        + "model's context window. If the user refers to something from them, ask rather than assume.]";

    private sealed record TurnRecord(
        Guid ConversationId, string Model, string UserMessage, string AssistantText,
        IReadOnlyList<ToolTelemetryEntry> ToolCalls, int TokensIn, int TokensOut, int Iterations,
        string Status, string? Error, DateTime StartedAt, int ElapsedMs);

    // The forensic row. Written after the conversation is persisted and swallowing its
    // own failures: telemetry that can break a turn is worse than telemetry that is
    // occasionally missing, and the failure is loud in the log either way.
    private async Task RecordTurnAsync(TurnRecord turn)
    {
        try
        {
            _db.AgentTurns.Add(new AgentTurn
            {
                AgentTurnId = Guid.NewGuid(),
                ConversationId = turn.ConversationId,
                UserId = _user.IsAuthenticated ? _user.UserId : null,
                Model = turn.Model,
                UserMessage = ToolTelemetry.Cap(turn.UserMessage),
                AssistantText = ToolTelemetry.Cap(turn.AssistantText),
                ToolCallsJson = ToolTelemetry.Serialize(turn.ToolCalls),
                ToolCallCount = turn.ToolCalls.Count,
                TokensIn = turn.TokensIn,
                TokensOut = turn.TokensOut,
                Iterations = turn.Iterations,
                Status = turn.Status,
                Error = turn.Error is { Length: > 500 } ? turn.Error[..500] : turn.Error,
                StartedAt = turn.StartedAt,
                FinishedAt = turn.StartedAt.AddMilliseconds(turn.ElapsedMs),
                ElapsedMs = turn.ElapsedMs,
            });
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai.chat.turnlog_failed conversation={ConversationId}", turn.ConversationId);
        }
    }

    // The tools this conversation has already had confirmed. A conversation that does
    // not exist yet has none, which is what makes a new chat start from zero.
    private async Task<IReadOnlyCollection<string>> LoadApprovedToolsAsync(
        Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is not { } id) return [];
        try
        {
            var json = await _db.AIConversations.AsNoTracking()
                .Where(c => c.AIConversationId == id && c.UserId == _user.UserId && c.IsActive)
                .Select(c => c.ApprovedToolsJson)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception ex)
        {
            // Failing closed here means asking again, which is the safe direction.
            _logger.LogWarning(ex, "ai.chat.approvals_read_failed conversation={ConversationId}", id);
            return [];
        }
    }

    private async Task PersistAsync(
        Guid conversationId, string userMessage, string assistantContent, int totalIn, int totalOut,
        IReadOnlyCollection<string>? approvedTools, List<string>? attachmentNames = null,
        IReadOnlyCollection<Guid>? loadedSkills = null,
        Guid? providerId = null, string? model = null)
    {
        try
        {
            var now = DateTime.UtcNow;
            var turn = new List<StoredMessage>
            {
                new("user", userMessage, attachmentNames is { Count: > 0 } ? attachmentNames : null),
                new("assistant", assistantContent),
            };
            var approvedJson = approvedTools is { Count: > 0 }
                ? JsonSerializer.Serialize(approvedTools.ToList())
                : null;
            var loadedSkillsJson = loadedSkills is { Count: > 0 }
                ? JsonSerializer.Serialize(loadedSkills.ToList())
                : null;

            await UpsertAttachmentsAsync(conversationId, now);

            var conv = await _db.AIConversations
                .FirstOrDefaultAsync(c => c.AIConversationId == conversationId);
            if (conv is null)
            {
                _db.AIConversations.Add(new AIConversation
                {
                    AIConversationId = conversationId,
                    UserId = _user.UserId,
                    Title = Cap(userMessage, 80),
                    MessagesJson = JsonSerializer.Serialize(turn),
                    Status = "active",
                    TokensIn = totalIn,
                    TokensOut = totalOut,
                    ApprovedToolsJson = approvedJson,
                    LoadedSkillsJson = loadedSkillsJson,
                    AIProviderId = providerId,
                    Model = model,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                var all = DeserializeMessages(conv.MessagesJson);
                all.AddRange(turn);
                conv.MessagesJson = JsonSerializer.Serialize(all);
                conv.TokensIn += totalIn;
                conv.TokensOut += totalOut;
                // Null means "nothing new was approved this turn" — not "revoke".
                if (approvedJson is not null) conv.ApprovedToolsJson = approvedJson;
                // Same rule: null is "nothing new this turn", never "forget".
                if (loadedSkillsJson is not null) conv.LoadedSkillsJson = loadedSkillsJson;
                // The resolved choice, not the requested one: a turn that inherited
                // the conversation's provider writes back the same value, and one
                // that fell back to the tenant default records which default it got,
                // so the thread keeps answering from there even if an admin later
                // adds a provider that would sort ahead of it.
                if (providerId.HasValue) conv.AIProviderId = providerId;
                if (!string.IsNullOrWhiteSpace(model)) conv.Model = model;
                conv.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai.chat.persist_failed conversation={ConversationId}", conversationId);
        }
    }

    // Persists this turn's attachment bytes per conversation, which is what lets
    // parse_file re-read a file in a later turn without the user re-uploading it.
    // One row per filename (case-insensitive; latest wins), oversize files are
    // skipped (parse_file would refuse them anyway), and past the per-conversation
    // cap the oldest-touched rows are evicted.
    private async Task UpsertAttachmentsAsync(Guid conversationId, DateTime now)
    {
        if (_turnScope.Attachments.Count == 0) return;

        var existing = await _db.ConversationAttachments
            .Where(a => a.ConversationId == conversationId)
            .ToListAsync(CancellationToken.None);

        foreach (var (name, bytes) in _turnScope.Attachments)
        {
            if (bytes.Length == 0 || bytes.Length > ConversationAttachment.MaxBytes) continue;
            var row = existing.FirstOrDefault(
                a => string.Equals(a.Filename, name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new ConversationAttachment
                {
                    ConversationAttachmentId = Guid.NewGuid(),
                    ConversationId = conversationId,
                    Filename = name,
                    IsActive = true,
                    CreatedAt = now,
                };
                _db.ConversationAttachments.Add(row);
                existing.Add(row);
            }
            row.Content = bytes;
            row.SizeBytes = bytes.Length;
            row.IsActive = true; // a row left soft-deleted by a conversation delete comes back on re-attach
            row.UpdatedAt = now;
        }

        var over = existing.Count - ConversationAttachment.MaxPerConversation;
        if (over > 0)
            _db.ConversationAttachments.RemoveRange(existing.OrderBy(a => a.UpdatedAt).Take(over));
    }

    private async Task SafeErrorAsync(IAgentEventSink sink, string message, string? code)
    {
        try { await sink.ErrorAsync(message, code, CancellationToken.None); }
        catch { /* client already gone */ }
    }

    private static List<StoredMessage> DeserializeMessages(string json)
    {
        try { return JsonSerializer.Deserialize<List<StoredMessage>>(json) ?? []; }
        catch { return []; }
    }

    private const int MaxAttachmentInlineBytes = 128 * 1024;

    // Inlines attached files into the user message so the agent can read them, and
    // registers the raw bytes on the turn scope so parse_file can reach them by
    // filename (the only route into a binary like an xlsx). Text is included
    // directly (capped); binary is pointed at parse_file rather than dumped. The
    // original message (without attachment content) is what gets persisted.
    private string ComposeUserMessage(string message, IReadOnlyList<ChatAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 }) return message;

        var sb = new StringBuilder(message);
        foreach (var att in attachments)
        {
            var name = string.IsNullOrWhiteSpace(att.Filename) ? "attachment" : att.Filename.Trim();
            sb.Append("\n\n--- Attached file: ").Append(name).Append(" ---\n");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(att.ContentBase64 ?? string.Empty); }
            catch (FormatException) { sb.Append("(could not decode this attachment)"); continue; }

            _turnScope.AddAttachment(name, bytes);

            // A NUL byte in the first block is a strong "not text" signal.
            var probe = bytes.AsSpan(0, Math.Min(bytes.Length, 8192));
            if (probe.IndexOf((byte)0) >= 0)
            {
                var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                var format = ext is "xlsx" or "pdf" or "docx" or "csv" or "json" ? ext : "xlsx";
                sb.Append($"(binary file, {bytes.Length} bytes. Read it with the ")
                  .Append($"parse_file tool: {{\"format\": \"{format}\", \"attachment\": \"{name}\"}} — ")
                  .Append("pick the format matching the file extension. Do not ask the user to paste ")
                  .Append("the content; the file is already available to parse_file.)");
                continue;
            }

            var take = Math.Min(bytes.Length, MaxAttachmentInlineBytes);
            sb.Append(Encoding.UTF8.GetString(bytes, 0, take));
            if (bytes.Length > take)
                sb.Append($"\n… (truncated; {bytes.Length} bytes total — parse_file with ")
                  .Append($"{{\"attachment\": \"{name}\"}} reads the whole file)");
        }
        return sb.ToString();
    }

    private static string Cap(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + $"\n…[truncated {s.Length - max} chars]";

    // `Attachments` carries filenames only (never content): enough for the UI to show
    // the paperclip chips after a reload and for the history note to name what was
    // attached. Null (absent in JSON) on assistant turns and on rows that predate it.
    private sealed record StoredMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("attachments")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        List<string>? Attachments = null);
}
