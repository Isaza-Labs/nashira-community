using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace nashira_backend.Services.Engine;

// Output of a completed step, keyed by node id. Only the output is kept — status
// and timing live on the step row, and a template can only address output.
public sealed record StepResult(JsonElement Output);

public interface IVariableResolver
{
    // Returns `payload` with every template reference inside its string values
    // replaced. Unresolvable references are left literal (see below).
    JsonElement Resolve(
        JsonElement payload,
        IReadOnlyDictionary<string, StepResult> completedSteps,
        JsonElement? deviceContext = null,
        JsonElement? runInput = null,
        JsonElement? runContext = null);
}

// Rewrites `{{ … }}` references inside a node's payload before the handler sees it.
//
// Four namespaces (workflow.v1 templates/SPEC.md §1):
//   {{ steps.<node>.output(.path)? }}  — an upstream step's output
//   {{ device(.path)? }}               — the device this step targets
//   {{ input(.path)? }}                — the payload the run was triggered with
//   {{ run(.path)? }}                  — the run's metadata (RunContext)
//
// Each reference may carry a filter chain (§4): `{{ ref | trim | default('n/a') }}`.
// Pipes outside quotes and parentheses separate filters; arguments are
// comma-separated with quotes stripped. Only `default` recovers an unresolved
// reference; every other filter on an unresolved value leaves the template
// unresolved, and unknown filters are ignored. The grammar is Flow Weaver's,
// token for token, because a workflow written against one engine has to resolve
// identically in the other.
//
// Two substitution modes, and the difference matters:
//   - Whole-string ("{{ steps.ping.output }}") replaces the string with the
//     referenced element, preserving its type. A number stays a number, an object
//     stays an object.
//   - Inline ("rtt={{ steps.ping.output.rtt }}ms") splices the value's string form
//     into surrounding text.
// Without the first mode every value would arrive at the handler as a string, and
// a handler expecting a number would have to guess.
//
// An unresolvable reference is left as literal text rather than becoming null or
// throwing. A missing step is usually an authoring typo, and a payload that
// silently turned into null would fail deep inside a handler with no trace of why;
// the literal `{{ steps.typo.output }}` shows up in the step's input and names
// itself. Callers log them.
public sealed partial class VariableResolver : IVariableResolver
{
    // All four namespaces in one alternation, so a string is resolved in ONE pass.
    //
    // Node ids may contain hyphens — workflow.v1 constrains only length — so
    // `drain-edge` has to match. The expression groups are permissive on purpose: each
    // captures the path AND any filter chain; ParseExpression splits them.
    //
    // This is a security boundary, not a tidy-up. Applying the four expressions in
    // sequence meant each pass ran over the OUTPUT of the previous one: a step whose
    // output contained the literal text `{{ input.api_token }}` — an SSH banner, a
    // command echo, anything a device can be made to say — got spliced in by the steps
    // pass and then RESOLVED by the input pass. Remote, attacker-influenced text
    // reached the `input`, `run` and `device` namespaces, and the residual scan
    // (templates/SPEC.md §8) could not see it, because the reference really had
    // resolved. One pass over the original string cannot splice a reference into a
    // position where it will be read as one.
    //
    // The alternatives keep each namespace's own anchoring: `steps.<id>.output`, and
    // `\b` after `device` / `input` / `run` so `{{ deviceName }}` and `{{ runtime }}`
    // stay literal. Group names, not numbers, because the ordering is now load-bearing.
    [GeneratedRegex(
        @"\{\{\s*(?:steps\.(?<step>[\w-]+)\.output(?<stepexpr>[^}]*?)"
        + @"|device(?<device>\b[^}]*?)|input(?<input>\b[^}]*?)|run(?<run>\b[^}]*?))\s*\}\}",
        RegexOptions.Compiled)]
    private static partial Regex AnyRegex();

