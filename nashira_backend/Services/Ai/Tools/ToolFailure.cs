using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Net;

namespace nashira_backend.Services.Ai.Tools;

// Shapes a handler's failure for the dispatcher. A handler that turns a DomainException into an
// `{error}` result loses the exception's code, and the dispatcher can only tell a failure that no
// change of arguments can fix (a denied secret lookup, a refused redirect) from an ordinary one by
// that code. Those two come back as `error_code`, which the dispatcher treats as final. Every other
// failure keeps the plain `{error}` shape, so corrections still apply to it.
public static class ToolFailure
{
    public static bool IsFinal(DomainException error) =>
        error.Code is SecretResolver.AccessDeniedCode or RedirectRefusedException.Code;

    public static JsonElement From(DomainException error) => IsFinal(error)
        ? JsonSerializer.SerializeToElement(new { error = error.Message, error_code = error.Code })
        : JsonSerializer.SerializeToElement(new { error = error.Message });
}
