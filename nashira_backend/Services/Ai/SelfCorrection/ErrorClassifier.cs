using System.Text.RegularExpressions;

namespace nashira_backend.Services.Ai.SelfCorrection;

// Classifies a tool error message into a coarse category (ported from
// nashira_agent self_correction.ERROR_PATTERNS). First matching category wins.
public static class ErrorClassifier
{
    public const string Timeout = "timeout";
    public const string Validation = "validation";
    public const string Auth = "auth";
    public const string Connection = "connection";
    public const string FieldError = "field_error";
    public const string Duplicate = "duplicate";
    public const string NotFound = "not_found";
    public const string Unknown = "unknown";

    private static readonly (string Category, Regex[] Patterns)[] Rules = Build();

    public static string Classify(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage)) return Unknown;
        foreach (var (category, patterns) in Rules)
            foreach (var pattern in patterns)
                if (pattern.IsMatch(errorMessage))
                    return category;
        return Unknown;
    }

    private static (string, Regex[])[] Build()
    {
        static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        return
        [
            (Timeout, [R(@"timed?\s*out"), R("timeout"), R(@"deadline\s+exceeded")]),
            (Validation, [R(@"validation\s+(failed|error)"), R(@"invalid\s+(field|value|type|format|parameter)"),
                          R(@"must\s+be\s+a\s+string"), R(@"required\s+field"), R(@"missing\s+required"), R(@"is\s+required")]),
            (Auth, [R(@"unauthorized|forbidden"), R(@"\b(401|403)\b"), R(@"authentication\s+failed"),
                    R("credential"), R(@"token\s+(expired|invalid)")]),
            (Connection, [R(@"connection\s+(refused|reset|error)"), R(@"could\s+not\s+connect"),
                          R(@"network\s+(error|unreachable)"), R("ECONNREFUSED|ECONNRESET"), R(@"\b50[023]\b")]),
            (FieldError, [R(@"field\s+'([^']+)'\s*:"), R(@"unexpected\s+keyword\s+argument"), R(@"got\s+an\s+unexpected")]),
            (Duplicate, [R("duplicate"), R(@"already\s+exists"), R(@"unique\s+constraint")]),
            (NotFound, [R(@"not\s+found"), R(@"\b404\b"), R(@"does\s+not\s+exist")]),
        ];
    }
}
