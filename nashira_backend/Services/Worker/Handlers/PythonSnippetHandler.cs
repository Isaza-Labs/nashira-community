using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Worker.Python;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// Runs an author's Python against the step's input.
//
// The tier is `requires_compensation`, the contract's default for this type, and the
// snippet's author may declare either way from there. It used to be NonReversible, which
// Idempotency.Effective treats as ABSOLUTE — so an author could not declare the tier the
// contract grants, and a portable workflow that rolls back became one that reports
// `final_state: failed` here while the oracle reports `rolled_back`. The old reasoning was
// that arbitrary code cannot be assumed re-runnable; the answer to that is that the author
// is the only party who can say, and the ceiling took the say away from them. A script that
// genuinely cannot be undone is still declarable as such on its snippet.
public sealed class PythonSnippetHandler : ISnippetHandler
{
    private readonly AppDbContext _db;
    private readonly IPythonSandbox _sandbox;
    private readonly ISecretResolver _secrets;
    private readonly IConfiguration _config;
    private readonly ILogger<PythonSnippetHandler> _logger;
    private readonly IPythonEnvironmentResolver? _environments;

    public PythonSnippetHandler(
        AppDbContext db, IPythonSandbox sandbox, ISecretResolver secrets, IConfiguration config,
        ILogger<PythonSnippetHandler> logger, IPythonEnvironmentResolver? environments = null)
    {
        _db = db;
        _sandbox = sandbox;
        _secrets = secrets;
        _config = config;
        _logger = logger;
        _environments = environments;
    }

    public string Type => Data.Models.Snippet.TypePythonSnippet;
    // The contract's default for this type. It used to declare NonReversible, which is
    // ABSOLUTE in Idempotency.Effective — so a snippet declaring `requires_compensation`
    // could not take effect, and a portable workflow that rolls back became one that reports
    // `final_state: failed` here while the oracle reports `rolled_back`. A script's
    // reversibility is a property of what the author wrote, and the author is the one who can
    // declare it; the handler cannot read the script's intent and must not guess the
    // strictest answer on their behalf.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        if (!StepOutputPrivacy.TryRead(request.Input, out var redactOutput))
            return SnippetResult.Fail("redact_output must be a boolean", "invalid_redact_output");
        using var secrets = new SecretResolutionScope();
        if (string.IsNullOrWhiteSpace(request.Code))
            return SnippetResult.Fail("this python_snippet has no code", "bad_snippet");

        // The static scan is an authoring hint, not the boundary — but a snippet
        // using dynamic import machinery cannot be checked at all, so it is refused
        // rather than run and hoped about.
        var scan = PythonImportScanner.Scan(request.Code);
        if (scan.Rejected.Count > 0)
            return SnippetResult.Fail(
                $"this snippet uses {string.Join(", ", scan.Rejected)}, which defeats import checking and is not permitted",
                "dynamic_import");

        var networkEnabled = request.NetworkEnabled;

        // Every active row, network-gated or not. Filtering the gated ones out of the
        // query was what made a gated module indistinguishable from an unapproved one
        // further down: paramiko and socket, both approved and both ready, were
        // reported as "not on the allowlist".
        var candidates = await _db.AllowedPythonModules.AsNoTracking()
            .Where(m => m.IsActive)
            .Select(m => new { m.Module, m.Status, m.Source, m.RequiresNetwork })
            .ToListAsync(ct);

        // Only `ready` rows are offered, and a network-gated row only when this
        // snippet has opted in. A pip package still installing, or one whose install
        // failed, is on the allowlist and not on disk — importing it would fail
        // anyway, with a message pointing at the script instead of at the install.
        var allowed = candidates
            .Where(m => m.Status == AllowedPythonModule.StatusReady
                && (!m.RequiresNetwork || networkEnabled))
            .Select(m => m.Module)
            .ToList();

