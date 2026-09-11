using System.Text.RegularExpressions;
using nashira_backend.Services.Settings;

namespace nashira_backend.Services.Ssh;

public sealed record CommandIntent(string Command, string Intent);

public sealed record CommandPolicyDecision(bool Allowed, IReadOnlyList<CommandIntent> Intents, IReadOnlyList<string> Blocked);

// Per-command governance for SSH execution (the flow-weaver PolicyEvaluator equivalent):
// classifies each command as read / mutation / destructive and blocks destructive commands
// unless an admin opts in (Ssh:AllowDestructiveCommands). This is the safety net beyond the
// tool-level single_confirm gate on device_connect.
public interface ISshCommandPolicy
{
    string Classify(string command);
    CommandPolicyDecision Evaluate(IReadOnlyList<string> commands);
}

public sealed class SshCommandPolicy : ISshCommandPolicy
{
    public const string Read = "read";
    public const string Mutation = "mutation";
    public const string Destructive = "destructive";

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Severity order: destructive wins, then mutation, then read. Unknown -> mutation (conservative).
    private static readonly Regex[] DestructivePatterns =
    [
        R(@"\b(reload|reboot)\b"),
        R(@"write\s+erase|erase\s+(startup-config|nvram:?|flash:?|config)"),
        R(@"^\s*(erase|delete|format)\b"),
        R(@"clear\s+config"),
        R(@"factory[\s-]?reset|\bzeroize\b"),
    ];

    private static readonly Regex[] MutationPatterns =
    [
        R(@"^\s*conf(igure|t)?\b|^\s*config\b"),
        R(@"^\s*set\b"),
        R(@"copy\s+running-config\s+startup-config|write\s+mem(ory)?|^\s*wr\b|^\s*write\b"),
        R(@"\bcommit\b"),
        R(@"^\s*(no\s+)?shutdown\b"),
    ];

    private static readonly Regex[] ReadPatterns =
    [
        R(@"^\s*(show|display|sh\b|disp\b|get|dir|ping|traceroute|monitor|more|cat)\b"),
    ];

    private readonly AppSettingsProvider _settings;

    public SshCommandPolicy(AppSettingsProvider settings) => _settings = settings;

    // Read per call, not once at construction: this is a singleton, so a value captured
    // in the constructor could only be changed by restarting the process — and a safety
    // gate that needs a redeploy to open or close is one nobody adjusts when it matters.
    private bool DestructiveAllowed =>
        _settings.GetBool("Ssh:AllowDestructiveCommands", false);

    public string Classify(string command)
    {
        var c = command ?? string.Empty;
        if (DestructivePatterns.Any(p => p.IsMatch(c))) return Destructive;
        if (MutationPatterns.Any(p => p.IsMatch(c))) return Mutation;
        if (ReadPatterns.Any(p => p.IsMatch(c))) return Read;
        return Mutation; // unknown command: treat as a mutation, not a read
    }

    public CommandPolicyDecision Evaluate(IReadOnlyList<string> commands)
    {
        var intents = commands.Select(c => new CommandIntent(c, Classify(c))).ToList();
        IReadOnlyList<string> blocked = DestructiveAllowed
            ? []
            : intents.Where(i => i.Intent == Destructive).Select(i => i.Command).ToList();
        return new CommandPolicyDecision(blocked.Count == 0, intents, blocked);
    }
}
