using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Worker.Python;
using nashira_backend.Data.Db;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests.Conformance;

// Family `snippets` (workflow-v1-conformance/snippets/SPEC.md).
//
// Two forms.
//
//   { type, input }  -> { normalized_input }
//       The payload after alias normalisation, before execution — SnippetInputNormalizer,
//       which is the single place the canonical-vs-alias question is answered and is on
//       the real execution path (SnippetNodeExecutor.AttemptAsync).
//
//   { type, probe }  -> { output }
//       The type's REAL handler is run on `probe` (normalised first, exactly as the node
//       executor does) and its output is returned, so a vector can assert the portable
//       output set with `fields-present`.
//
// The second form deliberately does not follow adapters/README's original `{ type,
// output } -> { fields }` shape, and the reason is the whole point of the kit: a vector
// that carries a captured output and then asserts which fields that same output has can
// never fail. It agrees with itself. Running the handler is what turns "these are the
// portable fields" into a claim about the product. It is also what lets an alias vector
// assert the tool that was ACTUALLY invoked rather than the key the payload happened to
// carry.
//
// Fakes sit at the same boundary the existing handler tests use — an in-memory
// DbContext and a stub MCP service — and nothing here opens a socket except the `ping`
// probe, which is aimed at a closed loopback port.
public sealed partial class NashiraAdapter
{
    private static JsonElement? Snippets(JsonElement input)
    {
        var type = Str(input, "type") ?? throw new InvalidOperationException("a snippets vector needs `type`");

        // Presence, not value: `declared_idempotency: null` is a vector saying the snippet
        // declares nothing, which is a different question from a vector that does not ask
        // about tiers at all. See NashiraAdapter.Idempotency.cs.
        if (input.TryGetProperty("declared_idempotency", out var declared))
            return Tier(type, declared, Prop(input, "config_overrides"));

        if (Prop(input, "probe") is { } probe)
        {
            var r = RunHandler(type, SnippetInputNormalizer.Normalize(type, probe), input);
            var answer = new Dictionary<string, JsonElement> { ["output"] = r.Output };
            // Only when the handler actually reached outward. An unconditional `sent` would
            // change the shape the two existing `exact` probe vectors assert.
            if (r.Sent is { } sent) answer["sent"] = sent;
            if (r.Logs is { } logs) answer["logs"] = JsonSerializer.SerializeToElement(logs);
            return JsonSerializer.SerializeToElement(answer);
        }

        var payload = Prop(input, "input")
            ?? throw new InvalidOperationException("a snippets vector needs either `input` or `probe`");

        return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
        {
            ["normalized_input"] = SnippetInputNormalizer.Normalize(type, payload),
        });
    }

    private static ProbeResult RunHandler(string type, JsonElement payload, JsonElement vector)
    {
        // `ansible_playbook` never reaches a handler here: the run path needs a Linux host
        // and an `ansible-playbook` binary, and what the contract asserts is not in the
        // step's output anyway. What the play is handed is the assertable part, and it comes
        // from the same function the run path calls.
        if (type == "ansible_playbook")
        {
            var extraVars = AnsiblePlaybookSnippetHandler.ExtraVarsJson(payload);
            using var parsed = JsonDocument.Parse(extraVars);
            return new ProbeResult(
                Output: JsonSerializer.SerializeToElement(new { }),
                Sent: JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["play_variables"] = parsed.RootElement.Clone(),
                }));
        }

        if (type == "python_snippet") return RunPython(payload, vector);

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"conformance-snippets-{Guid.NewGuid()}").Options);

        RecordingNotificationDispatcher? slack = null;
        RecordingSlackProvider? slackBot = null;
        RecordingRestExecutor? rest = null;
        RecordingEmailSender? email = null;
        RecordingDefaultRelay? relay = null;

        ISnippetHandler handler;
        switch (type)
        {
            case "ping":
                handler = new PingSnippetHandler(db, NullLogger<PingSnippetHandler>.Instance);
                break;
            case "transform":
                handler = new TransformSnippetHandler();
                break;
            case "mcp_call":
                handler = SeedMcpServer(db, payload);
                break;
            case "slack_message":
                slack = new RecordingNotificationDispatcher();
                slackBot = new RecordingSlackProvider();
                handler = SeedSlackChannel(db, slack, slackBot, payload);
                break;
            case "email_send":
                email = new RecordingEmailSender();
                relay = new RecordingDefaultRelay();
                handler = SeedEmailChannel(db, email, relay, payload);
                break;
            case "rest_call":
                rest = new RecordingRestExecutor();
                handler = new RestCallSnippetHandler(
                    rest, new UnusedUrlGuard(), new UnusedSecretResolver(), new UnusedHttpClientFactory());
                break;
            default:
                throw new InvalidOperationException(
                    $"the snippets family has no probe harness for '{type}'. Add one, or write the vector "
                    + "in the `{ type, input }` form — do not let it fall through and report nothing.");
        }

        var result = handler.ExecuteAsync(
            new SnippetRequest
            {
                NodeId = "probe",
                WorkflowId = Guid.Empty,
                SnippetId = Guid.NewGuid(),
                SnippetType = type,
                Input = payload,
                TimeoutSeconds = 5,
                Environment = "draft",
            },
            default).GetAwaiter().GetResult();

        if (!result.Success && result.ErrorCode is "bad_input" or "not_found")
            throw new InvalidOperationException(
                $"the probe never reached the handler's output path: {result.ErrorCode} — {result.Error}");

        // A refusal IS an answer here: `not_supported` is what the canonical-key rule
        // requires of a handler that cannot honour a key, so the vector asserting it needs
        // the outcome rather than an exception. The output of a failed step is empty, so the
        // refusal is reported in its own right.
        if (!result.Success)
            return new ProbeResult(JsonSerializer.SerializeToElement(new
            {
                ok = false,
                error_code = result.ErrorCode,
                error = result.Error,
            }));

        // `logs` is NOT propagated here. Several handlers set it for diagnostics, and adding
        // it to the answer changed the shape the two `exact` probe vectors assert — a probe
        // must not alter what it observes. Only `python_snippet` reports logs, because there
        // the contract puts the script's raw output in them (D4).
        return new ProbeResult(
            result.Output,
            Sent: slackBot?.Sent() ?? slack?.Sent(payload) ?? email?.Sent() ?? relay?.Sent() ?? rest?.Sent());
    }

    // Exactly one active Slack channel, so `ResolveByDestinationAsync` finds it without the
    // node needing a `via`. The dispatcher records rather than posts.
    //
    // Which transport gets seeded is the ADAPTER's decision, not the vector's. A vector says
    // what the contract requires — "a message carrying a thread reference is posted as a reply
    // in that thread" — and each product arranges whatever it takes to answer that. Here a
    // thread reference means the bot transport, because it is the only one that can carry one;
    // in the oracle there is only ever the one transport and its adapter does nothing special.
    // A vector naming a transport would be a vector describing this product's plumbing, which
    // no other implementation could run.
    private static ISnippetHandler SeedSlackChannel(
        AppDbContext db, RecordingNotificationDispatcher dispatcher,
        RecordingSlackProvider provider, JsonElement payload)
    {
        if (Prop(payload, "thread_ts") is not null)
        {
            db.MessagingChannels.Add(new nashira_backend.Data.Models.MessagingChannel
            {
                MessagingChannelId = Guid.NewGuid(),
                Provider = nashira_backend.Data.Models.MessagingChannel.ProviderSlack,
                Name = "conformance",
                Slug = "conformance",
                BotTokenEncrypted = System.Text.Encoding.UTF8.GetBytes("xoxb-conformance"),
                Enabled = true,
            });
            db.SaveChanges();
            return new SlackMessageSnippetHandler(db, dispatcher, new OneProviderResolver(provider),
                new PassthroughSecretProtector(), NullLogger<SlackMessageSnippetHandler>.Instance);
        }

        db.NotificationChannels.Add(new nashira_backend.Data.Models.NotificationChannel
        {
            NotificationChannelId = Guid.NewGuid(),
            Name = "conformance",
            Slug = "conformance",
            Kind = nashira_backend.Data.Models.NotificationChannel.KindSlack,
        });
        db.SaveChanges();
        return new SlackMessageSnippetHandler(db, dispatcher, new NoBotProviderResolver(),
            new PassthroughSecretProtector(), NullLogger<SlackMessageSnippetHandler>.Instance);
    }

    // A named channel when the payload names one, the deployment relay otherwise — which is
    // the distinction the contract's sender rule turns on, and the payload already carries it.
    private static ISnippetHandler SeedEmailChannel(
        AppDbContext db, RecordingEmailSender sender, RecordingDefaultRelay relay, JsonElement payload)
    {
        if (Str(payload, "channel") is { Length: > 0 } slug)
        {
            db.EmailChannels.Add(new nashira_backend.Data.Models.EmailChannel
            {
                EmailChannelId = Guid.NewGuid(),
                Name = slug,
                Slug = slug,
                Host = "smtp.conformance.test",
                FromAddress = "nashira@conformance.test",
                Enabled = true,
            });
            db.SaveChanges();
        }

        return new EmailSendSnippetHandler(db, sender, relay, NullLogger<EmailSendSnippetHandler>.Instance);
    }

    // The real sandbox and a real interpreter. The script is synthesised from the vector's
    // `probe`, which names the value the script is to produce; `emit` chooses HOW it says so
    // — `assign` (this product's convention) or `print` (the oracle's). That second form is
    // the one no output-shape vector can see, because unwrapping the envelope satisfies the
    // shape while a printing script still returns nothing.
    private static ProbeResult RunPython(JsonElement payload, JsonElement vector)
    {
        var exe = FindPython()
            ?? throw new InvalidOperationException(
                "no Python interpreter is on PATH, so the python_snippet probe cannot answer. A "
                + "vector that needs one must declare \"not_implemented\": true rather than be "
                + "silently skipped — an unrun probe is not a passing one.");

        var literal = payload.GetRawText();
        var code = (Str(vector, "emit") ?? "assign") switch
        {
            "print" => $"import json\nprint(json.dumps({literal}))",
            "assign" => $"result = {literal}",
            var other => throw new InvalidOperationException(
                $"a python_snippet probe's `emit` must be \"assign\" or \"print\", not '{other}'"),
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Python:Executable"] = exe })
            .Build();

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"conformance-python-{Guid.NewGuid()}").Options);

        // The printed form needs `json`, and the import guard is real: without this the probe
        // is refused with `module_denied` before it ever reaches the interpreter. Seeding one
        // stdlib module is the smallest thing that lets the oracle's own convention be
        // expressed — it does not weaken the guard, which is exercised by its own tests.
        db.AllowedPythonModules.Add(new nashira_backend.Data.Models.AllowedPythonModule
        {
            AllowedPythonModuleId = Guid.NewGuid(),
            Module = "json",
            Status = nashira_backend.Data.Models.AllowedPythonModule.StatusReady,
        });
        db.SaveChanges();

        var handler = new PythonSnippetHandler(
            db,
            new PythonSandbox(config, NullLogger<PythonSandbox>.Instance),
            // Throws rather than no-ops: a vector carrying a ${secret:…} reference would be
            // describing a credential this harness never set up, and handing the script the
            // literal marker instead is not what either side of the contract means.
            new UnusedSecretResolver(),
            config,
            NullLogger<PythonSnippetHandler>.Instance);

        var result = handler.ExecuteAsync(
            new SnippetRequest
            {
                NodeId = "probe",
                WorkflowId = Guid.Empty,
                SnippetId = Guid.NewGuid(),
                SnippetType = "python_snippet",
                Code = code,
                Input = payload,
                TimeoutSeconds = 30,
                Environment = "draft",
            },
            default).GetAwaiter().GetResult();

        if (!result.Success)
            throw new InvalidOperationException(
                $"the python probe did not run: {result.ErrorCode} — {result.Error}");

        return new ProbeResult(result.Output, Logs: result.Logs);
    }

    private static string? FindPython()
    {
        foreach (var exe in new[] { "python3", "python" })
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p is null) continue;
                p.WaitForExit(5000);
                if (p.HasExited && p.ExitCode == 0) return exe;
            }
            catch (Exception) { /* not this one */ }
        }
        return null;
    }

    private static ISnippetHandler SeedMcpServer(AppDbContext db, JsonElement payload)
    {
        db.McpServers.Add(new nashira_backend.Data.Models.McpServer
        {
            McpServerId = Guid.NewGuid(),
            Name = Str(payload, "server") ?? "ops",
        });
        db.SaveChanges();
        return new McpCallSnippetHandler(db, new EchoingMcpService());
    }

    // Answers with the tool name it was asked for, so the handler's output carries the
    // tool that was ACTUALLY invoked. That is what makes an inverted alias visible: a
    // payload naming two tools resolves to one, and this says which.
    private sealed class EchoingMcpService : IMcpServerService
    {
        public Task<McpSyncResult> SyncToolsAsync(Guid serverId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IntegrationHealthLike> CheckAsync(Guid serverId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<McpCallResult> CallAsync(Guid serverId, string toolName, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(new McpCallResult(toolName, null, false));
    }
}