        // Fail before spawning anything: naming the offending module beats an
        // ImportError from inside the interpreter.
        var blocked = scan.Modules.Except(allowed, StringComparer.OrdinalIgnoreCase).ToList();
        if (blocked.Count > 0)
        {
            // Three different situations with three different fixes, and only one of
            // them is the snippet author's. Saying "not on the allowlist" for all of
            // them sends an admin to look at a list the module is already on.
            var known = candidates
                .Where(m => blocked.Contains(m.Module, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // Approved, installed, and gated behind a switch on the SNIPPET rather
            // than on the module. Nothing about the allowlist needs changing.
            var gated = known
                .Where(m => m.Status == AllowedPythonModule.StatusReady && m.RequiresNetwork)
                .Select(m => m.Module)
                .ToList();

            var notReady = known
                .Where(m => m.Status != AllowedPythonModule.StatusReady)
                .ToList();

            var unknown = blocked
                .Where(b => !known.Any(k => string.Equals(k.Module, b, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var parts = new List<string>();
            if (gated.Count > 0)
                parts.Add($"{string.Join(", ", gated)} are approved but need network access, "
                    + "which this snippet does not have — an administrator can enable "
                    + "`network_enabled` on the snippet");
            if (unknown.Count > 0)
                parts.Add($"{string.Join(", ", unknown)} are not on the allowlist");
            foreach (var m in notReady)
                parts.Add(m.Status == AllowedPythonModule.StatusFailed
                    ? $"'{m.Module}' is approved but its install failed — see Admin → Python modules"
                    : $"'{m.Module}' is approved and still installing — retry in a moment");

            return SnippetResult.Fail($"module(s) {string.Join("; ", parts)}", "module_denied");
        }

        // The snippet's Python environment, when it names one. Why this stays inside the
        // sandbox's guarantees:
        //   - It only chooses the interpreter binary. The path comes from the resolver —
        //     a slug-named directory under the packages volume, set by an Admin — never
        //     from the step's input, so a workflow cannot point the sandbox at a binary.
        //   - The import allowlist above has already run and is enforced again inside the
        //     interpreter by the harness. A package present in the environment but not on
        //     the allowlist is exactly as unimportable as one that is not installed.
        //   - PythonSandbox applies the same wrapper, `-I -B -u`, scrubbed environment,
        //     per-run working directory, deadline and output caps to it. `-I` still keeps
        //     PYTHONPATH and the user site out; the venv's own site-packages is part of the
        //     interpreter's prefix, which is the point of choosing it.
        //   - The approved-packages dir is appended to sys.path AFTER the environment's
        //     site-packages, so the environment's pinned versions win and a pip-approved
        //     module that is not in the environment still imports as before.
        // Resolved before any secret is decrypted: an unusable environment fails the step
        // without plaintext ever having existed.
        string? interpreter = null;
        if (request.PythonEnvironmentId is { } environmentId)
        {
            if (_environments is null)
                return SnippetResult.Fail("Python environments are not available in this process",
                    PythonEnvironmentUnavailableException.Code);
            try
            {
                interpreter = (await _environments.ResolveAsync(environmentId, ct)).Interpreter;
            }
            catch (PythonEnvironmentUnavailableException ex)
            {
                return SnippetResult.Fail(ex.Message, PythonEnvironmentUnavailableException.Code);
            }
        }

        // The script needs plaintext to act, but its output cannot be trusted to
        // distinguish public data from encoded secrets. Track resolution, not just matches.
        var (scriptInput, secretValues) = await ResolveSecretsAsync(request.Input, ct);

        PythonRunResult result;
        try
        {
            result = await _sandbox.RunAsync(
                new PythonRunRequest(
                    Code: request.Code!,
                    Input: scriptInput,
                    TimeoutSeconds: request.TimeoutSeconds,
                    NetworkEnabled: networkEnabled,
                    AllowedModules: allowed,
                    PackagesDir: PythonPackagesLayout.ResolveSiteDirIfPopulated(_config),
                    Interpreter: interpreter),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException &&
            (secrets.HasResolvedMaterial || secretValues.Count > 0 || redactOutput))
        {
            return StepOutputPrivacy.Withhold(SnippetResult.Fail("script failed", "script_error"));
        }

        if (secrets.HasResolvedMaterial || secretValues.Count > 0 || redactOutput)
            return StepOutputPrivacy.Withhold(new SnippetResult
            {
                Success = result.Ok, Change = StepChange.AuthorDecides,
                // Only host-decided sandbox codes survive Withhold; script_error, timeout
                // and the rest still become output_redacted.
                ErrorCode = result.Ok ? null : result.ErrorKind,
                Retryable = result.ErrorKind == "timeout",
            });

        if (!result.Ok)
        {
            _logger.LogWarning(
                "python.snippet.failed node={Node} kind={Kind} exit={Exit}",
                request.NodeId, result.ErrorKind, result.ExitCode);

            return new SnippetResult
            {
                Success = false,
                // The script failed; whether it had already mutated something before it did
                // is exactly what this handler cannot know, so the author still decides.
                Change = StepChange.AuthorDecides,
                Output = SecretRedactor.Redact(JsonSerializer.SerializeToElement(new
                {
                    error = result.Stderr,
                    stdout = result.Stdout,
                    error_kind = result.ErrorKind,
                }), secretValues),
                Error = SecretRedactor.Redact(
                    string.IsNullOrWhiteSpace(result.Stderr) ? "the script failed" : result.Stderr,
                    secretValues) ?? string.Empty,
                ErrorCode = result.ErrorKind ?? "script_error",
                // What the script printed before it failed is the first thing anyone reads
                // when diagnosing it, and it belongs in the same place as on the success
                // path rather than only inside the output object.
                Logs = SecretRedactor.Redact(result.Stdout, secretValues),
                // A timeout may be a slow dependency; a syntax or import error will
                // fail identically forever.
                Retryable = result.ErrorKind == "timeout",
            };
        }

        return new SnippetResult
        {
            Success = true,
            // The author's script decides. This handler runs whatever was written and
            // cannot tell a report generator from a configuration push, so it says so
            // instead of guessing — and the snippet declares (D1). Guessing here is what
            // the removed tier inference used to do.
            Change = StepChange.AuthorDecides,

            // The value the script produced, unwrapped. The sandbox has ALREADY separated it
            // from what the script printed (SplitEnvelope splits on its own sentinel), so the
            // `{result, stdout}` this used to build was a second wrap around an
            // already-separated pair — and it put the script's object one level deeper than
            // any workflow written against the contract addresses it.
            // Masked on the way out: this is stored on the run and read by people who may not
            // be allowed to read the secrets the script was given.
            Output = SecretRedactor.Redact(PortableOutput(result), secretValues),
            // Where the printed text belongs: the step already carries logs, the run detail
            // already shows them, and nothing downstream addresses them by field.
            Logs = SecretRedactor.Redact(result.Stdout, secretValues),

        };
    }

    /// <summary>
    /// Replaces every <c>${secret:…}</c> reference in the step's input with the plaintext
    /// behind it, immediately before the interpreter starts.
    /// </summary>
    /// <remarks>
    /// The `ssh`, `rest_call` and `integration_action` handlers have always done this; this
    /// one did not, and the gap had a shape: a script that needed a device password had no way
    /// to be given one except as a literal in the node's `config_overrides` — which is stored
    /// in the workflow definition, copied into every bundle export, and echoed back in the
    /// step's input snapshot on every run. The credential store exists precisely so that
    /// never happens, and a `python_snippet` was the one step type that could not reach it.
    ///
    /// Resolution happens HERE rather than in the executor for the same reason it does in the
    /// other handlers: the executor's stored snapshot is written before this runs, so the
    /// plaintext lives only inside this call and the child process it feeds. An unresolvable
    /// reference is left literal by the resolver, so a typo reaches the script as the marker
    /// the author typed instead of as a silent empty string.
    ///
    /// Any successful resolution withholds the entire script result. Value masking alone
    /// cannot stop encoded output from entering a stored run readable by other roles.
    /// </remarks>
    private async Task<(JsonElement Input, IReadOnlyCollection<string> Secrets)> ResolveSecretsAsync(
        JsonElement input, CancellationToken ct)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return (input, []);

        var secrets = new HashSet<string>(StringComparer.Ordinal);
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            await WriteWithSecretsAsync(writer, input, secrets, ct);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return (doc.RootElement.Clone(), secrets);
    }

    // Substitutes marker by marker rather than the whole string at once, which is what lets
    // this know which plaintext values it inserted. An unresolved marker stays literal, as it
    // always did, and is not a value to mask.
    private async Task<string> SubstituteTrackedAsync(string raw, ISet<string> secrets, CancellationToken ct)
    {
        var references = SecretResolver.References(raw);
        if (references.Count == 0) return raw;

        var sb = new StringBuilder(raw.Length);
        var cursor = 0;
        foreach (var reference in references)
        {
            var at = raw.IndexOf(reference.Raw, cursor, StringComparison.Ordinal);
            sb.Append(raw, cursor, at - cursor);

            var value = await _secrets.SubstituteAsync(reference.Raw, ct);
            sb.Append(value);
            if (!string.Equals(value, reference.Raw, StringComparison.Ordinal))
                secrets.Add(value);

            cursor = at + reference.Raw.Length;
        }
        sb.Append(raw, cursor, raw.Length - cursor);
        return sb.ToString();
    }

    // Walks the whole payload, not just its top level: a credential is as likely to sit in
    // `{"steps": [{"send": "${secret:…}"}]}` as directly on the object.
    private async Task WriteWithSecretsAsync(
        Utf8JsonWriter writer, JsonElement el, ISet<string> secrets, CancellationToken ct)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var raw = el.GetString() ?? string.Empty;
                writer.WriteStringValue(
                    raw.Contains("${secret:", StringComparison.Ordinal)
                        ? await SubstituteTrackedAsync(raw, secrets, ct)
                        : raw);
                break;

            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in el.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    await WriteWithSecretsAsync(writer, prop.Value, secrets, ct);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in el.EnumerateArray())
                    await WriteWithSecretsAsync(writer, item, secrets, ct);
                writer.WriteEndArray();
                break;

            default:
                el.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// The step's output: what the script said it produced, whichever way it said it.
    /// </summary>
    /// <remarks>
    /// Two authoring conventions have to work, because a workflow is portable and its script
    /// travels with it. This product's harness binds a name and reads it back; the contract's
    /// oracle has no such harness and takes the script's stdout as the return channel. A
    /// script written for either one runs here.
    ///
    /// The assigned value WINS where a script does both, so accepting the printed form cannot
    /// change what an already-written script means — the same precedence the payload alias
    /// table uses, where the canonical form wins and the alternative only fills its absence.
    ///
    /// A script that assigns nothing and prints nothing parseable produced no value, and says
    /// so with a JSON null rather than a failure: a script whose purpose is its side effect is
    /// not an error. When it printed something unparseable the text is preserved under `raw`,
    /// which is what the oracle does with stdout it cannot parse — a portable template reading
    /// `.raw` finds it here too.
    /// </remarks>
    internal static JsonElement PortableOutput(PythonRunResult result)
    {
        if (result.Output.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return result.Output;

        var printed = result.Stdout?.Trim();
        if (string.IsNullOrEmpty(printed)) return result.Output;

        try
        {
            using var doc = JsonDocument.Parse(printed);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { raw = printed });
        }
    }
}