    // ANSI escapes, in the three shapes a terminal emits: CSI (colours, cursor
    // moves), OSC (titles, hyperlinks) and the single-character escapes.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled)]
    private static partial Regex AnsiCsi();

    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.Compiled)]
    private static partial Regex AnsiOsc();

    [GeneratedRegex(@"\x1B[@-Z\\-_]", RegexOptions.Compiled)]
    private static partial Regex AnsiOther();

    // C0 controls plus DEL, keeping \t \n \r — a CLI capture with its line breaks
    // removed is not "stripped", it is destroyed.
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled)]
    private static partial Regex C0Controls();

    // Guards against a self-referential payload built by a previous resolution.
    private const int MaxDepth = 32;

    private readonly ILogger<VariableResolver> _logger;

    public VariableResolver(ILogger<VariableResolver> logger) => _logger = logger;

    public JsonElement Resolve(
        JsonElement payload,
        IReadOnlyDictionary<string, StepResult> completedSteps,
        JsonElement? deviceContext = null,
        JsonElement? runInput = null,
        JsonElement? runContext = null)
    {
        var ctx = new Context(completedSteps, deviceContext, runInput, runContext);
        var walked = Walk(payload, ctx, 0);
        return walked ?? JsonSerializer.SerializeToElement(new { });
    }

    private sealed record Context(
        IReadOnlyDictionary<string, StepResult> Steps, JsonElement? Device, JsonElement? Input, JsonElement? Run);

    private JsonElement? Walk(JsonElement node, Context ctx, int depth)
    {
        if (depth > MaxDepth) return node;

        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var map = new Dictionary<string, JsonElement>();
                foreach (var p in node.EnumerateObject())
                    map[p.Name] = Walk(p.Value, ctx, depth + 1) ?? default;
                return JsonSerializer.SerializeToElement(map);
            }

            case JsonValueKind.Array:
            {
                var list = new List<JsonElement>();
                foreach (var item in node.EnumerateArray())
                    list.Add(Walk(item, ctx, depth + 1) ?? default);
                return JsonSerializer.SerializeToElement(list);
            }

            case JsonValueKind.String:
                return ResolveString(node.GetString() ?? string.Empty, ctx);

            default:
                return node;
        }
    }

    private JsonElement ResolveString(string text, Context ctx)
    {
        // Whole-string: the entire value is one reference, so return the element
        // itself and keep its type.
        if (TryWholeString(text, ctx, out var whole)) return whole;

        // Inline: splice each reference's string form into the surrounding text. One
        // pass over the original — never over the result of a previous pass, or a value
        // one reference resolved to would itself be scanned for references.
        var result = AnyRegex().Replace(text, m => Stringify(Resolve(m, ctx), m.Value));

        return JsonSerializer.SerializeToElement(result);
    }

    // Which namespace a match landed in, resolved against the context. The group that
    // participated identifies the alternative that matched.
    private JsonElement? Resolve(Match m, Context ctx)
    {
        if (m.Groups["step"].Success)
            return ResolveStep(m.Groups["step"].Value, m.Groups["stepexpr"].Value, ctx);
        if (m.Groups["device"].Success)
            return ResolveFrom(ctx.Device, m.Groups["device"].Value, "device");
        if (m.Groups["input"].Success)
            return ResolveFrom(ctx.Input, m.Groups["input"].Value, "input");
        if (m.Groups["run"].Success)
            return ResolveFrom(ctx.Run, m.Groups["run"].Value, "run");
        return null;
    }

    private bool TryWholeString(string text, Context ctx, out JsonElement value)
    {
        value = default;
        var trimmed = text.Trim();

        var m = AnyRegex().Match(trimmed);
        if (!m.Success || m.Length != trimmed.Length) return false;

        var resolved = Resolve(m, ctx);
        if (resolved is null) return false;
        value = resolved.Value;
        return true;
    }

    private JsonElement? ResolveStep(string nodeId, string expression, Context ctx)
    {
        if (!ctx.Steps.TryGetValue(nodeId, out var step))
        {
            _logger.LogDebug("workflow.template.unknown_step node={Node}", nodeId);
            // The step is unknown, but `| default(x)` can still stand in for it.
            return ResolveExpression(null, expression);
        }
        return ResolveExpression(step.Output, expression);
    }

    private JsonElement? ResolveFrom(JsonElement? root, string expression, string what)
    {
        if (root is null)
            _logger.LogDebug("workflow.template.no_context context={Context}", what);
        return ResolveExpression(root, expression);
    }

    // Path + filter chain against a root, or against nothing (root null = the
    // namespace is absent or the step unknown). Public so ConditionEvaluator
    // resolves a reference exactly the way a payload does — two grammars would let
    // an author write a condition that reads correctly and evaluates against
    // nothing.
    public static JsonElement? ResolveExpression(JsonElement? root, string expression)
    {
        var parsed = ParseExpression(expression);
        var resolved = root is { } r ? TryResolvePath(r, parsed.Path) : null;
        return ApplyFilters(resolved, parsed.Filters);
    }

    // Walks a dotted/bracketed path. Public so ConditionEvaluator uses the exact
    // same grammar — a path that resolves in a payload must resolve identically in
    // an edge condition, or the two would disagree about the same workflow.
    public static JsonElement? TryResolvePath(JsonElement root, string path)
    {
        var segments = ParsePath(path);
        // null = the path is malformed. Distinct from an empty list, which means
        // "no path at all" and legitimately resolves to the root — conflating the
        // two made `list[0` (unbalanced) return the entire step output.
        if (segments is null) return null;

        var current = root;
        foreach (var segment in segments)
        {
            if (segment.Index is { } index)
            {
                if (current.ValueKind != JsonValueKind.Array) return null;
                if (index < 0 || index >= current.GetArrayLength()) return null;
                current = current[index];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object) return null;
                if (!current.TryGetProperty(segment.Name!, out var next)) return null;
                current = next;
            }
        }
        return current;
    }

    private readonly record struct PathSegment(string? Name, int? Index);

    // ".devices[0].output" -> [devices, 0, output]. `['key']` / `["key"]` name a
    // property whose key is not an identifier (a hostname with dots, say), same as
    // the oracle.
    //
    // Returns null when the path is malformed, and an empty list when there is no
    // path (which resolves to the root). Those must stay distinguishable: a
    // malformed path that returned "no segments" would resolve to the entire
    // referenced object, so `{{ steps.s.output.list[0 }}` would quietly hand the
    // whole step output to the next node instead of failing to resolve.
    //
    // A list rather than an iterator because a ReadOnlySpan cannot live across a
    // `yield`, and because a partial parse must not be emitted at all.
    private static List<PathSegment>? ParsePath(string path)
    {
        var segments = new List<PathSegment>();
        if (string.IsNullOrWhiteSpace(path)) return segments;

        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                // `..` and a trailing `.` are malformed, not "skip a dot": the
                // contract says a malformed path is a resolution failure.
                if (i + 1 >= path.Length || path[i + 1] == '.') return null;
                i++;
                continue;
            }

            if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0) return null; // unbalanced
                var inner = path[(i + 1)..close].Trim();
                if (inner.Length >= 2
                    && ((inner[0] == '\'' && inner[^1] == '\'') || (inner[0] == '"' && inner[^1] == '"')))
                {
                    segments.Add(new PathSegment(inner[1..^1], null));
                }
                else
                {
                    if (!int.TryParse(inner, out var index)) return null;
                    segments.Add(new PathSegment(null, index));
                }
                i = close + 1;
                continue;
            }

            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            var name = path[start..i].Trim();
            if (name.Length == 0) return null;
            segments.Add(new PathSegment(name, null));
        }
        return segments;
    }

    // ── filters ──────────────────────────────────────────────────────────

    public readonly record struct FilterCall(string Name, IReadOnlyList<string> Args);

    public readonly record struct ParsedExpression(string Path, IReadOnlyList<FilterCall> Filters);

    // Splits what followed the namespace keyword — `(.path)?( | filter(args))*` —
    // into the path and the filter calls. A `|` inside quotes or parentheses is
    // part of an argument, not a separator, so `default('a|b')` keeps its pipe.
    public static ParsedExpression ParseExpression(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return new ParsedExpression(string.Empty, Array.Empty<FilterCall>());

        var parts = new List<string>();
        var sb = new StringBuilder();
        var quote = '\0';
        var paren = 0;
        foreach (var c in raw)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; sb.Append(c); continue; }
            if (c == '(') { paren++; sb.Append(c); continue; }
            if (c == ')') { if (paren > 0) paren--; sb.Append(c); continue; }
            if (c == '|' && paren == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        parts.Add(sb.ToString());

        var path = parts[0].Trim();
        var filters = new List<FilterCall>(parts.Count - 1);
        for (var p = 1; p < parts.Count; p++)
        {
            var token = parts[p].Trim();
            if (token.Length == 0) continue;
            var lp = token.IndexOf('(');
            if (lp < 0)
            {
                filters.Add(new FilterCall(token, Array.Empty<string>()));
                continue;
            }
            var rp = token.LastIndexOf(')');
            if (rp <= lp)
            {
                filters.Add(new FilterCall(token, Array.Empty<string>()));
                continue;
            }
            filters.Add(new FilterCall(token[..lp].Trim(), SplitArgs(token.Substring(lp + 1, rp - lp - 1))));
        }
        return new ParsedExpression(path, filters);
    }

    private static List<string> SplitArgs(string raw)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var quote = '\0';
        foreach (var c in raw)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; sb.Append(c); continue; }
            if (c == ',') { parts.Add(Unquote(sb.ToString().Trim())); sb.Clear(); continue; }
            sb.Append(c);
        }
        var last = sb.ToString().Trim();
        if (last.Length > 0 || parts.Count > 0) parts.Add(Unquote(last));
        return parts;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"'))
            ? s[1..^1]
            : s;

    // Runs the chain. `resolved` is null when the reference did not resolve; only
    // `default` handles that, every other filter then yields null so the caller
    // keeps the literal template. Unknown filters are ignored, per the contract —
    // the residual scan is what flags a typo, not the resolver.
    public static JsonElement? ApplyFilters(JsonElement? resolved, IReadOnlyList<FilterCall> filters)
    {
        var value = resolved;
        foreach (var f in filters)
        {
            if (string.Equals(f.Name, "default", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasValue(value))
                    value = StringElement(f.Args.Count > 0 ? f.Args[0] : string.Empty);
                continue;
            }

            if (!HasValue(value)) return null;

            value = f.Name.ToLowerInvariant() switch
            {
                "trim" => OnString(value!.Value, s => s.Trim()),
                "upper" => OnString(value!.Value, s => s.ToUpperInvariant()),
                "lower" => OnString(value!.Value, s => s.ToLowerInvariant()),
                "truncate" => OnString(value!.Value, s => Truncate(s, ParseInt(f.Args, 0, 200))),
                "json" => StringElement(value!.Value.GetRawText()),
                "strip_ansi" => OnString(value!.Value, StripAnsi),
                "strip" => OnString(value!.Value, s => C0Controls().Replace(StripAnsi(s), string.Empty).Trim()),
                _ => value,
            };
        }
        return value;
    }

    // What `default` stands in for: an unresolved reference, a null, or an empty
    // string — the three shapes of "nothing there" a step output produces. Same
    // rule as the oracle so `{{ steps.x.output.err | default('none') }}` reads the
    // same on both engines.
    private static bool HasValue(JsonElement? el) =>
        el is { } v
        && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        && !(v.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.GetString()));

    // The string filters act on strings; anything else passes through unchanged
    // (templates/SPEC.md §4). Upper-casing a number is not a thing.
    private static JsonElement OnString(JsonElement value, Func<string, string> fn) =>
        value.ValueKind == JsonValueKind.String
            ? StringElement(fn(value.GetString() ?? string.Empty))
            : value;

    private static JsonElement StringElement(string s) => JsonSerializer.SerializeToElement(s);

    private static int ParseInt(IReadOnlyList<string> args, int index, int fallback) =>
        index < args.Count && int.TryParse(args[index], out var n) ? n : fallback;

    private static string Truncate(string s, int max) =>
        max <= 0 ? string.Empty : (s.Length <= max ? s : s[..max]);

    public static string StripAnsi(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = AnsiCsi().Replace(s, string.Empty);
        s = AnsiOsc().Replace(s, string.Empty);
        return AnsiOther().Replace(s, string.Empty);
    }

    // ── residual scan (templates/SPEC.md §8) ─────────────────────────────

    // Any `{{ … }}` still standing after resolution. Deliberately as permissive as
    // the oracle's: the four namespaces are not the only way to write a reference
    // wrong, and a payload holding literal braces the author did not mean is a
    // mistake worth naming either way.
    [GeneratedRegex(@"\{\{\s*[^}]+\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex ResidualRegex();

    // One unresolved reference and where it sits, as a dotted JSON path
    // (`$.document.sections[0].markdown`) so the author can jump to the field.
    public sealed record UnresolvedTemplate(string Location, string TemplateText);

    // Walks an already-resolved payload and collects every string value that still
    // carries a `{{ … }}`. The engine runs this before dispatching a step: a
    // residual means the author referenced a step or a path that does not exist, and
    // the handler would otherwise emit fiction — a report with literal
    // `{{ steps.audit.output.rows[0] }}` cells, an SSH command with a brace in it.
    // Failing the step with `unresolved_template` and naming the field is the
    // contract's rule and the honest outcome.
    public static IReadOnlyList<UnresolvedTemplate> FindUnresolvedTemplates(JsonElement payload)
    {
        var found = new List<UnresolvedTemplate>();
        WalkForResiduals(payload, "$", found);
        return found;
    }

    private static void WalkForResiduals(JsonElement el, string path, List<UnresolvedTemplate> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = el.GetString();
                if (string.IsNullOrEmpty(s)) return;
                var match = ResidualRegex().Match(s);
                if (match.Success) sink.Add(new UnresolvedTemplate(path, match.Value.Trim()));
                return;
            }
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject()) WalkForResiduals(p.Value, $"{path}.{p.Name}", sink);
                return;
            case JsonValueKind.Array:
            {
                var i = 0;
                foreach (var item in el.EnumerateArray()) WalkForResiduals(item, $"{path}[{i++}]", sink);
                return;
            }
            default:
                return;
        }
    }

    // The step error for a residual scan: the offending fields, capped so a
    // pathological payload cannot produce a screen of error text.
    public static string DescribeUnresolved(IReadOnlyList<UnresolvedTemplate> unresolved)
    {
        var lines = unresolved.Take(10).Select(u => $"  - {u.Location}: {u.TemplateText}").ToList();
        if (unresolved.Count > lines.Count)
            lines.Add($"  … and {unresolved.Count - lines.Count} more");
        return "unresolved template references — this step references step outputs, run metadata or "
             + "device fields that do not exist or did not resolve. Fix the node's config_overrides so "
             + "every `{{ ... }}` points at something real:" + Environment.NewLine
             + string.Join(Environment.NewLine, lines);
    }

    // ── per-device scoping ───────────────────────────────────────────────

    // For a `per_device` consumer: every completed output that has the fan-out
    // aggregate shape `{ devices: [ { device_id, output } ] }` is replaced by the
    // current device's own `output`, so `{{ steps.show.output.stdout }}` reads this
    // device's stdout rather than the envelope (templates/SPEC.md §6). Outputs of
    // any other shape pass through. A producer with no entry for this device is
    // dropped, which makes a reference to it unresolved — the contract's rule, and
    // the honest one: the device has no output there, and handing it a sibling's
    // would be worse than a residual.
    public static IReadOnlyDictionary<string, StepResult> ScopeOutputsToDevice(
        IReadOnlyDictionary<string, StepResult> outputs, Guid deviceId)
    {
        var scoped = new Dictionary<string, StepResult>(outputs.Count, StringComparer.Ordinal);
        var id = deviceId.ToString();
        foreach (var (node, step) in outputs)
        {
            if (!IsAggregate(step.Output, out var devices))
            {
                scoped[node] = step;
                continue;
            }
            if (TryExtractDeviceOutput(devices, id, out var own))
                scoped[node] = new StepResult(own);
        }
        return scoped;
    }

    // The aggregate shape is an object with a non-empty `devices` array whose
    // entries carry a string `device_id`. Requiring the id keeps an ordinary once
    // step whose output happens to have a `devices` list (an inventory query, say)
    // from being mistaken for a fan-out.
    private static bool IsAggregate(JsonElement output, out JsonElement devices)
    {
        devices = default;
        if (output.ValueKind != JsonValueKind.Object) return false;
        if (!output.TryGetProperty("devices", out devices) || devices.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in devices.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("device_id", out var did)
                && did.ValueKind == JsonValueKind.String)
                return true;
        }
        return false;
    }

    private static bool TryExtractDeviceOutput(JsonElement devices, string deviceId, out JsonElement output)
    {
        output = default;
        foreach (var entry in devices.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("device_id", out var did)
                || did.ValueKind != JsonValueKind.String
                || !string.Equals(did.GetString(), deviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.TryGetProperty("output", out var own)) continue;
            output = own;
            return true;
        }
        return false;
    }

    // String form of a resolved value for inline splicing. Objects and arrays
    // render as their raw JSON so an inline reference to a structure is at least
    // inspectable instead of "System.Text.Json.JsonElement".
    public static string Stringify(JsonElement? value, string fallback)
    {
        if (value is not { } v) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => v.GetRawText(),
            _ => v.ToString(),
        };
    }

    // The device view a template may address. Deliberately a projection, not the
    // entity: a step payload must not be able to reach the credential id.
    public static JsonElement DeviceView(Data.Models.Device d) =>
        JsonSerializer.SerializeToElement(new
        {
            id = d.DeviceId,
            name = d.DeviceName,
            ip = d.IpAddress,
            platform = d.Platform,
            vendor = d.Vendor,
            os_version = d.OsVersion,
            site = d.Site,
            role = d.Role,
            status = d.Status,
            external_id = d.ExternalId,
            properties = d.Properties,
        });
}
