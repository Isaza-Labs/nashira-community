using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Net;

namespace nashira_backend.Services.Ai.Tools;

// Shapes a handler's failure for the dispatcher. A handler that turns a DomainException into an
// `{error}` result loses the exception's code, and the dispatcher can only tell a failure that no
// change of arguments can fix (a denied secret lookup, a refused redirect, an outbound failure that
// could not be classified) from an ordinary one by that code. Those come back as `error_code`, which
// the dispatcher treats as final. Every other failure keeps the plain `{error}` shape, so
// corrections still apply to it.
public static class ToolFailure
{
    // The one list of final codes. Both this class and the dispatcher read it, so what a handler
    // reports as final and what the dispatcher refuses to correct and re-run cannot drift apart.
    // Running such a call again would repeat the original request or the denied lookup.
    private static readonly HashSet<string> FinalCodes = new(StringComparer.Ordinal)
    {
        SecretResolver.AccessDeniedCode,
        RedirectRefusedException.Code,
        RedirectSearch.IncompleteCode,
    };

    public static bool IsFinalCode(string? code) => code is not null && FinalCodes.Contains(code);

    public static bool IsFinal(DomainException error) => IsFinalCode(error.Code);

    public static JsonElement From(DomainException error) => IsFinal(error)
        ? JsonSerializer.SerializeToElement(new { error = error.Message, error_code = error.Code })
        : JsonSerializer.SerializeToElement(new { error = error.Message });
}
