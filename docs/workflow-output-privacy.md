# Workflow output privacy

`config_overrides.redact_output` accepts only a JSON boolean. `true` withholds
output, logs and error details. `false` (the default) preserves them **only for
steps that did not receive resolved stored-secret material** (the caller's own session
reference does not count; see below).

A withheld failure reports the error code `output_redacted`, except for decisions the
host makes itself before or instead of running any step code: `secret_access_denied`
and the Python sandbox codes `unsandboxed_admin_required`, `sandbox_not_configured`,
`sandbox_invalid` and `runtime_unavailable`. These keep their code, so a denial can be
told apart from a failure, while the output, logs and error text stay withheld; none
of them is retried. A code that a handler builds from a script exit, a remote response
or a status line (`http_*`, `redirect_refused`, `script_error`, `timeout`) is always
replaced, so a withheld step reports `output_redacted` for those failures.

Successful resolution of stored material through `ISecretResolver` marks the current
execution as sensitive, including credential usernames/client IDs stored in clear. So
does an OAuth access token that was derived from stored secrets or whose provenance is
unknown. The entire output of a marked step is replaced by `[redacted]` before run
persistence or downstream template resolution. The same applies to Python's direct
handler path. Reverse/base64, split strings, numeric encodings, property names, printed
text and exceptions do not bypass this boundary. Non-secret sibling steps retain their
output when `redact_output:false`; the flag never authorizes secret disclosure.

The caller's own session reference (`${secret:session:current:jwt}`, which the built-in
`na_*` specs use) is the exception. It lends the caller's bearer to this backend and
reads nothing from the secrets tables, so it does not mark the execution: a step that
resolves only a session reference keeps the output rules it had before, and its output
stays visible with `redact_output:false`. A step that also resolves stored material is
marked, and withheld, like any other.

Arbitrary code with plaintext can encode it in any return field. Nashira cannot
prove that a particular sibling field from that same execution is public. Split
public diagnostics into a separate step that never receives the secret. This is
a deliberate limit: same-execution public fields are withheld too, and downstream
steps cannot consume the withheld data. Unresolved markers remain literal for
authorized callers; unauthorized resolution still stops the step.

This governs workflow result payloads, not arbitrary network/filesystem side
effects or timing/status covert channels. Keep the OS sandbox, secret Admin
authorization and UrlGuard enabled. Existing handler paths that use credentials
directly rather than `ISecretResolver` retain their own security controls; this
change does not redesign those credential-use APIs.

## Upgrade note: OAuth integrations

An integration using the OAuth2 client-credentials method obtains its access token from
the client ID, client secret and token URL in its settings. If any of them is a
stored-secret reference (`${secret:...}`), the token is derived from stored secrets:
reusing it, whether it is obtained now or was already held in memory, requires an
Admin, and a step that used it has its output withheld like any other step that
received resolved secret material. Operators and Viewers get `secret_access_denied`,
which is final and not retried. Integrations whose OAuth settings are literal values
or come from a linked credential keep their authorization; their token requests no
longer follow redirects, so a token endpoint that redirects fails with `redirect_refused`
and has to be configured with its final URL.
