# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).


## 2026-09-08 — One image, the deployment it was sold, and nothing else

**Nashira now ships as a set of capabilities an operator selects, instead of a product
that installs all of itself everywhere.** `NASHIRA_MODULES` in `deploy/.env` lists what a
deployment runs — `chat`, `ai-studio`, `automation`, `fleet`, `integrations`,
`communications`, `secrets`, `git`, `knowledge`, `artifacts`, `governance`,
`observability` — with `core` always present. Leaving the variable out keeps every
capability enabled, so existing deployments are unchanged by this release.

The selection is resolved once, before anything else is composed, and an invalid one
stops the process rather than starting a half-configured instance: an unknown module, a
missing dependency (`ai-studio` requires `integrations`, `chat` requires `ai-studio`,
and `communications` requires `chat`), or an explicitly empty list all fail at boot
with every error reported at once. Absence and emptiness are deliberately different
answers, and Compose passes the variable through without collapsing them.

A disabled capability is absent rather than broken. Its endpoints stay routed and answer
`503 module_disabled` — refused before model binding, so nothing runs and no mutation is
audited. Its background workers do not start, so nothing dials out to Slack or claims a
job on behalf of a capability nobody enabled. Its agent tools never enter the registry:
the model is not told about work it cannot do, which is what stops it reaching for it.
Its snippet types are not runnable, its seeders do not write, and the built-in skills and
API specs that describe it are left out of the system prompt and the agent's catalogue.

The console asks the backend what this installation runs — `GET /api/modules`, after
signing in — so one frontend image serves every combination. Navigation is the
intersection of module, role and stored permission, in that order: a permission cannot
put back a capability the deployment does not have. The sidebar, command palette, section
tabs, admin hub, docs and route guard all read the same answer, and opening a disabled
page by URL shows *Module unavailable for this deployment* without mounting the page or
firing its requests. When the manifest cannot be read the console fails closed to core
and says so, with a retry — it never assumes everything is on.

Nothing is deleted. Turning a capability off leaves its rows exactly where they are;
retention skips the tables of a module that is off, and turning it back on runs that
module's idempotent seeders over the data that was kept. Rolling back is the same
operation as rolling forward: restore the value, recreate the container.

Two surfaces adapt rather than disappear, because both belong to core: Overview asks only
for the capabilities that are on — a missing producer leaves the card absent, never a
zero or an error — and Settings offers only the controls whose read sites this deployment
reaches. Users keeps creating accounts without Communications, and says before you save
that the temporary password will not be emailed.

Every activatable component is classified in one table per kind — endpoints, agent tools,
snippet handlers, hosted services, seeders, built-in content, settings, navigation
destinations and documentation — and each has a test that fails when something new
arrives unclassified. `check:nav` refuses a route nobody classified, and `check:modules`
refuses a module key the two sides disagree about.


## 2026-09-08 — Navigation access by role, with exceptions where people need them

**Administrators can now decide which Nashira areas each role sees, without turning
navigation into another hard-coded role check.** The new **Admin → Navigation access**
screen presents every destination in the same sections as the product sidebar and lets
an administrator show, hide or restore a whole section or one page at a time for
`viewer`, `operator` and `admin`.

Roles are the baseline, not the limit of the model. A second scope adds per-user
exceptions for the cases where two operators do not need the same workspace. Resolution
is deliberately predictable: a user exception wins over the role setting, the role
setting wins over the system default, and the page's existing minimum role still wins
over all three. Making an admin page visible to a viewer therefore does not promote the
viewer or grant its API permissions.

The effective result is applied to every way into a page: the sidebar, section tabs,
command palette, keyboard shortcuts, account menu, admin tool cards and direct URLs.
The logo also opens the first area the user can actually reach instead of always sending
them to Chat. The Navigation access page itself cannot be hidden, so administrators
cannot strand themselves outside the control that restores a section.

Visibility settings are persisted independently from agent tool permissions. The new
authenticated endpoint returns only the current user's effective navigation overrides;
role and user management endpoints remain admin-only, rate-limited and audited with
their complete before/after state. API authorization continues to be enforced by the
existing Viewer, Operator and Admin policies — navigation visibility is an interface
restriction, never a replacement security boundary.


## 2026-09-07 — The diagrams nobody could see, and two escapes too many

**`mermaid` has been a dependency of the frontend since snippets grew a
`logic_diagram_mermaid` field, and nothing ever imported it.** The API required the
diagram on `python_snippet`, `python`, `transform` and `jmespath`, refused a create
without one, and `Skills/mermaid.md` told the agent "the UI renders it" — in the edit
dialog, in the workflow panel. Neither surface existed. The field was a textarea holding
arrow syntax, so the one thing a logic diagram is for (opening a step six months later
and seeing what it does without reading the Python) never happened, and a typo in an
edge survived until somebody pasted the source into a different tool.

`ui/Mermaid.svelte` is that renderer. It imports mermaid dynamically — the library
carries its own layout engine and is by a wide margin the heaviest thing in the tree, so
it stays out of the entry chunk and loads when a diagram is actually on screen. It
repaints on the mode toggle (`mermaid.initialize` is global, so the theme is re-applied
per render), reports what mermaid rejected instead of drawing nothing, and debounces at
180 ms because both callers change the source far faster than a layout pass completes.

It is wired into two places:

- **The snippet editor**, drawn live beside the source. Which is also the dialog the
  workflow confirmation card opens on a step, so it is how a node's logic gets read.
  The field now says which types the API will refuse without one.
- **Chat**, for any ` ```mermaid ` fence. The agent emits these constantly — it writes
  the diagram out before asking to create the snippet — and rendered as code, the one
  artefact whose entire purpose is being readable at a glance was the least readable
  thing in the answer. `markdown.ts` leaves a placeholder and `ChatMarkdown` mounts the
  component onto it, since that module is a pure string renderer and mermaid is async
  and measures the DOM.

`Skills/mermaid.md` claimed the workflow canvas rendered these and told the agent to
update them through `fw_snippets:update_snippet` — FlowWeaver's tool name, which does
not exist here. It now names the surfaces that exist and the route that exists
(`snippets_update` via `execute_operation`), and says the thing that actually makes a
diagram go stale: that update MERGES, so omitting the field keeps the old diagram
rather than clearing it, and nothing fails.

### Every code block in chat was escaped twice

Found on the way in, and worse than the missing diagrams. `renderMarkdownSafe` escapes
the whole document before handing it to marked — that is what makes model output safe —
and marked's default code renderer then escapes it *again*, unconditionally. So
`if x < 5 & y:` reached the bubble as the literal text `if x &lt; 5 &amp; y:`, and the
block's Copy button handed that to whoever pasted it into a device. Inline code was the
same, which is where every device name, interface and CLI fragment in an answer lives:
`Gi0/1 <-> Gi0/2` mid-sentence read as `Gi0/1 &lt;-&gt; Gi0/2`.

Both renderers now emit the pre-escaped text as it is. A `<` cannot open a tag once it
is already `&lt;`, so nothing is weakened — the fence info string is still filtered down
to the characters a class name may hold.

### Selecting text left no mark

Skeleton ships one `::selection` rule — primary-500 at 50%, both modes. In light mode
that is fine. In dark mode, the one this app opens in, primary-500 is the azul #1D4E89,
and half of a dark navy over a near-black surface is a shade of the surface: dragging
across text looked identical to not dragging. Dark mode now selects in the turquesa,
which is already this palette's dark-mode action colour.

The user's own chat bubble is the case neither wash reaches, because there the ground IS
the primary colour. `.selection-on-fill` gives those surfaces a neutral highlight — white
over the azul, near-black over the turquesa — with the text colour flipped to match.

Each chat turn also carries a small Copy button now, hidden until the turn is hovered.
It copies `message.content` — the markdown the model emitted, not the rendered DOM — so
what lands in the clipboard is what the next prompt needs, and hand-selecting the bubble
no longer drags in the tool-call chips above it.

### Theme export writes a file

**Themes → Export JSON copied to the clipboard**, which is one keystroke away from
losing the whole theme, and on the LAN deployments this runs on (`http://<ip>:3006`)
`navigator.clipboard` does not exist at all — so half the time Export ended in the
paste-it-back-yourself fallback. It now downloads `<name>.theme.json`. Import still
accepts a paste, since that is how a theme arrives from a chat message, and takes a file
as well: an export nothing can read back is not a round trip.


## 2026-09-07 — Gemini's thought signatures, and the tool call that dies on the second turn

**Every tool-using conversation on a Gemini model failed the moment a tool result went
back.** The first turn worked, the tool ran, and the follow-up request came back 400:

```
Function call is missing a thought_signature in functionCall parts. This is required
for tools to work correctly [...] Additional data, function call
`default_api:list_integrations`, position 6.
```

A **thought signature** is an opaque, encrypted handle Gemini attaches to a `functionCall`
part — its own reasoning behind that call, sealed so the next request can restore it
without the API having to keep server-side state. On 2.5 and later, returning it is not
advisory: the API validates `functionCall` parts strictly and rejects the whole request
if one comes back bare. `GeminiProvider` read `functionCall` and nothing else off the
part, so the signature was dropped on the way in and could not be sent on the way out.

The subtle part is *where* it lives. `thoughtSignature` is a **sibling** of `functionCall`
inside the part, not a field on it — so `ToToolCall` now takes the whole part rather than
the `functionCall` it used to be handed, and `MapMessage` writes the signature back
alongside the call rather than inside it. `ToolCallResult` carries it across the runner's
tool loop, where the objects survive in memory; the other three providers leave it null.

An unsigned call goes back with **no key at all**, which is the case that decides the
shape. Gemini signs the first call of a parallel group and leaves the rest bare, and a
non-thinking model signs nothing — inventing a placeholder for those is as fatal as
dropping a real one. Preserving per-part, verbatim, is the only mapping that is right in
all three cases. Nothing reads the value.

Replayed history is unaffected: conversations rebuilt from the database carry text only,
never `functionCall` parts, so there is no signature to miss.


## 2026-09-06 — ServiceNow, and the 401 that is not a bad password

**New integration bundle: `integrations/servicenow/`** — an OpenAPI 3.1 spec (15
operations across incidents, change requests, the CMDB and the directory), a scoped
prompt skill, and `register.ps1`, which creates the credential and posts the pair to
`/api/integrations/bundle` in one transaction.

Not in `nashira_backend/Specs/`, and that took a minute to settle. `BuiltinSpecSeeder`
stamps every file there with Nashira's own base URL and the *caller's session token* —
correct for the thirteen documents describing our own API, actively wrong for an
external system. And every `Skills/*.md` is concatenated into every conversation's
prompt, whereas a skill attached to an integration reaches it only when that integration
is in scope. So external systems live in `integrations/<name>/` and are attached, never
seeded.

**The instance was refusing correct credentials and nothing said why.** Basic auth to
`/api/now/table/...` answered:

```json
{"error":{"message":"User is not authenticated","detail":"Required to provide Auth information"}}
```

with the same account whose UI login worked, on an account that was active, unlocked,
not MFA-enrolled and not password-reset-pending. The cause was three properties away:

```
glide.authenticate.basic_auth.restriction.active  = true
glide.authenticate.basic_auth.restriction.enforce = true
```

**Basic Auth Restriction**, enforced by default on instances created since 2026. It
refuses Basic for any user without a row in `sys_user_basic_auth_exception`, and the
refusal is byte-identical to the one a wrong password gets. Working account, correct
secret, every API call refused, and the one signal pointing at the truth — the UI login
still succeeding — reads as evidence the credentials are fine. So the bundle
authenticates with OAuth 2.0 client credentials, which the restriction does not touch,
and both the skill and the README name this failure by its symptom rather than its
cause, because the symptom is what somebody will be holding.

**`IntegrationTypeProfile` now knows `servicenow`.** This is the NetBox trap again, in a
system likelier to hit it: a ServiceNow instance answers **200** to an anonymous GET of
its root — that is the login page — so the default "probe the base URL" reports healthy
for an instance whose every API call is refused. The profile supplies
`/api/now/table/sys_user?sysparm_limit=1` instead: cheap, authenticated, present
regardless of plugins. Its declared method is `bearer` so `AuthMismatch` contradicts
nothing — ServiceNow takes `Bearer` for an OAuth token and Basic for a password, and
flagging either would send an operator who configured OAuth correctly to go and break
it.

**And the bundles are now tested, because nothing else looks at them.** They are not
compiled and not seeded, so a malformed spec surfaces only when someone runs the
register script against a live instance — or does not surface at all, and leaves an
integration whose action catalogue is empty. `IntegrationBundleSpecTests` walks
`integrations/*/`: the spec parses to at least one operation, every operation has a
summary (one without is listed by id alone next to twenty that describe themselves, so
it is the one never called), a `skill.md` ships beside every `spec.yaml`, the directory
name is a usable `api` identifier, and no operationId collides with anything — including
the built-ins, because `YamlSpecIndex` keys the whole catalog with
`GroupBy(OperationId).First()` and the loser of a collision is not lower-priority, it is
absent.

Three things the spec deliberately refuses to expose: **DELETE** anywhere (ServiceNow
records are closed or cancelled; deleting an incident destroys the audit trail that was
the reason for opening it), **PUT** (it replaces the record, so an omitted field is
cleared — that has quietly wiped assignment and work notes on people who meant to move a
state), and **generic writes** (reads take any table name so nothing is blocked on
nobody having enumerated it, but a mutation the governance tier has to classify should
read as "raise a change request", not "PATCH some row in some table").


## 2026-09-03 — Two tools took Gemini's whole tool set down, and a bad Relay string logged forever

**`create_workflow` and `update_workflow` declared arrays with no `items`.**

```json
"nodes":{"type":"array","description":"workflow.v1 nodes (JSON array)"},
"edges":{"type":"array","description":"workflow.v1 edges (JSON array)"},
```

OpenAI and Anthropic accept that. Gemini requires `items` on every ARRAY and rejects the
*entire request*, so two under-specified properties in two of a hundred and ten tools meant no
Gemini turn could run at all — and the 400 names the offender only by index:

```
GenerateContentRequest.tools[0].function_declarations[87].parameters.properties[edges].items:
  missing field.
```

which is not a thing anyone can look up. Both now declare `"items":{"type":"object"}`, which is
what they hold. (A property-less OBJECT is fine nested — the same request carried
`input_schema` and `metadata` as bare objects and Gemini did not object; the "OBJECT with no
properties is rejected" rule applies to the top-level `parameters`.)

**And a net, because the next one will be written the same way.** `SanitizeSchema` now fills in
`items: {type: OBJECT}` for any ARRAY that reaches it without one. A tool written against
OpenAI or Anthropic passes review and only fails here, at which point it takes down every
Gemini turn rather than its own — one omission should not cost the whole tool set. Declaring
`items` at the tool is still the correct fix; four tests cover the net, including that it never
overwrites a declared `items` and never adds one to a non-array.

**The Teams relay stopped logging the same failure every 30 seconds.**

```
teams.relay.error channel=bb4c9791… attempt=7
System.ArgumentException: The value for the connection string parameter
'https://flowweaver.servicebus.windows.net/teams-bot' is empty or missing.
```

That is the Hybrid Connection **URL** stored where the **connection string** goes —
`TeamsRelayConnectionString.Validate` already rejects it at save time, so the row predates that
check. The listener retried it forever with capped backoff, which is right for a dropped socket
and wrong for a string that will never parse: the real cause scrolls away and the log reads
like a flaky network instead of like a field somebody has to fix.

A connection string that fails to parse now logs once, at Error, saying what to do, and stops
that channel's listener. Stopping is safe because `ReconcileAsync` keys on the entry still
being in `_listeners`, so nothing restarts it — and when the string is corrected, reconcile
sees the stored value differ from the running one, drops the listener and starts a fresh one.
Recovery needs no restart. Everything else still retries.


## 2026-09-03 — An identity-linked Anthropic key needs a workspace id

Found the moment the error body started being surfaced. The 400 said exactly what it wanted:

```
anthropic-workspace-id is required when authenticating with an identity-linked API key;
send the id of the workspace this request acts in.
```

A plain API key is scoped to one workspace and carries it. An **identity-linked** key belongs to
a person who may have access to several, so the key alone does not say which workspace to scope
and bill the request to, and the API refuses to guess. `AnthropicProvider` sent `x-api-key` and
`anthropic-version` and nothing else.

The workspace id is a provider setting, so it lives in `config.workspace_id` — which the form
had just learned to edit — and the header goes out only when one is configured. That last part
is the constraint: a workspace-scoped key needs no header, and sending an empty one would turn
every other deployment's working provider into a 400.

The form's Config hint names the key when the provider type is `anthropic`, and the field
rejects the two things people reach for instead of the id — the workspace **name**, and the
whole Console **URL**. Both were accepted, saved, and then rejected by Anthropic one chat turn
later with `anthropic-workspace-id header must be a valid workspace ID`, which does not say
which of the two you did. A pasted URL is answered with the id extracted from it
(*"paste only wrkspc_01ABC"*); a name is answered with where the id actually lives. The
backend stays permissive — Anthropic is the authority on its own id format, and a future one
should not need a frontend release.

Five tests cover the header against a capturing handler (present, absent, blank, trimmed), and
the whole chain was verified on the wire — provider row through `LlmProviderFactory` to the
request — by pointing a provider's base URL at a local listener and reading what arrived:

```
x-api-key : sk-ant-fake
anthropic-version : 2023-06-01
anthropic-workspace-id : wrkspc_01TEST
```

with the last line correctly absent once `config` was cleared.

**This was not the alternation bug.** That one is real, was reproduced, and is fixed in the
entry below — a conversation carrying an empty assistant turn still gets a 400 from any
Anthropic key. It was simply not what this deployment was hitting. The 400 that hid both of
them was the same one, which is the argument for surfacing provider error bodies at all.


## 2026-09-03 — A conversation that asked for confirmation could never talk to Anthropic again

Reported as a 400 from `api.anthropic.com` on one conversation, with nothing to go on:

```
System.Net.Http.HttpRequestException: Response status code does not indicate success: 400 (Bad Request).
   at LlmHttp.PostAsync(...)
```

**Two bugs, and the first was hiding the second.**

**`LlmHttp.PostAsync` threw the diagnosis away.** Every non-retryable 4xx from every provider
went through `response.EnsureSuccessStatusCode()`, which reports the status line and discards
the body — and the body is the only part that says what was wrong. Providers are specific
(`"max_tokens: 64000 > 32000"`, `"messages: roles must alternate"`); what reached the operator
was a sentence that reads like a bad API key. The body is now parsed for the reason — the four
shapes differ but all put the text in `error.message`, except Ollama which puts it in `error`
— and surfaced as `anthropic returned HTTP 401: API key is invalid. (authentication_error)`,
with the full body logged at Warning for the request id.

**The 400 itself: an empty assistant turn collapsed the roles.** `AnthropicProvider.MapMessages`
drops a turn that ends up with no content blocks — correctly, an empty `content` array is a
400. But dropping one from the *middle* of the history leaves the turns on either side of it
adjacent, and two `user` turns in a row is the same 400 by a different name. The mapper opened
an assistant turn for the empty message, which flushed the user turn before it, then dropped
the empty turn on the next flush:

```
user("first question"), assistant(""), user("second question")   ->   [user, user]
```

An empty assistant message is not hypothetical. `AgentConversationRunner` persists `finalText`
verbatim:

```csharp
var assistantText = errored && finalText.Length == 0 ? "[error]" : finalText.ToString();
```

A turn that stops to ask for tool confirmation produces no text and is not an error, so `""` is
what gets stored. From the next message on, every turn in that conversation replays it and gets
a 400 — permanently, with no way for the user to tell why or to recover. The fix skips an
assistant message with no content and no tool calls *without opening a turn*, which merges the
two user turns instead of separating them. Nothing is lost, and it is honest: the assistant did
not say anything between them.

`GeminiProvider` was already immune — it builds the parts first, skips the message when there
are none, and merges consecutive same-role turns — so this aligns Anthropic with the shape
Gemini already had. OpenAI accepts consecutive same-role messages and needed nothing.

Four tests pin the alternation, including the two-confirmations case; they fail with
`roles must alternate, got [user, user]` against the old mapper.

Ruled out along the way, all verified against the current API: `temperature` is correctly
withheld from Sonnet 5 (a sampling parameter there is a 400, not a warning), `max_tokens: 64000`
is inside the model's 128K output ceiling, the 110 tool names are unique and all match
`^[a-zA-Z0-9_-]{1,64}$`, and no tool schema uses `$ref`/`oneOf`/`anyOf`/`allOf`.


## 2026-09-03 — `config` is editable from the provider form

`CreateAIProvider` and `UpdateAIProvider` had no `config` field, so the jsonb column could only
be set by editing the row in the database. Two things read it — `config.models` (the extra
model ids that join `default_model` in the chat's model picker) and `config.model_limits`
(`context_window` / `max_output_tokens`) — which meant the picker shipped able to offer exactly
one model per provider.

The form now has a **Config (JSON)** textarea, parsed as you type. Unknown keys are stored
untouched; the whole object is replaced on save, so `{}` clears it.

**It validates rather than accepting quietly.** A wrong shape here is not a runtime error, it is
silence: `AiModelsController` ignores a `models` that is not an array of strings, and
`ModelLimits.FromConfig` returns null for a malformed `model_limits`. The admin would see a
saved provider whose extra models never appear in the picker, and no explanation anywhere.
`config.models must be an array of model ids` is the only place that can say why.


## 2026-09-03 — The chat can be pointed at a provider, and remembers which one

Configuring several providers had no effect on the chat. Every turn resolved the same way:

```csharp
// Resolve the tenant's default (first enabled) provider — used by the chat
// loop until per-agent provider selection lands.
(llm, model) = await _providerFactory.ResolveDefaultAsync(null, streamCt);
```

`ResolveDefaultAsync` orders by `CreatedAt` and takes the first row, so a tenant with Anthropic
and OpenAI configured could only ever reach whichever was added first. The second provider was
configurable, listed, enabled, billable — and unreachable. The comment had said so all along.

**The turn can now name a provider.** `ChatRequest` takes `provider_id` and `model`, both
optional, and `AgentConversationRunner` resolves them against what the conversation already
used before falling back to the default.

**The choice sticks to the conversation.** `ai_conversations` gains `AIProviderId` and `Model`,
written on every turn. A follow-up sent without naming anything keeps talking to the same
provider instead of quietly reverting to the default halfway through a thread — the same rule
the tool approvals and loaded skills already follow, for the same reason: a conversation is the
unit a person holds in their head.

The precedence is three lines (`AgentConversationRunner.ChooseModel`), and one of them is
easy to get wrong:

- An explicit provider replaces the whole choice, **model included**. Inheriting the
  conversation's model across a switch would send `gpt-5` to Anthropic, and the failure
  arrives as an opaque vendor error rather than as anything the user can act on.
- A model with no provider is the picker offering another model of the provider already in
  play, so the provider survives.
- Neither: whatever the conversation used last, else the tenant default. Rows written before
  this existed hold neither, so they resolve exactly as they did before.

**The server says which provider answered.** A new `model` SSE frame carries the resolved
provider id, its display name and the model, emitted after resolution and before the first
token. It is sent on every turn, including turns that named nothing — that is how the browser's
selector learns what a conversation resolved to without asking. Messaging channels (Slack,
Telegram, Teams) no-op it: they have no selector, and the choice is already on the row.

**The picker.** `ModelPicker` groups models by provider under `<optgroup>` and sits above the
message box. `GET /api/ai/models` already existed and nothing consumed it — it was built ahead
of this. Its policy moves from Operator to **Viewer** to match the chat itself: a role allowed
to send a turn has to be able to see which models it may name, or the picker appears for some
users and not others while both can chat. The endpoint returns model ids and provider names,
never keys.

A "New chat" carries the current selection forward. Someone who switched models to do a piece
of work is still doing that work on the next thread, and reverting to the default on every new
chat would undo a deliberate choice.

**A provider that goes away says so.** `ResolveAsync` now requires `Enabled` in the lookup, not
just in `Build`: chat requests carry a client-chosen id, and a conversation can hold the id of a
provider an admin has since switched off. Both had to read as "not available" rather than as a
disabled row being used anyway. The message names the provider — *"The AI provider 'OpenAI' is
disabled. Enable it, or pick another model to continue."* — because the person reading it is in
the chat and cannot act on a GUID. The selector shows an orphaned selection in warning colour
with the same instruction, and picking another model unpins the thread.

Note that `CreateAIProvider` still exposes no `config` field, so `config.models` is reachable
only by editing the row directly; in practice each provider offers its default model and the
picker is a provider picker. That is the shape the feature was asked for.


## 2026-09-02 — Every provider type the UI offers is now a provider that answers

`LlmProviderFactory` built exactly one of the three types the admin form accepted:

```csharp
"anthropic" or "ollama" =>
    throw new NotImplementedException($"provider type '{provider.Type}' is a Phase 2 follow-up"),
```

The controller validated the type, the `create_ai_provider` tool validated the type, and the
factory then refused to build two of them. Nothing failed at configuration time: the provider
saved, appeared enabled in the table, and the first question anyone asked it died inside the
turn.

**The two missing clients exist.**

- `AnthropicProvider` talks to the Messages API. The wire format is not OpenAI's, so the
  mapping is the substance of the file: the leading system messages become the top-level
  `system` field, tool calls and results become content blocks inside user/assistant turns,
  and roles are merged so a parallel tool round's results arrive in one user message — the
  shape the API requires. Streaming is a typed SSE sequence; a tool call is opened by
  `content_block_start` and filled by `input_json_delta` fragments, and one cut off
  mid-arguments is dropped rather than thrown, so `done` still carries the stop reason.
  `max_tokens` is required on every request and is clamped to the older models' output caps,
  and `temperature` is sent only to the models that still accept it — on the current families
  a sampling parameter is a 400, not a warning.
- `OllamaProvider` talks to the native `/api/chat`, not the OpenAI-compatibility shim: NDJSON
  instead of SSE, whole tool calls instead of argument deltas, and no tool call ids at all —
  results are addressed by tool name, so the ids the stream hands the runner are synthesised
  here and resolved back on the way out.

**And four more types.** DeepSeek and Kimi (Moonshot) speak the OpenAI wire format, so they
reuse `OpenAiProvider` with a different host. `custom` is the same client pointed at whatever
OpenAI-compatible endpoint a tenant runs; it is the one type whose base URL is required,
because it is the one type with no vendor endpoint to fall back to. Every other type may leave
Base URL blank and get its vendor's.

**`gemini` is the native API, not the compatibility endpoint.** It started as
`OpenAiProvider` pointed at `/v1beta/openai`, which was the cheap way in and the wrong one:
that route carries no `systemInstruction`, and it forwards tool schemas written for OpenAI's
draft into a validator that 400s on any keyword outside Google's OpenAPI subset — so a
tool-heavy turn failed on a body that looked fine. `GeminiProvider` is ported from flow-weaver
(with its 66 tests, deliberately verbatim so the two suites stay comparable): `contents` with
the assistant role spelled "model", the system prompt hoisted into `systemInstruction`,
function results matched by NAME because Gemini mints no call ids — the synthetic ids are
prefixed with the function name so the pairing survives a windowed history — schemas
sanitised against the supported keyword set, thought summaries filtered out of the reply text,
and the upstream `error.message` surfaced in the exception instead of "Response status code
does not indicate success: 404".

**The list lives in one place.** `LlmProviderCatalog` holds the types, their default endpoints
and which of them requires a base URL; the controller, the `create_ai_provider` tool and the
factory all read it, and a test asserts the tool's JSON schema enum still matches. That is the
invariant this entry is about: a type the form accepts is a type the factory can build.

The provider modal offers the seven types, hints the endpoint and a sample model id for the one
selected, and requires the base URL for `custom` before it will save.

**Four things a review caught before any of it shipped.**

- **A pasted base URL no longer doubles a path segment.** Every vendor publishes its base URL
  with the version already on it — `https://api.deepseek.com/v1`, Gemini's
  `…/v1beta/openai` — because that is what the OpenAI SDK expects, so pasting the documented
  value is the normal case. Concatenating produced `/v1/v1/chat/completions`: a 404 that reads
  like a bad key, from a field that looks right because it is what the vendor's own page says.
  `LlmHttp.CombineUrl` joins on the longest shared prefix instead, and all four providers use
  it.
- **`max_tokens` no longer exceeds a model's own ceiling.** Opus 4 and 4.1 stop at 32k output
  while the rest of the 4.x line takes 64k; at the streaming default every request to them
  would have been a 400. They are named in the cap table, and a clamp that bites is logged —
  a reply cut at a model's ceiling otherwise reads like a reply the model chose to end.
- **Ollama is told how big its context is.** It runs a model at its Modelfile window
  (commonly a few thousand tokens) unless `num_ctx` says otherwise, and a prompt that does not
  fit loses its beginning — where the system prompt is — with no error and no log line. The
  symptom is an agent that ignores its instructions, and nothing points at the provider. It
  now sends a 32k default, overridable per provider.
- **`stream_options` is asked for, not assumed.** It is how token counts arrive on a stream,
  and OpenAI, DeepSeek and Kimi honour it — but an older vLLM, a llama.cpp server or a strict
  gateway behind `custom` answers 400, which is not retryable, so the provider was unusable
  over a field that only buys accounting. It is now dropped for the rest of the turn if the
  endpoint says no.

**And two bugs the parity pass with flow-weaver surfaced.**

- **`temperature` is no longer sent to models that reject it.** GPT-5 and the o-series accept
  only their own default and answer 400 to anything else; it was sent unconditionally, so
  every request to the OpenAI models a tenant is most likely to configure today failed. The
  same rule already applied on the Anthropic side, where sampling was removed from the current
  families.
- **`ParseResponse` no longer throws on a response that omits a field.** `TryGetProperty`
  throws on an absent parent rather than answering false, so an OpenAI-compatible endpoint
  that leaves out `usage` — or an error body with no `choices` — took the call down from
  inside the parser.

**Both products now differ in nothing that touches a provider.** The last asymmetry was
Gemini (native in flow-weaver, compatibility endpoint here); the port above closes it. What
travelled the other way — flow-weaver's `model_limits`, its GPT-5 / o-series temperature gate
— is below.

**Provider limits are configured the same way in both products.** `Config.model_limits`
(`context_window` / `max_output_tokens`) is flow-weaver's key, ported here rather than
invented again: Anthropic sends it as `max_tokens` (still clamped by the model's ceiling),
OpenAI as `max_tokens` or `max_completion_tokens` depending on the family, Ollama as `num_ctx`
and `num_predict`. The same operator configuring both products does not have to learn two
names for the same knob.


## 2026-08-31 — Whether a step changed anything is now measured, not assumed

`SnippetResult.Changed` was a `bool?`, and when a handler said nothing the executor answered
for it:

```csharp
AggregateChanged(anyChanged, anyUnspecified, tier)
    => anyChanged || (anyUnspecified && tier != IdempotencyKind.Idempotent);
```

Silence meant **changed**, unless the handler was idempotent. Most handlers are not, so most
steps were recorded as having mutated something because nobody had said otherwise — and the
audit trail, the rollback plan and the run's final state were all computed on top of that.
Three of fifteen handlers were breaking the silence.

A tier says whether an action *could* be undone. The change signal says whether anything *was*
done. Answering the second with the first is what made it describe the handler's category
instead of the run.

**The inference is gone.** `SnippetResult.Change` is a required three-valued answer:
`Changed`, `Unchanged`, or `AuthorDecides` — and the third is not "unknown". A run cannot act on
"unknown" without picking something, and every reason to pick would be a guess.

**Every handler now answers, and several turned out to be able to MEASURE what they had been
deferring:**

- `ansible_playbook` reads the `changed=` count out of the play recap it was already parsing.
- `integration_action` uses the action's own HTTP verb; it used to fall through to null, so a
  catalogued GET that nobody had marked `read_only` was recorded as a change.
- `rest_call` does the same, and the REST executor now carries the verb on its result so the
  catalogued path can see it too.
- `git` was already measuring — a commit with no sha changed nothing.
- `ping` and `transform` read and compute; they change nothing by construction.
- `email_send`, `report` and `slack_message` change something whenever they succeed.

**Four types genuinely cannot know**, because the author supplies the action: `ssh`,
`python_snippet`, `ansible_playbook` when it defers, and `mcp_call` — MCP gives a tool a name
and a schema and no verb. For those the author declares, in the place the action lives:
`config_overrides.changes` on the NODE where the node carries it (ssh commands, an mcp tool),
and the new nullable `Snippet.ChangesState` where the snippet carries it (a python script, a
playbook). The node wins, being more specific. **A step of one of those types with no
declaration anywhere FAILS**, naming the snippet and what to set — louder than the old default,
and true.

### What you will see change

Steps recorded as `changed` only because their handler stayed silent are now `no_change`.
Rollback plans get shorter. More failed runs report `rolled_back` rather than `failed`.

Those are not new numbers to compare against the old ones. **Historical run records were
computed under the inference** and are not comparable with anything recorded from here on.

Migration `Phase15_SnippetChangesState`: one nullable column on `snippets`. Additive — no
rename, no drop, no recreate — and deliberately not backfilled: existing rows were written when
the answer was inferred, and inventing a value for them would record a measurement nobody took.

Backend 1229 tests green.

## 2026-08-31 — A snippet that carries code must explain itself

**BREAKING for seven existing snippets**, named below.

`python_snippet`, `python`, `transform` and `jmespath` now REQUIRE a `logic_diagram_mermaid`.
And for every type, a diagram that IS supplied must actually be one — a value whose first
meaningful line is not a Mermaid directive is refused at the API rather than stored, because a
field that accepts anything eventually holds a pasted stack trace and the first person to find
out is whoever opens the renderer.

**Why now: the rule is FlowWeaver's, and not having it made snippets non-portable.** Seeding
both deployed products through their own APIs with one payload, this one accepted a
diagram-less `transform` and the other refused it. So a snippet authored here could not be
recreated in a FlowWeaver instance by the route an operator actually takes, and nothing said so
until they tried.

Ported **verbatim** — same four types, same fifteen directives, same message text — so the two
products refuse an identical payload identically. Verified against the running pair: byte-for-
byte the same refusal. A parity rule that reworded its own error would leave an operator
comparing two products by their error text and concluding they differ.
`Skills/mermaid.md` came across with it, because the message points readers there and a
broken pointer is not parity.

**The seven, measured against the live instance rather than estimated** — all of the affected
types, not a sample. Each still RUNS; each refuses its next edit until someone writes a
diagram:

`Build ping summary email body`, `Python step`, `Reshape step output`, `hello-world-python`,
`qa-probe-mermaid-asymmetry`, `qa-python`, `qa-transform`

Three are QA fixtures. **The other four should be documented by whoever wrote them** — a
diagram invented by a third party is documentation that looks authoritative and is not.

**Not enforced on bundle import**, in either product. That was a decision, not an oversight:
refusing a bundle because a snippet authored elsewhere lacks a diagram would make a legacy
workflow unmovable, and the two products already agreed there. It does leave each product with
two doors and different rules — recorded in the change's follow-ups with three readings and a
recommendation.

Backend 1249 tests green; the deployed pair passes 72 QA checks with the parity now asserted
rather than reported as a finding.

## 2026-08-31 — `changes` is no longer reported as an unknown key

The node-level change declaration drew the unknown-key note, which said two false things: that
a key the contract names might be a typo, and that the handler ignores it. The executor reads
it, and a deferring type without a declaration anywhere fails its step.

Excluded from `SnippetKeyCatalog.UnknownKeys`, exactly as the idempotency override already was.
Found by deploying both products and passing a bundle between them — the same fault existed on
both sides.

## 2026-08-31 — A conditional edge with no condition stops being accepted

**BREAKING.** A workflow whose graph contains a `conditional` edge with an empty condition is
now **refused before any step runs**, with a message naming the edge by source and target.

It used to be accepted. `WorkflowSimulationAnalyzer` raised a `conditional_missing_condition`
finding at promotion, and at run time `ConditionEvaluator` read an empty expression as false,
so the edge simply never fired. The run SUCCEEDED, the branch was absent, and nothing in the
record said the author had forgotten to write the expression. That is the hardest kind of fault
to notice, and an edge type whose entire purpose is to gate cannot default to a silent no.

FlowWeaver — the contract's oracle — has always refused such a graph. This is the two products
agreeing, and the shared conformance vector was corrected in the same change: it asserted the
non-firing edge, having been reified from the DAG-WALK layer without noticing that the oracle's
parser refuses one layer earlier, so no walk ever happens.

**The refusal is a run-time gate, not a parse error, and that distinction cost a rewrite.** The
first version put the check in `Dag.Parse` and broke `WorkflowSimulationAnalyzer`: six callers
parse a graph and only one is about to execute it, and the analyzer parses precisely to report
*everything* wrong with a graph. A parser that threw on the first fault would hand an author one
issue at a time instead of the list they asked for. So parsing stays permissive, analysis sees
the whole graph, and `Dag.RequireRunnable()` refuses at the point a run begins.

**The simulation finding stays.** It is the earlier and friendlier warning; it just stops being
the only thing between a blank condition and a run.

**Who this affects is knowable in advance** — `workflow_simulate` has been reporting
`conditional_missing_condition` on exactly these graphs. Query them before shipping; the query
is in the change's follow-ups.

A condition that is present and evaluates to false is untouched: the graph is valid, the edge
does not fire, the target is `skipped`. There is a test that says so, because that is the half
most at risk of being caught by a too-broad refusal.

Backend 1229 tests green; conformance `executor` 33/33.

### The declaration had nowhere to live, and now it does

Shipping the above left a hole that only showed up when the agent skills were reviewed:
`Snippet.ChangesState` was **read by the executor and writable by nobody**. No DTO, no API
field, no `create_snippet` argument — the column existed and was permanently null.

For `python_snippet`, whose code lives on the snippet row, that made the snippet-level
declaration unreachable: every such step failed unless someone hand-edited `changes` into each
node's `config_overrides`. The rule was right and the surface for obeying it did not exist.

`changes_state` is now on `CreateSnippet`, `UpdateSnippet`, `SnippetResponse`, `SnippetDraft`
and the `create_snippet` agent tool, which echoes it back so an omission is visible at creation
rather than at the first run. Only a literal `true`/`false` counts — a string `"true"` is not a
declaration.

**The agent skills were wrong too, and in a way that mattered more.** `snippets.md` still
stated that `mcp_call` and `python_snippet` are `non_reversible` handlers whose ceiling is
absolute. That stopped being true when their floor was lowered to `requires_compensation`, and
it is live instruction — an agent reading it would have talked a user out of a declaration the
executor would in fact have honoured. Corrected, along with the four types that ARE
non-reversible (`email_send`, `slack_message`, `ssh`, `ansible_playbook`), and the change
signal documented beside the tier it keeps being confused with. `runs.md` gained
`change_undeclared`, which reads like a handler failure and is not one.

### The other half landed the same day

FlowWeaver adopted the same signal, same shape, same names — `StepChange`, the node-then-snippet
declaration order, the failure when nobody declares — and with it the run model it had never
had: `final_state`, a rollback plan, and an `audit.v1` event per changed node.

Nothing in this repository changed for that, and the shared kit was not touched. What changed is
what the kit MEASURES: the `executor` family went from 0/33 to 22/33 there, so for the first time
the two products are being compared on execution behaviour rather than on one of them having the
model and the other not. The eleven that still fail are decisions recorded in FlowWeaver's
`openspec/changes/adopt-run-outcome-model/followups.md`; two of them — `stop_on_failure` and a
blank `conditional` condition — are places where THIS product's behaviour is the one under
question, not FlowWeaver's.

## 2026-08-28 — The run context reports what the oracle reports

`run.failed_step_id` and `run.failed_step_error` are **empty strings until a step fails**, not
nulls.

This changed because of a conformance vector — in the opposite direction from the usual one.
The vector asserted null, this product emitted null, and flow-weaver emitted empty strings. On
the kit's own rule the oracle defines the contract, and flow-weaver's own test suite explained
why its choice was the right one: a notify node reached by an `always` edge reads these fields
on the SUCCESS path, often as the whole value of a message body. As a JSON null that body
becomes null, and a handler that requires one refuses a step that used to work. As `""` it
sends an empty body, which is what the author asked for.

So the vector had been written from THIS product rather than from the oracle — the fourth time
that has happened in this kit, and the first time it was caught by the other product's tests
rather than by reading its code. The vector was corrected in both copies and this product moved
to match.

`owner_email` and `url` stay null: they are genuinely unavailable here, which is a different
kind of absence from a placeholder that has not been filled in yet, and the projection now
distinguishes the two.

Ten executor vectors were briefly declared unimplemented during the same review and the
declarations were **reverted**. They assert a run model flow-weaver does not have — no
`changed` / `no_change` on a step, no `final_state`, no executor-computed rollback plan, no
per-node `audit.v1` — but this product implements all of it, so declaring them would have
claimed the behaviour is required of nobody. They stay as assertions this product meets and
the oracle does not, which is the honest state and is what blocks the kit leaving `1.1.0-draft`.

Backend 1229 tests green.

## 2026-08-28 — Six contract divergences closed, and the vectors that will keep them closed

Six vectors had been sitting in `workflow-v1-conformance/vectors/snippets/_pending/` because
each needed a product decision rather than a bug fix. All six are now decided, implemented and
landed, and `_pending/` is gone. The conformance gate runs 191 vectors, 0 fail, 0 skip.

Five of the six shared one shape, and it is the shape this whole interchange effort exists to
remove: **a canonical key is accepted, silently ignored, and the step reports success.** Nothing
fails, so nothing gets investigated.

**A threaded Slack reply lands in its thread.** `thread_ts` and `blocks` were read by nobody, so
a reply meant for a thread was published at the root of the channel and reported as sent. This
product has two transports and only one of them can thread: an incoming webhook has no thread
parameter at all, while a Slack `MessagingChannel` posts `chat.postMessage` with its bot token
and already addressed threads as `channel:thread_ts` for the reply flow. The snippet now prefers
that channel when one is configured. On the webhook path `blocks` is delivered — it is carriable
there — and `thread_ts` is **refused** with `not_supported` rather than flattened. Ambiguity
between several bot channels resolves to nothing rather than to a guess: posting an operations
alert into whichever workspace sorted first is not a recoverable mistake. `blocks` on a Teams or
generic-webhook record is refused too, being Slack-shaped.

**A message is never sent from an unintended sender.** `from_address` / `from_name` were dropped
in silence. A named `EmailChannel` builds its own sender and now honours them; the deployment
relay has no sender parameter at all and **refuses** with `not_supported`, naming the field and
saying that a named channel is the fix. The capability difference already existed in the code, so
this needed no new column and no new configuration. Two rules the implementation needed: the
channel's display name is never paired with the message's address — that reads as one team's
label over another team's mailbox — and a display name declared alone IS honoured over the
channel's own address, because labelling a message without changing its mailbox is legitimate and
dropping it would be the same silent discard in miniature.

**A catalogued `rest_call` resolves inside the source it names.** `source` was accepted and
ignored; resolution went by `operation_id` alone. The defect was worse than it looked: the index
is built with `GroupBy(OperationId).First()`, so an operation whose id a second spec also defines
is not merely lower-priority — it is **unreachable by id**, with the winner decided by spec load
order. A bundle could resolve to the wrong upstream, spend that upstream's credentials, and
return a well-formed 200. Resolution is now `(source, operation_id)`; a node naming no source
resolves exactly as before; a source that does not define the operation fails naming both.
`IApiSpecIndex` gained a **default** interface method — resolve unscoped, then refuse a mismatch
— so the eight existing implementations did not have to change and none of them can return an
operation from the wrong source.

**A python step's output is what the script produced.** The `{ result, stdout }` envelope is
gone: the sandbox had already separated the returned value from the printed text, so the handler
was wrapping an already-separated pair a second time and putting the author's object one level
deeper than any workflow written against the contract addresses it. The printed text moved to the
step's logs, which the run detail already shows. **BREAKING** for a template addressing
`steps.<id>.output.result`.

**And a python script may say what it produced either way.** Unwrapping the envelope was not
enough: the two products disagree about the *authoring* convention, not only the packaging. This
product's harness binds a name and reads it back; the contract's oracle takes standard output as
the return channel. A script written for either now runs here, with the assigned value winning
when a script does both — so accepting the printed form cannot change what an already-written
script means. Text that parses as nothing is preserved under `raw`, as the oracle does. A script
that assigns nothing and prints nothing produced no output and is not an error.

**`mcp_call` and `python_snippet` stop over-declaring.** Both shipped `non_reversible`, which is
absolute: it made the contract's own default undeclarable, so an author who had marked a step
compensable ran as if they had not, and a failed run reported `final_state: failed` where the
oracle reports `rolled_back`. Both now declare `requires_compensation`, the contract's default,
and an author may still declare a genuinely irreversible step as such.

### The sixth was the specification's mistake, not the code's

`ansible_playbook`'s vector asserted that only `extra_vars` reached the play. **No implementation
has ever done that.** The oracle passes the entire resolved payload (`AnsibleHandler.cs`,
`input.GetRawText()`) and so does this product, both documenting it as how a playbook receives
runtime parameters. The assertion had been written from the specification's own prose rather than
reified from the oracle — which is the rule the kit rests on — and it survived being written,
reviewed and scheduled before anyone opened the handler. Narrowing both products to match it
would have broken the documented parameter-passing mechanism in each of them, in exchange for a
credential leak that turns out not to exist: the device context carries inventory attributes, and
authentication travels by the inventory file and the process environment. **The specification was
corrected and no handler changed.**

### Coverage

Two companion vectors were added because a rule pinned on only one branch is half a rule. An
implementation that refused *every* declared sender would have passed the refusal vector and
failed every real workflow; one that removed the python envelope and still ignored a printing
script would have passed the output-shape vector while a portable step returned nothing.

Four of the vectors assert what the step **caused** rather than what it returned — the outbound
Slack call, the play's variables, the resolved upstream, the effective tier — because in every
one of these failures the output is indistinguishable from a correct run. Each probe has a
self-test proving it can report a wrong value; a probe that cannot fail is a second copy of the
implementation agreeing with itself. Parked vectors are now **named in the report** instead of
merely excluded: letting the parked set vanish quietly would be the same failure wearing the
kit's escape hatch as a disguise.

The kit's `oracle_commit` was corrected from `fw@1a11ea3` to `fw@fe07550`. The 1.1 layers were
reified from work that was still uncommitted when the pin was written, so the pinned commit
contained none of what the specification describes; anyone checking it out to verify the contract
would have concluded the specification was invented.

Backend 1229 tests green (+25). No migration was touched. flow-weaver was not modified.


## 2026-08-28 — Policies get a real editor instead of a JSON box

### Added
- **A visual rule builder on admin → Policies.** The rule was a bare JSON
  textarea with two "paste a template" buttons, which meant the only way to
  write a guardrail was to already know the schema — and the schema is the part
  that decides whether the thing blocks anything. The editor now has a **Visual**
  and a **JSON** tab over the same document: pick the shape (deny a run / gate a
  promotion), write the reason, toggle environments, pick device roles, pools and
  snippet types from what actually exists, add description phrases as chips, and
  build the `require` list of a gate with typed fields. The JSON tab is still
  there and still authoritative — both tabs read and write the same string, so
  they cannot drift, and hand-written formatting survives until a visual control
  is actually touched.
- **The pickers offer real values, not free text.** Device roles come from the
  inventory, pools from the pool list, snippet types from `/api/snippets/types`.
  A rule naming a role nobody uses never matches, and a policy that never matches
  is indistinguishable from no policy at all — which is the one way a guardrail
  can fail without anyone noticing.
- **`within_days` on a `successful_runs` gate requirement.** A count with no
  horizon is satisfied by runs from a year ago, which is not what anybody means
  by "three clean runs before production". Optional; absent or `0` keeps the old
  behaviour of counting every successful run in scope.
- **Arm/disarm from the row, and an Audit link.** The power toggle sends only
  `enabled`, so it cannot carry a half-finished rule from an open form along with
  it, and it confirms in both directions — arming starts enforcing immediately,
  disarming lets through whatever was being held back. Audit jumps to the trail
  filtered to policy changes, which is where "this used to block and now it does
  not" is answered. `/admin/audit` now seeds its entity-type filter from the
  query string, the way it already did for `actionPrefix` and `requestId`.

### Changed
- **Policies are expandable cards, not table rows plus a dialog.** A policy is
  read far more often than it is written, and the question people arrive with is
  "what does this one actually block?" — which a modal only answers after you
  have decided to change something. The row now opens in place, with the name,
  shape, armed state and description on the header. The dry run is unchanged and
  still reachable from the header, from each row, and from the create form.
- **The editor says what it cannot represent.** A rule carrying a matcher or a
  requirement type this build cannot evaluate — a rule written against another
  deployment, or by hand against a newer schema — is called out by name, with the
  warning that editing on the Visual tab will drop it. It used to be dropped
  silently.


## 2026-08-28 — Vendor commands can be filtered, refreshed, and told about a new platform

### Added
- **`GET /api/vendor-commands/platforms`.** Returns every platform the deployment
  knows, with a command count and a device count each: the union of what the
  catalogue covers and what the inventory actually runs. Nothing exposed that
  list before, so the only platform picker anywhere was a free-text box — and
  `resolve` matches the platform string exactly, which makes `cisco-ios` next to
  `cisco_ios` a 404 at run time and nothing before it. Derived rather than
  declared: a constant in the frontend goes stale the moment a vendor YAML lands
  under `Skills/vendors`.
- **A platform filter, a Refresh button and Add platform on admin → Vendor
  commands.** The filter runs server-side — the API has accepted `platform` and
  `intent` since the controller existed, the client just never sent them — which
  matters because the catalogue is one row per intent per platform and a dozen
  vendors outruns any page size. Search still narrows the loaded page on top of
  it. Add platform names a vendor that is not catalogued yet and hands straight
  off to the create form; there is no platform table to write to, so it becomes
  real when the first command is saved against it rather than being parked in
  browser storage where only one person can see it.
- **The page warns about platforms that have devices but no commands.** Every
  intent resolves to a 404 on those, which shows up as a workflow that runs on
  every vendor except one — the failure the catalogue exists to prevent, and the
  one thing neither the table nor the inventory showed on its own.

### Changed
- **The platform field on a new command is a picker, not a text box.** It offers
  the known platforms plus whatever Add platform just introduced. On an edit it
  stays disabled, as it was: intent and platform are the lookup key.


## 2026-08-28 — A Teams channel can now say why it is not receiving anything

### Fixed
- **A wrong Azure Relay credential is rejected at the save.** The Teams
  `app_token` is a Hybrid Connection *connection string*, but the value an admin
  has in hand at that moment is the Hybrid Connection's public *URL* — the
  details panel names it, since it is what the Azure Bot's messaging endpoint
  points at. Pasting it was accepted: `HybridConnectionListener` threw on it,
  `TeamsRelayHostedService` logged `teams.relay.error` and retried with backoff
  forever, and the channel went on reporting "Azure Relay · no ingress" while
  receiving nothing. `TeamsRelayConnectionString.Validate` now rejects a URL by
  name, and rejects a namespace-level policy string — which parses, but carries
  no `EntityPath`, so the listener attaches to no connection at all. The details
  panel says outright that the two values are different.
- **`Messaging__PublicBaseUrl` is now wired in the deployment.** The backend has
  read `Messaging:PublicBaseUrl` since messaging channels landed, but nothing
  ever set it: it was absent from `appsettings.json`, from `deploy/.env.example`
  and from the compose file, so every deployment ran with it empty. Two things
  silently did not work as a result. Admin → Messaging channels showed no
  webhook URL for any channel — deliberately, since half a URL is worse than
  none, but with no way to obtain the other half. And the bot could not offer an
  account link: `MessagingLinkService` returns an empty URL rather than a
  hostless `/link?token=…`, so an unlinked user messaging the bot got "link your
  Nashira account from the web app first" and a `messaging.link.no_public_base_url`
  warning in the log, with no page in the app that mints the token by hand. A
  channel could therefore be created and enabled but never bind anyone.
- **It defaults to `FRONTEND_ORIGIN` instead of to empty.** The value is the
  browser-facing origin of the app, which that variable already declares, so
  deriving it removes a setting nobody would think to look for. Set
  `MESSAGING_PUBLIC_BASE_URL` explicitly only when the provider must reach the
  app at a different hostname than people browse to — a tunnel or a reverse
  proxy in front of the webhook endpoints.


## 2026-08-27 — A run can finally be told what to run with, and on what

### Added
- **The manual Run button now opens a dialog instead of a bare confirmation.**
  It collects the two things a run needs and the UI never asked for: the runtime
  input and the target devices. The API has accepted both since
  `RunWorkflowRequest` existed — `runWorkflow()` simply POSTed an empty body — so
  a workflow whose nodes reference `{{ input.host }}` ran with that unresolved,
  and a `per_device` workflow ran against nothing. The rollback warning moved
  into the same dialog rather than preceding it: split across two steps, the user
  accepted the non-reversible risk before seeing what the run would target.
- **`$lib/workflow/deriveInputs` derives an input schema from the DAG.** Nothing
  forces a workflow to declare `input_schema` and there is no UI to author one,
  so most have none — an input form driven only by the declared schema would have
  been blank for almost every workflow. The derivation walks each node's
  `config_overrides` recursively (a reference can sit inside a body inside a
  headers map inside a list) and synthesizes one property per top-level
  `{{ input.X }}` key, matching the engine's own `input\b` boundary so
  `{{ inputs }}` and `{{ input_date }}` are not mistaken for references. A
  declared schema always wins: it carries types, descriptions and defaults no
  derivation can recover.
- **`DevicePicker` replaces the three inline checkbox lists.** It adds search and
  — the part that mattered — the environment gate. A device declares which
  promotion stage may dispatch to it; the executor drops the rest and refuses the
  run when that leaves nothing, so a selection could resolve to zero targets and
  only say so after the user pressed Run. Blocked devices are now unselectable at
  selection time, and an inherited selection the environment would drop is
  flagged with a way to clear it rather than silently unselected.

### Changed
- **The same form drives every path that starts a run.** Triggers (cron and
  webhook) and acceptance tests configured their input through a raw JSON
  textarea against no schema; both now use the schema-driven form and the device
  picker, seeded from the workflow's declared defaults so a key added after the
  trigger was saved still appears. `TriggerPayload.inputDefaults` and
  `AcceptanceTestPayload.input` take a parsed object accordingly — there is no
  half-typed text left for the API layer to validate. An empty object is sent as
  `null`: a present-but-empty default is not the same as declaring none.
- **`run_workflow` (agent tool) accepts `input` and `target_devices`.** It could
  pass neither, so the one caller with no human to ask was also the one that
  could not supply what the run needed. Unknown device ids are rejected up front,
  exactly as the manual endpoint does — discovering one mid-run would leave a
  half-executed workflow whose failure names a device instead of the request that
  was wrong.

### Removed
- **`WorkflowRunService.RunAsync(workflow, ct)`.** The convenience overload passed
  `null, []` and defaulted the trigger to `agent`; it was the mechanism by which
  the agent path silently dropped input and targets. Its callers now say what
  they are running with, and the assertion that an agent run is not recorded as
  `manual` moved to the caller that now owns that guarantee.

### Fixed
- **`WorkflowRollbackAnalyzerTests` compiles again.** It still called
  `Analyze(dag)` after the analyzer took a resolved-tier map, which had broken
  the whole test project — no test in the repository could run. Rewritten against
  the current contract, plus a case for the reason the signature changed: a node
  whose config claims `idempotent` is still non-reversible when that is the tier
  the executor will apply.

### Known issues
- Three tests fail on `dev` and are unrelated to the above — they fail identically
  at `aee97ac` with only the compile fix applied:
  `WorkflowExecutorTests.Failure_after_non_reversible_change_cannot_roll_back` and
  two `WorkflowBundleV3Tests` sub-workflow round-trip cases.
- Recursive sub-workflow inputs are still not collectable: `subflow` nodes are
  skipped by `SnippetNodeExecutor`, so a form that asked for a child workflow's
  inputs would be asking for steps this build does not execute.

## 2026-08-27 — Bilateral messaging channels: Slack, Telegram, WhatsApp, Teams

The agent stops being reachable only through the browser. A user writes from a
chat platform, the message enters through a webhook (or an outbound socket), the
turn runs over the existing `jobs` queue, and the reply returns to the same
thread. Ported from flow-weaver so both products share the surface.

Permissions are enforced **identically to the web**: the turn runs as the *linked*
internal user, with that user's real role read fresh on every message, capped —
never raised — by the channel's `MaxRole`. A channel can only narrow privilege, so
a non-admin can no more run an admin tool from Slack than from the browser.

### Added
- **Five entities + migration `Phase13_MessagingChannels`.** `MessagingChannel`
  (provider, three encrypted credentials, per-provider config, permission ceiling),
  `MessagingIdentityLink` (external identity → internal user),
  `MessagingInboundEvent` (append-only, and the idempotency anchor),
  `MessagingDelivery`, `MessagingLinkToken` (single-use, hashed, ~15 min).
  `AIConversation` gains `Source` / `MessagingChannelId` / `ExternalThreadId`, which
  is what lets one chat thread keep its history across turns.
- **Four providers behind `IMessagingProvider`.** Slack (HMAC `v0:{ts}:{body}`,
  300 s replay window, `url_verification` challenge, bot-subtype denylist),
  Telegram (`X-Telegram-Bot-Api-Secret-Token`, `is_bot` loop guard), WhatsApp
  (`hub.challenge` handshake, `X-Hub-Signature-256`, Graph API), and Teams (full
  Bot Framework JWT validation including the `serviceurl` claim, a cached AAD token
  evicted on 401/403, and `<at>` mention stripping).
- **`MessagingWebhookController`** (`[AllowAnonymous]`, per-provider verification,
  rate-limited per IP+channel) feeding `MessagingIngestService`: verify → dedupe →
  allowlist → identity → conversation → backpressure → enqueue.
- **Self-service account linking.** An unlinked user gets a one-time link from the
  bot; `/link` confirms it against whoever is signed in. `MessagingLinkController`.
- **Two no-public-ingress paths.** `SlackSocketModeHostedService` (outbound
  WebSocket via `apps.connections.open`) and `TeamsRelayHostedService` (Azure Relay
  Hybrid Connection, since Teams has no Socket Mode). Both reconcile every 30 s with
  capped backoff.
- **Admin UI at `/admin/messaging-channels`** with per-provider forms — the secret
  fields are relabelled per platform, the ones that do not apply are hidden, and the
  provider config renders as labelled inputs through the new `ParamsEditor` /
  `JsonSchemaForm` / `FieldHint` components rather than a raw JSON textarea.

### Notes on the security-relevant choices
- **The Azure Relay is transport, not authentication.** A Socket Mode event is
  trusted because the socket is the trust boundary, so that path skips the HMAC.
  The relayed Teams request is the same Bot Framework POST carrying the same JWT
  and goes through ordinary verification — issuer, audience, signature and
  `serviceurl` are all still checked. This also means *Requires client
  authorization* must be **off** on the Hybrid Connection, or the Relay consumes the
  `Authorization` header as its own SAS token and the JWT never arrives.
- **At-most-once agent turns.** The inbound row is written as the dedupe gate
  *before* the job is enqueued, and the worker flips `queued → processing`
  atomically. A reclaimed job sees `processing` and skips: no second LLM call, no
  duplicate reply.
- **Confirmation-gated tools are refused, not deferred.** There is no dialog in a
  Slack thread; the turn ends telling the user to run that action from the web app.
- **Messaging jobs share the `jobs` table but not the worker.** Each claim filters
  on its own types. Unlike a workflow run, a messaging job *is* safe to requeue on
  lease expiry, because the inbound event — not the job row — is the idempotency key.

## 2026-08-27 — Outbound notification channels renamed, freeing "messaging" for chat

### Changed
- **`MessagingChannel` is now `NotificationChannel`.** The name described two
  unrelated things. Here it meant a fire-and-forget outbound sink — one encrypted
  webhook URL, one direction, no identity, no conversation. In flow-weaver it
  means the *bidirectional* chat surface: a user writes from Slack, Telegram,
  WhatsApp or Teams, the agent answers in the same thread, and the turn runs as
  the linked internal user. Porting that feature in required the name back, and
  keeping both under one word would have made both harder to reason about.
  Renamed end to end: the `MessagingChannel` / `MessagingDelivery` entities, the
  `messaging_channels` / `messaging_deliveries` tables, `MessagingDispatcher` →
  `Services/Notifications/NotificationDispatcher`, the `messaging` named
  `HttpClient` → `notifications`, the audit entity key
  (`messaging_channel` → `notification_channel`, which
  `EntityRestoreService` resolves for undelete), the OpenAPI self-spec
  `na_notifications.yaml`, the `exports` agent skill, and the e2e scenarios.
- **The API moved from `/api/messaging/channels` to
  `/api/notifications/channels`**, and the admin page from `/admin/messaging` to
  `/admin/notifications` (nav label *Notifications*). Response fields
  `messaging_channel_id` / `messaging_delivery_id` become
  `notification_channel_id` / `notification_delivery_id`; `messaging.api.ts`
  becomes `notifications.api.ts`. The form, its validation and the delivery
  history are unchanged — this is a rename, not a redesign.
- **`slack_message` still posts through these channels.** Only the type it
  resolves changed; the `channel` / `text` / `via` contract, the single-Slack-channel
  fallback and the delivery row stamped with `WorkflowRunId` all behave as before.
  The "not found" message now points at `/admin/notifications`.

### Migration
- `Phase13_RenameMessagingToNotificationChannels` renames both tables, their id
  columns, their indexes and their primary-key constraints. EF scaffolded it as
  drop + create; it was rewritten by hand as renames, because these tables hold
  live configuration — including the encrypted webhook URL, which the API never
  returns and so could not be re-entered from anywhere but the operator's
  password manager — plus the delivery history that exists precisely to answer
  questions after the fact. A drop/create would have destroyed both silently.

## 2026-08-27 — A workflow can now leave this instance and run on another one

### Added
- **The conformance kit grew the four layers a *portable* workflow depends on.**
  `workflow-v1-conformance` moves to `1.1.0-draft`, reified from flow-weaver
  `fw@1a11ea3`, and gains `bundle/SPEC.md` (the interchange bundle: one portable
  identity per referenced thing, a `requires` block, sub-workflows and triggers
  travelling, secrets never), `snippets/SPEC.md` (per type: canonical input keys
  with FW as the oracle, the aliases a handler must accept, the portable output a
  downstream template may rely on, the idempotency floor), `templates/SPEC.md`
  (namespaces, filters, per-device scoping, condition grammar) and
  `execution/SPEC.md` (outcome classification, the canonical retry shape, what a
  `subflow` node does, what an importer does with triggers). Three vector
  families — `bundle`, `snippets`, `templates` — are reserved beside the five that
  existed. The reason all four landed at once: the two products had agreed on the
  graph since 1.0 and still could not run each other's workflows, because every
  layer *above* the graph had drifted independently. The graph was never the part
  that was broken.
- **The bundle is v3, and it is the only export that survives crossing instances.**
  `GET /api/workflows/{id}/bundle` now emits `schema_version: "v3"` with a
  `requires` block saying what the receiving instance must support: every
  `snippet_type` in play, the capabilities the graph uses (`subflow`,
  `template_filters`, `run_namespace`, `per_device_scope`, `max_parallel`,
  `per_pool`, `conditional_edges`, `python_network`, `triggers` — a closed
  vocabulary; an unknown name is refused rather than skipped), and every
  `${secret:…}` reference the workflow will try to resolve, with the nodes that use
  it. `BundleRequirements` computes it as a pure function of the file, so the far
  side can recompute it and compare. v2 files are still read: their `requires` is
  inferred from what they carry (`bundle/SPEC.md` §2.3), so nothing exported before
  today has to be re-exported.
- **Portable reference keys.** Inside a bundle a node names what it points at:
  `integration` (the slug), `action`, `server`, `credential`, `repository` — never a
  local GUID. `snippet_id` and `subflow_workflow_id` stay GUIDs because they are
  structural, and the bundle carries the full definition of what they point at, so
  they are always remapped. The legacy id keys the other product used to write
  (`integration_id`, `action_id`, `mcp_server_id`, `credential_id`,
  `repository_id`) are translated on import through the mapping the bundle's own
  `dependencies` carry, and refused with `bundle_reference_untranslatable` — naming
  the node and the key — when no mapping exists. Before this, a foreign node's ids
  were meaningless here and the import died at the reference gate with nothing to
  act on.
- **Sub-workflows travel.** Every workflow reachable through a `subflow` node,
  transitively, appears once in `dependencies.workflows` with its own nodes, edges,
  input schema and metadata; its snippets and references merge into the same
  `dependencies` sections and the same `requires`. On import the children are
  created first as drafts with `metadata.is_subflow = true`, then every
  `subflow_workflow_id` in the parent and in the other children is remapped. A child
  is reused only on an exact name match **and** an identical canonical hash of
  nodes+edges: the same name over a different graph is a different workflow that
  happens to be called the same thing, and binding to it would silently run the
  wrong steps. A cycle is refused (`bundle_subflow_cycle`) on export as well as on
  import — an unrunnable file must not leave this instance either.
- **Triggers travel, disarmed.** Cron and webhook triggers are exported without
  their HMAC secret, their target devices or their run statistics, and are created
  **disabled, with a freshly generated secret and no targets**; the notes tell the
  operator what to set before enabling each one. A webhook route is kept when it is
  free here and regenerated (with a note) when it is not; an unresolvable IANA
  timezone stores UTC and leaves the trigger disabled rather than firing at the
  wrong hour. `WorkflowTriggerSecrets` now owns the two random values, so the
  trigger endpoint and the bundle importer cannot drift on the property that
  matters — a route and a secret are random, never derived from the name.
- **`{{ run.* }}` resolves.** A fixed ten-field run projection (`RunContext`): id,
  workflow_id, workflow_name, environment, trigger, started_at, owner_email, url,
  failed_step_id, failed_step_error. A field this build cannot provide is emitted
  as `null` rather than omitted, so a template that reads it resolves instead of
  being left literal and failing the step. `failed_step_id` / `failed_step_error`
  are filled in as steps fail, which is what lets a node on a `failure` edge name
  the step that sent it there.
- **Template filters.** `{{ ref | trim | default('n/a') }}` — `default`, `trim`,
  `upper`, `lower`, `truncate(n)`, `json`, `strip_ansi`, `strip`. Only `default`
  recovers an unresolved reference; every other filter on an unresolved value
  leaves the template unresolved, and unknown filters are ignored. Conditions
  resolve their references through the same grammar as payloads: two grammars would
  let an author write a condition that reads correctly and evaluates against
  nothing.
- **Console: "Export bundle" beside "Export YAML"** on a workflow's page, and a
  Bundles section in the docs — what travels, what never does, and the two possible
  outcomes of an import. `importWorkflow` now returns `import_notes`,
  `created_snippets`, `created_workflows` and `created_triggers` instead of
  discarding them.

### Changed
- **The retry policy on the wire is the canonical shape**
  `{ max_retries, initial_delay_seconds, backoff, max_delay_seconds }`
  (`execution/SPEC.md` §3), with `backoff` extended to `linear`. Nashira's legacy
  `{ max_attempts, delay_seconds, backoff }` is still read — `max_retries =
  max_attempts − 1` — and exports are translated on the way out, so a snippet
  authored here is intelligible there. The two gates are unchanged and remain
  Nashira's declared profile difference: a retry needs the handler to have marked
  the failure retryable **and** the step to be `idempotent`, capped at 5 attempts
  and 30 s between them. A bundle cannot raise those caps; a policy must not be
  able to pin a synchronous run open.
- **A `per_device` consumer reads a `per_device` producer's output for its own
  device.** The stored output of a fan-out step is the aggregate
  `{ devices: [ { device_id, output } ] }`; a per-device consumer now sees the entry
  for the device it is running against, so `{{ steps.show.output.stdout }}` means
  what its author meant. A `once` consumer still sees the whole aggregate.
- **A residual `{{ … }}` fails the step before the handler runs**
  (`unresolved_template`, naming the reference) instead of reaching a device as a
  literal. The scan runs per device, so a reference that resolves for one target and
  not another fails only that target's slice. The one deliberate exception is
  `{{ device.* }}` inside the stored input snapshot of a per-device step: one
  snapshot cannot show one value per device.
- **Snippet handlers accept the contract's keys as well as their own.** `ssh`
  (`commands`/`command`, `device`/`host`, `credential`, `use_structured`/
  `structured`, `enable_secret`/`enable`), `integration_action` (`params`/`query`
  canonical, `path_params`/`query_params` as aliases, canonical wins when both are
  present, and the response headers now ride in the portable output), `git`
  (`repository` by name beside `repository_id`), `report` (FlowWeaver's structured
  `document` rendered through the same markdown pipeline as Nashira's `content`),
  `slack_message` (`channel` + `via`), `ansible_playbook` (`hosts`/`targets`/
  `device`/`host`). `ping` reports its probe method (`tcp` here) in its output, as
  the contract requires, and returns a null latency on a timeout rather than the
  elapsed time — a template averaging latencies must not count a failure as a
  fast reply. An import notes, per node, every `config_overrides` key that no
  canonical name or documented alias covers, so a typo like `comands` surfaces at
  import instead of at 3am.
- **`transform` evaluates JMESPath.** `expression` is the canonical key (on the
  payload or on the snippet's `code`); Nashira's `mapping` stays as an alias,
  evaluated through the resolver's own path grammar so keys with dots and hyphens
  keep working. New dependency: `JmesPath.Net`, the package and version flow-weaver
  evaluates with.

### Fixed
- **ssh credential material can no longer travel as a plain value.** `username`,
  `password`, `private_key` and `key_passphrase` in a node's `config_overrides` are
  accepted only as `${secret:…}` references — resolved once, immediately before the
  wire — and a plain value is refused at import (`bundle_reference_untranslatable`)
  and at run time. A typed-in password is not portable, it lands verbatim in the
  stored input snapshot of every run, and the credential store exists precisely so
  it never has to be typed into a workflow.
- **An import is now all-or-nothing, and says everything at once.** Resolution
  reports every missing dependency, unsupported capability and untranslatable
  reference in a single refusal (`bundle_dependencies_missing`,
  `bundle_capability_unsupported`, `bundle_reference_untranslatable`,
  `bundle_subflow_cycle`, `bundle_incomplete`, `bundle_version_unsupported`), and
  the snippets, sub-workflows, workflow and triggers are created in one
  transaction. An operator fixing an import wants the whole list, not its first
  item — and nothing is ever stubbed: a placeholder that returns nothing turns "the
  workflow my colleague sent me" into a run that goes green having done no work,
  which is the failure this whole format exists to remove.
- **A degraded import says so.** `max_parallel > 1` is walked serially,
  `target_mode: per_pool` is created as `per_device`, a network-enabled python
  snippet arrives with the flag off, a snippet matched by name rather than slug, a
  `${secret:…}` that does not resolve here yet — each one is an `import_notes[]`
  line naming what to check. Silence means nothing was degraded, and that silence
  is the contract.
- **`csv` and `xlsx` reports fail with `not_supported`** instead of quietly
  producing a markdown file under a spreadsheet's name. The contract allows the
  formats; this build's renderers write prose, and a silently substituted format is
  worse than a failed step.

## 2026-08-26 — Assistant profiles reach the agent

### Added
- **A user's profile now steers the assistant.** The profile assigned to a user
  (`display_name`, `skills`, `response_style`) and the user's own
  `custom_profile_text` ride into every one of their turns as a second system
  message, `[USER PROFILE CONTEXT]`, placed right behind the shared prompt
  (`UserProfileContext` / `UserProfileContextLoader`, read once per turn in
  `AgentConversationRunner`). `Skills/base.md` gained the rule that reads it:
  adapt tone, depth and format; never tools, permissions or confirmations.
  This is the per-user behaviour netora's Python agent had (its
  `user_profiles` + `custom_profile_text`, injected in `chat_routes.py`) and
  that the port had stored but never applied — `administration.md` used to
  tell the agent, correctly, that assigning a profile changed nothing. A
  deactivated profile is ignored (the user's text still applies); a failed
  read costs the persona for that turn, not the turn
  (`ai.chat.profile_read_failed`). `ai.chat.begin` logs `profile=`.
- **Account page: "Assistant profile".** Every user can pick a profile and
  write up to 500 characters about themselves (role, tools, language) —
  `GET/PUT /api/profiles/users/me/profile`, which existed without a UI.
- **Users → Edit assigns a profile.** Admins set another user's profile and
  text from the edit modal; the list shows a Profile column. Backed by a new
  `GET /api/profiles/users/{userId}/profile` (Admin): the PUT replaces the
  text too, so the modal reads it first rather than wiping what the user
  wrote. Until now only the API and the `assign_profile` tool could do this.

### Changed
- `custom_profile_text` is capped at **500 characters** (netora's limit) on
  the endpoint and on the `assign_profile` tool; whitespace-only text is
  stored as null. It never enters the shared, process-cached prompt.
- `nashira.md`, `administration.md`, the admin Profiles page and the docs now
  describe profiles as what they do, and say when a profile (per user, tone)
  is the mechanism versus a prompt skill (every user, rules).

## 2026-08-26 — Truncated answers: tool-result cap, cut-off notice, turn deadline, prompt size

### Added
- **Integration skills load on demand.** A prompt skill with an `integration_id`
  no longer rides in every turn. The system prompt carries a one-line index of
  these skills ("Integration skills": name, integration, what it covers, loaded
  or not); the full text enters when the integration is in play — the user
  names it, a call goes to its API through `execute_operation` /
  `operation_detail` / `discover_operations` (auto-loaded mid-turn, right
  behind the tool result), or the agent asks with the new `load_skill` tool
  (read-level, autonomous, every role — unlike `get_skill`, which is admin
  material). Once loaded, a skill stays loaded for that conversation
  (`ai_conversations.LoadedSkillsJson`, same shape as tool approvals). Global
  rows and the built-in `Skills/*.md` are unchanged; a row whose integration
  is gone or disabled falls back to global rather than vanishing. Before this,
  each vendor skill added (an Action1 skill is 25 KB) cost its full length on
  every turn about anything, forever. `ai.chat.begin` now logs `prompt_chars`,
  `scoped_skills` and `skills_loaded`; loads log `ai.chat.skill_autoloaded`.
- **Conversation history is a window.** `AiChat:HistoryCharBudget` (default
  `60000`, `0` disables) bounds the history replayed to the model: the newest
  messages that fit, cut on turn boundaries, the most recent exchange always
  included. When something is left out the model is told how many messages
  and to ask rather than assume; `ai.chat.history_windowed` logs the cut. The
  conversation itself is still stored whole. Before, every turn re-sent the
  entire thread until the provider refused it or a gateway trimmed it from
  the top — where the system prompt is.

### Fixed
- **`operation_detail` resolves `$ref`.** Vendor-generated specs (the official
  Action1 one, NetBox's) define every parameter, body schema and response once
  under `components` and reference it; `OperationYamlSlicer` read a `$ref`
  entry as a nameless parameter and showed such operations with
  `parameters: []`, so the model never learned that `limit`/`from` existed,
  took the server's 50-row default and reported the first page as the whole
  inventory. Local `#/components/...` references are now followed for
  parameters, request bodies, schemas and responses, and `allOf` is flattened
  one level (a ResultPage response is `allOf: [$ref] + properties`). Shipped
  inline specs are unaffected.
- **An answer the model could not finish now says so.** OpenAI-compatible
  endpoints report `finish_reason: "length"` when the model runs out of output
  tokens, and the provider parsed it and threw it away, so a cut-off answer was
  persisted as a complete one — it reads like a short answer, and nothing on
  screen said otherwise. The stop reason now rides on the stream's `done` event
  (`ChatStreamEvent.StopReason`); on `length` (or Anthropic-style `max_tokens`)
  the runner appends a visible notice to the answer, logs
  `ai.chat.output_cut_off`, and records the turn as `truncated` — a new
  `AgentTurn` status that `/admin/sessions` shows as a warning rather than a
  failure. The notice goes through the answer text on purpose: it is persisted
  with it, so on the next turn the model also knows it was cut off. A tool call
  whose arguments were cut mid-JSON is dropped with a warning instead of
  killing the turn on a parse error nobody could trace.
- **A tool result is no longer cut at 8 000 characters before the model sees it.**
  The cap was a private constant in `AgentConversationRunner` — a regression
  from flow-weaver, where the same guard ships at 100 000 and is configurable.
  At 8 000 a single `execute_operation` page of an external API (an Action1 or
  NetBox list with a few dozen rows) never fit, so the model was handed a JSON
  body cut mid-item plus a `[truncated N chars]` marker and answered from half
  the data; that is what surfaced as "the assistant truncates its answers".
  The cap is now `AiChat:MaxToolResultChars` (default `100000`, override with
  `AiChat__MaxToolResultChars`), logged on `ai.chat.begin`, and pinned by a test
  so it cannot quietly shrink again. Nothing else about the marker changed: a
  result past the cap is still cut, still tells the model how much is missing.

### Changed
- **`AiChat:StreamDeadlineSeconds` defaults to 240, up from 120.** Every tool
  round re-sends the whole prompt, each REST call may take up to 15s, and a
  real answer takes the model a while to write; at 120s such turns ended as
  `timeout` with half an answer on screen. 240 keeps the 60s margin under the
  5-minute transport backstop that the options tests insist on. Deployments
  with a reverse proxy in front need its read timeout above this number, or
  the cut moves to the proxy — silently.

## 2026-08-25 — Full mailbox capability on email channels (IMAP)

### Added
- **Email channels grew an inbound (IMAP) side.** A channel can now carry
  `imap_host` / `imap_port` (default 993) / `imap_security` (default `ssl`,
  same strict no-downgrade modes as SMTP) plus optional dedicated credentials —
  blank reuses the SMTP username/password, the common one-account case. A
  channel without an IMAP host stays exactly what it was: outbound-only.
  MailKit's `ImapClient` drives the new `IEmailChannelMailbox` service
  (list folders, list/search, fetch, mark, move, archive, delete); no new
  dependency, MailKit was already in the project for SMTP.
- **Seven mailbox agent tools**, so mail tasks work from chat:
  `list_email_folders`, `list_emails` (unread/from/subject/body/date filters),
  `read_email`, `mark_email`, `move_email`, `archive_email`, `delete_email`.
  Reads are `autonomous` — and genuinely read-only: folders open read-only, so
  listing or reading never flips the user's unread state. Mutations are
  `single_confirm`; `delete_email` is `elevated_confirm` because expunge
  (`permanent: true`, or an account with no Trash) cannot be undone by anyone.
  Deletes default to the server's Trash; archive finds the special-use archive
  folder, an existing `Archive`, or creates one.
- **`email_mailbox` workflow snippet** (seeded as `baseline-email-mailbox`):
  one handler, `action` = `list` / `read` / `mark` / `move` / `archive` /
  `delete` / `folders`, default idempotency `requires_compensation`. A list
  step emits a flat `uids` array designed to feed a downstream acting step via
  `{{ steps.list.output.uids }}`.
- **Admin UI and API.** `/admin/email` gained a "Mailbox (IMAP)" form section,
  a `mailbox` badge, and a one-click IMAP test;
  `POST /api/email/channels/{id}/test-imap` connects and reports folder and
  unread counts. Channel responses expose `imap_*` fields, `has_imap_password`
  and `imap_configured`; the IMAP password is write-only with
  `clear_imap_password` for explicit removal, mirroring the SMTP one.
- New agent skill (`Skills/email.md`) covering the triage loop, uid staleness,
  and the Trash-before-expunge stance; docs updated (email channels section,
  snippet catalogue, messaging "outbound only" note now points at the email
  exception).

## 2026-08-25 — Bulk delete tools for the agent

### Added
- **Four bulk-delete agent tools** — `bulk_delete_devices`, `bulk_delete_reports`,
  `bulk_delete_workflows`, `bulk_delete_snippets` — so a batch cleanup is one
  confirmed call spending **one** mutation-budget slot, instead of a loop of
  per-item deletes that exhausted the turn's budget of 20 and stalled every 20
  rows. Each tool selects by an explicit id/name list, by filters, or both
  (filters restrict the list; an empty selection is refused), soft-deletes the
  matches in a single transaction, and returns the resolved outcome
  (`deleted`, `not_found`, `excluded_by_filter`). Device filters:
  `missing_ip`, `site`, `role`, `vendor`, `platform`. Report filters: `search`,
  `workflow_run_id`, `expired_only`, `older_than_days` (exports are never
  touched). Workflow filters: `environment`, `name_contains`. Snippet filters:
  `name_contains`, `type` — snippets still referenced by an active workflow are
  skipped and reported with the referencing workflow names, mirroring the
  REST delete's guard.
- **Confirmation and attribution are built in.** All four are classified
  `single_confirm`, so the batch always confirms in chat (the read-only
  autonomy waiver never applies to them), and an optional `expected_count`
  refuses the call when the match count no longer equals what the user
  confirmed. Beyond the dispatcher's per-call audit row, each handler writes
  its own hash-chained audit event (`agent.bulk`) carrying the selection
  criteria and the resolved list of what was deleted — attributed by
  `AuditLogger` to the requesting user — plus a structured
  `ai.tool.bulk_delete_*` log line with the user id and actor.
- **`query_devices` gained a `missing_ip` filter** so the agent can preview
  exactly which devices a `bulk_delete_devices missing_ip=true` call would
  remove before asking for confirmation.

### Changed
- Agent skills (`inventory`, `workflows`, `snippets`, `exports`) now steer the
  agent to the bulk tools for multi-item deletions — preview first, show the
  names and count, pass `expected_count` — and the chat UI labels the new
  tools ("Delete devices in bulk", …) in the confirmation card.


## 2026-08-25 — Nashira brand identity applied to the default theme

### Changed
- **The default palette now implements the Nashira visual identity** (from
  `frontend/static/Nashira_resumen_identidad_interfaz.pdf`), replacing the
  placeholder cyan-teal/graphite look. Every OKLCH ramp in `app.css` was
  rebuilt around the brand anchors, each landing verbatim on its natural stop:
  azul `#1D4E89` (primary-500, primary action in light, 8.39:1 under white
  text), turquesa `#00B2CA` (primary-400 and the whole secondary family —
  information, progress and focus), beige `#E3CFB4` (tertiary-200, warm
  accents), the coral-derived destructive pair `#B72B19` / `#FF795F`
  (error-500 / error-400), and neutrals running lavanda `#EAECF6` (light
  canvas) to carbón `#222222` (surface-950, the dark canvas). The primary ramp
  is hue-shifted like warning's — turquesa light stops, azul dark stops — so
  the existing mode-aware pairs (`text-primary-700-300` etc.) deliver the
  identity's mode assignment without touching component markup: deep azul
  links in light, bright turquesa in dark.
- **Filled CTAs and active indicators follow the identity's dark-mode pairs.**
  `Button` (primary/danger), the chat send button, the user chat bubble,
  audit filter buttons, status dots and active tab/nav borders now switch in
  dark mode to `-400` fills with `-950` text (turquesa + carbón, 6.5:1;
  salmon + deep red for destructive, 6.5:1) instead of keeping the light-mode
  fill. Keyboard focus rings ride a new mode-scoped `--focus-color` token:
  azul in light (turquesa cannot clear 3:1 there), turquesa in dark.
- **The logo is now the real brand asset.** `Logo.svelte` renders
  `static/Nashira_logo_v1.png` (serif wordmark + eight-point star, coral
  `#EE3D23`) in both modes, per the identity mockups; `wordmark={false}`
  draws an inline coral star for icon-sized uses. The favicon is a coral
  star with a light sparkle on a carbón tile. Coral itself lives in a new
  `--color-brand` variable outside the theme families, so custom themes
  recolour the interface but never the brand.
- **Theme engine kept in lockstep.** `RAMPS`/`SHELL`/`STOCK_HEX` in
  `theme.svelte.ts`, the `nashira` preset, the pre-paint shell backgrounds in
  `app.html` and `prefs.svelte.ts` (`#eaecf6` / `#161719`) and the in-app
  theme docs were all updated to the new baseline; `npm run check:theme`
  passes. Success and warning ramps are unchanged — the identity does not
  redefine them and their status semantics predate it.


## 2026-08-25 — Copy buttons work on insecure origins

### Fixed
- **Copy buttons no longer fail on HTTP deployments.** `navigator.clipboard`
  only exists in secure contexts (HTTPS or localhost), so on an instance
  reached as `http://<ip>:3006` every copy affordance — the secret-reference
  button in Admin → Secrets, the chat code-block Copy button, the theme JSON
  export — hit its failure path ("Copy failed — reference: …"). A shared
  `copyText` helper now tries the Clipboard API first and falls back to the
  legacy `execCommand('copy')` path, which still works on insecure origins;
  the failure toasts remain only for the case where both mechanisms fail.


## 2026-08-25 — API rate limiting and real client addresses

### Added
- **Every API endpoint is now rate-limited** (ported from FlowWeaver's design,
  built on ASP.NET Core's native rate limiter — no external packages). Eight
  named policies in `RateLimitingConfiguration`, applied per endpoint via
  `[EnableRateLimiting]`: `auth_login` (5/min per IP on login, refresh and
  bootstrap — complements the per-account lockout: IP covers attackers rotating
  usernames, lockout covers attackers rotating IPs), `auth_generic` (100/min
  per user), `read_heavy` (300/min per user for list/detail GETs),
  `write_normal` (60/min per user for create/update/delete), `ai_chat`
  (30/hour per user — every request hits an external LLM), `workflow_run`
  (20/hour per user — schedules real work on devices), and two anonymous
  webhook-ingest policies partitioned by (IP, route) so one noisy source
  cannot starve other hooks: `git_webhook_ingest` (30/min) and
  `workflow_webhook_ingest` (20/min). Authenticated policies partition by the
  JWT user id, falling back to IP, so many sessions cannot multiply one
  attacker's quota. Rejections return 429 with `Retry-After: 60` and
  `{ error: "rate_limited", retry_after_seconds: 60 }` — the shape the SPA's
  API client already maps to a friendly "too many requests" message.
- **The backend now sees the real client address.** The SvelteKit container
  proxies `/api`, so every request used to arrive from that container's IP —
  one constant address in the audit/auth-event trails, and a single shared
  per-IP login budget for the whole platform. The proxy now appends
  `X-Forwarded-For`, and the backend honours it — plus `X-Forwarded-Proto`/
  `-Host` — **only from proxies the operator declared** in
  `Network__TrustedProxies` (CIDRs and/or IPs; the compose file pins the
  network subnet to 172.27.0.0/16 and trusts exactly that). Unset means trust
  nothing: an empty trust list disables the middleware outright, because to
  `ForwardedHeadersMiddleware` empty lists mean "trust everyone" — which would
  let any caller forge its origin and dodge the login limiter. `ClientIp`
  centralises reading the resolved address (normalising IPv6-mapped IPv4) for
  the limiter, `AuditEvent.Ip` and `AuthEvent.Ip`.


## 2026-08-25 — Credentials email on user creation

### Added
- **A newly created user is emailed their credentials.** Both creation paths —
  `POST /api/users` and the AI `create_user` tool — send the username and
  temporary password to the new user's address through the deployment's SMTP
  relay (`UserCredentialsNotifier`), with an explicit note that the password is
  temporary and must be changed at first sign-in. The message is in English.
- **The admin is told when that email cannot go out.** When the SMTP relay is
  not configured (or the send fails), user creation still succeeds and the
  response carries `credentials_email_sent: false` plus a `warning` telling the
  admin to deliver the credentials another way; the admin UI surfaces it as a
  warning toast instead of the success message. `IEmailService` gained an
  `IsConfigured` flag so callers can tell "no relay" apart from a failed send.
  The password itself never reaches the logs or the audit trail.


## 2026-08-25 — Restore from the audit trail

### Added
- **A delete event in /admin/audit can now restore the record it points at.**
  Expanding a delete row shows a Restore button when the server knows how to
  bring the record back (`restorable`, computed server-side so the UI never
  offers what the API would refuse). `POST /api/audit/{id}/restore` flips the
  soft-delete flag (`IsActive`) back on via the new `EntityRestoreService` — an
  explicit map of 24 restorable entity types (credentials, secrets, users,
  devices, workflows, integrations, …) rather than reflection over every DbSet,
  so a type whose delete endpoint is not known to be soft fails with "cannot be
  restored" instead of resurrecting something its controller never expected
  back. The primary key is resolved from EF metadata (`FindAsync`), so one code
  path covers all types.
- **The restore leaves its own trace.** The endpoint is `[SkipAudit]` for the
  generic mutation filter (which would have recorded it as a mutation of
  "audit") and instead appends an explicit hash-chained event: the real entity
  type, action `restore`, the signed-in admin as actor, and
  `restored_from_sequence` pointing back at the delete event it undid. The
  `restore` action renders with a success badge in the trail.
- Failure modes are first-class: restoring a record whose name or slug a live
  record has since taken returns a 409 naming the conflict (most unique
  indexes are filtered to active rows only); an already-active record and a
  hard-removed one are reported as exactly that.
- **Deleted chat conversations are restorable too — attachments included.**
  The audit filter now maps the conversations controller to `ai_conversation`
  (the restore map also accepts the older raw `ai_conversations` spelling for
  rows written before the mapping), and deleting a conversation soft-deletes
  its stored attachments instead of hard-removing them, so a restore brings
  the thread back whole. Every attachment read path (`parse_file`, the
  per-turn upsert) filters on the active flag, so a deleted thread's file
  content stays unreachable until it is restored. Restoring does not expose
  the chat to the admin who restores it — conversation reads remain
  owner-scoped; the thread simply reappears for its owner.

## 2026-08-25 — Chat attachments the agent can actually read

### Fixed
- **A binary file attached to a chat message (xlsx, any zip-based format) was
  unreadable by the agent.** The runner inlined text attachments into the user
  message but replaced binary ones with "(binary file, N bytes — not shown
  inline)" and then dropped the bytes; `parse_file` supports xlsx but only via
  `content_base64`, which the model had no way to obtain — so it asked the user
  to paste the file as base64. Attachment bytes are now registered on
  `AgentTurnScope` (the existing per-turn scoped ambient) and `parse_file`
  gained an `attachment` parameter that reads them back by filename,
  case-insensitively. The inline note for a binary attachment now tells the
  model exactly which call to make and explicitly not to ask the user to paste
  content; a truncated text attachment points at the same route for the full
  bytes. A wrong filename errors with the list of names that ARE attached.
- **Attachment chips disappeared when a conversation was reloaded.** Persisted
  turns stored only role + content, so the paperclip chips on user bubbles
  existed only in the live session — reloading made it look like the file was
  never attached. Stored user messages now carry an optional `attachments`
  array of filenames (never content; older rows simply omit it) and the UI
  renders the chips from history.

### Added
- **Attachments now live for the whole conversation, not just the turn they
  arrive in.** Each turn's attachment bytes are persisted into a new
  `conversation_attachments` table (migration
  `Phase11_ConversationAttachments`): one row per (conversation, filename),
  case-insensitive — re-attaching a name overwrites, which is also how "here
  is the corrected version" should behave. `parse_file` resolves `attachment`
  first against the current turn, then against the conversation's stored rows,
  so a follow-up like "now check column X" works without re-uploading the
  file. When rebuilding the LLM context, a stored message's attachments are
  annotated as still readable via `parse_file`. Bounds and hygiene: files over
  5 MB are not stored (the reader would refuse them anyway), each conversation
  keeps at most 20 attachments (oldest-touched evicted past the cap), and
  deleting a conversation hard-deletes its attachment rows — file content must
  not outlive a thread nobody can open.

- **`parse_file` reads PDF and Word documents.** Two new formats: `pdf`
  returns text per page (PdfPig, with `ContentOrderTextExtractor` so columns
  and tables come out in reading order rather than content-stream order; a
  `page` parameter fetches a single page of a long document) and `docx`
  returns the body text plus every table as a columns-and-rows grid
  (DocumentFormat.OpenXml — already a transitive dependency via ClosedXML,
  now referenced directly and pinned). The attachment note the model sees now
  suggests the format from the file extension. Legacy binary `.doc`
  (Word 97–2003) is not supported and fails with a parse error; the skill
  tells the model to ask for `.docx` or PDF. New packages: `PdfPig 0.1.16`,
  `DocumentFormat.OpenXml 3.1.1` (pin).

### Changed
- The chat composer now rejects files over 5 MB with a toast (matching
  `parse_file`'s server-side cap — a bigger file would attach fine and then
  fail opaquely when read) and shows an error instead of silently skipping a
  file the browser could not read.
- `Skills/exports.md` documents the `attachment` route and the one-turn
  lifetime of attachment content.

## 2026-08-25 — NetBox inventory sources: token hygiene and sync errors that name the problem

### Security
- **`token_secret_ref` accepted a raw NetBox token, stored it unencrypted, and
  echoed it back to every viewer.** The API said "prefer a `${secret:...}`
  reference" but enforced nothing: a literal token pasted into the form landed
  as-is in `inventory_sources` and round-tripped through `GET
  /api/inventory/sources`, which only requires the Viewer role. Now
  (`InventoryTokenRef`, shared by the controller and the `create/
  update_inventory_source` agent tools):
  - Write time accepts the **name of a stored secret** (normalized to a full
    `${secret:secret:<name>:value}` reference) or a full `${secret:...}`
    reference, and rejects anything else with an error that deliberately does
    not echo the value — it is most likely a real token.
  - Secret and credential references are checked for existence at save time, so
    a typo fails as a 400 naming the missing secret instead of a NetBox 403 at
    sync time.
  - Responses mask legacy literal tokens as `***`; references pass through
    untouched. Update endpoints treat an echoed `***` as "keep what is stored",
    so edit forms can round-trip a response safely. Legacy literal rows keep
    syncing (the sync falls back to the literal when the bare value matches no
    secret) but can no longer be (re)saved.

### Changed
- **The inventory source form no longer takes the token as free text.** The
  single "Token secret reference" input became an Authentication selector: no
  auth, paste an API token (written to the secret store as
  `netbox-<source>-token` — rotated in place on re-edit — with only the
  reference saved on the source), pick an existing secret, pick an existing
  credential that holds a token (`${secret:credential:<name>:token}`), or type a
  custom `${secret:...}` reference. The paste field is a password input; the
  token itself never appears on screen nor travels with the source payload.
- **An unresolvable `${secret:...}` reference now fails the sync with a 400
  naming the reference.** `SecretResolver` leaves unresolved markers in place by
  design, so the sync used to send `Authorization: Token ${secret:...}` and
  surface NetBox's 403 — indistinguishable from a wrong token. A deleted or
  renamed secret is now reported as exactly that.

### Changed
- **The backend built its Serilog pipeline from `ReadFrom.Configuration` with no
  `Serilog` section to read, so every namespace ran at a flat `Information`.**
  `UseSerilog` replaces the Microsoft.Extensions.Logging factory, which means the
  `Logging:LogLevel` block in `appsettings.json` never reached the pipeline — it
  looked like configuration while doing nothing. The result was a log that was
  simultaneously too loud and too blind: every EF Core SQL statement and every
  ASP.NET framework line was printed, while all 21 `LogDebug` calls in the
  codebase were unreachable. `AI_LOG_PAYLOADS=true` in `OpenAiProvider` logged at
  Debug and therefore did nothing at all. Levels are now built in code, matching
  Flow Weaver:
  - `Microsoft.AspNetCore` → Warning, `EntityFrameworkCore.Database.Command` →
    Warning (`LOG_EF_SQL` to raise it while hunting query bugs).
  - `nashira_backend.Services.*` → Information (`LOG_ACTIONS`), with
    `Services.Ai`, `Controllers.AiChatController` and `BackgroundServices` pinned
    to Debug — longest-prefix match keeps them verbose even when `LOG_ACTIONS`
    silences the rest.
  - `LOG_FORMAT=pretty|json` picks the console sink: colored one-liners
    (`[HH:mm:ss INF] ShortContext message`) or CLEF for Loki/Elastic. Unset
    auto-picks pretty in Development, json otherwise.
  - Every line now carries `service` and `env`; `ShortContextEnricher` and
    `CallerTagEnricher` (new, in `Services/Observability/`) keep the text sink
    scannable. `CallerTag` renders empty until request context pushes `username`.
- **Request logging dropped health and OpenAPI polling to Debug.** The container
  healthcheck hits `/health/live` every few seconds; at Information it was most
  of the log. 4xx now logs at Warning and 5xx at Error rather than all at
  Information.
- `LOG_FORMAT`, `LOG_EF_SQL`, `LOG_ACTIONS` and `AI_LOG_PAYLOADS` are wired
  through `deploy/docker-compose.yml` and documented in `deploy/.env.example`.

## 2026-08-24 — Role gating consistency, admin self-protection, N concurrent chats

### Fixed
- **Five sidebar items led non-admins to a denial panel.** The nav registry
  deliberately lists the read-only catalogs (`/admin/snippets`, `/admin/pools`,
  `/admin/vendor-commands`, `/admin/integrations`, `/admin/mcp`) at `minRole:
  viewer` — matching the backend, whose GET endpoints are Viewer policy — but the
  `/admin/*` layout blanket-required admin, so viewers and operators saw the pages
  in the menu and couldn't open them. The layout now asks the registry
  (`minRoleForPath`) for the role the current path requires, so the guard can never
  contradict the sidebar again; undeclared `/admin` paths still fail closed to
  admin. Anything admin-only stays hidden from non-admins as before.
- **The role driving the UI was trusted from localStorage forever.** Editing
  `nashira:auth` in devtools flipped the whole UI to admin chrome (the API still
  refused the calls, rendering 403-filled pages). On boot the app now confirms the
  session against `GET /auth/me` — until then unused — and corrects any local
  drift (`authStore.reconcile`).
- **An admin could demote or deactivate their own account.** `PUT /users/{id}`
  accepted a self role change and even `is_active=false` on yourself, side-stepping
  the existing self-delete guard. Both the REST endpoint and the agent's
  `update_user` tool now refuse changing your own role (`self_demote`) and
  deactivating your own user (`self_deactivate`): privilege removal on an admin
  must come from another admin, so a tenant can't end up admin-less through one
  session's own actions. Covered by new `UsersControllerTests`.
- **Only one chat could run at a time.** The backend never serialized anything —
  the limit was the frontend's single module-level `ChatSession`: one conversation
  id, one transcript, one AbortController, one global `sending` flag. Sending
  while any turn streamed was silently dropped, and switching conversations (or
  "New chat") aborted the in-flight turn. The store is now a `ChatHub` holding one
  live session per conversation for the lifetime of the tab: any number of chats
  stream concurrently, switching threads parks a streaming session and returning
  picks up the same instance mid-stream, and a draft that acquires its server id
  mid-stream is re-keyed seamlessly when the URL catches up. `sending` remains a
  per-conversation guard so two turns can't race one conversation's history
  (the backend persists turns with read-modify-write).

## 2026-08-24 — Scheduled runs execute as the trigger's creator

### Fixed
- **A workflow with a `rest_call` step against the platform's own API (an `na_*`
  spec) returned HTTP 401 when fired by a cron trigger, while the same workflow
  succeeded when run manually.** The self-API specs authenticate with
  `${secret:session:current:jwt}` — "the caller's own bearer". A manual run executes
  inline in the HTTP request, so the resolver reuses that request's token; a
  scheduled run executes on the job queue with no HTTP context and no identity bound,
  so the marker stayed literal and the self-call went out with a garbage bearer.
  `MutableCurrentUser` existed for exactly this (mint a short-lived token from a
  bound identity) but was never registered nor bound. Now:
  - `ICurrentUser` resolves per context: HTTP requests keep reading JwtBearer claims
    via `CurrentUser`; background scopes get the scope's `MutableCurrentUser`.
  - The job worker binds the trigger creator's identity (`WorkflowTrigger.CreatedBy`,
    with that user's role) before running a triggered job, so the session-token mint
    works and the run is capped at the creator's own permissions. Jobs with no
    trigger, triggers with no `CreatedBy`, or creators since deleted/disabled run
    anonymous as before (logged as `jobs.worker.run_as_anonymous`).

## 2026-08-24 — MCP OAuth redirect URI behind the frontend proxy

### Added
- **`docs/mcp-oauth.md`** — admin guide for connecting an MCP server with the OAuth
  authorization-code flow: how the Authorize flow works, how the redirect URI is
  derived, a worked Google/Gmail example (Web client, consent screen, scopes,
  `access_type=offline&prompt=consent`, Workspace Developer Preview enrollment), and a
  troubleshooting table for every failure mode met while wiring it. The `mcp` skill
  now names the two OAuth failure cases (`needs_authorization`, preview enrollment) so
  the agent answers them precisely.

### Fixed
- **The MCP OAuth Authorize button generated an unreachable redirect_uri when used
  through the deployed frontend.** The SvelteKit proxy strips the browser's `Host`,
  so the backend built the OAuth callback URL from its internal Docker name
  (`http://backend:8080/...`) — which Google rejects (`invalid_request`: http on a
  non-localhost host) and no browser could reach. The proxy now forwards the
  browser-facing origin as `X-Forwarded-Host`/`X-Forwarded-Proto`, and the two
  OAuth endpoints build the redirect_uri from those (new `McpOAuthRedirect`
  helper), falling back to the request host for direct calls.
- docker-compose now sets `Cors__AllowedOrigins__0` from `FRONTEND_ORIGIN`, so the
  post-consent 302 lands on the deployed frontend instead of the dev origin
  (`localhost:5173`) baked into appsettings.json.

## 2026-08-21 — MCP OAuth parity with Flow Weaver

### Added
- **The OAuth authorization-code flow for MCP servers, ported 1:1 from Flow Weaver.**
  Nashira's MCP core was already FW's (same client, connection factory, codec); what FW
  had and Nashira lacked was the browser-consent grant. Ported verbatim, with only
  mechanical adaptations (namespaces, `ISecretProtector`, Nashira's snake_case auth
  field names — so every already-configured server keeps decrypting):
  - `McpOAuthService` — OAuth 2.1 client mechanics: metadata discovery (RFC 9728 /
    RFC 8414 / OIDC), Dynamic Client Registration (RFC 7591), PKCE, and the
    authorization-code / refresh / client-credentials grants.
  - `McpOAuthFlowService` + `POST /api/mcp/servers/{id}/oauth/start` + the anonymous
    callback endpoint — signed, time-limited state (DataProtection), one-time PKCE
    verifier, CSRF-checked nonce, and a replayed callback can never downgrade a
    healthy server.
  - `McpTokenService` grew FW's refresh path: an expired authorization-code token
    renews via its refresh token, and when only a browser can fix it the server flips
    to the new `needs_authorization` status — which is what makes the Authorize button
    light up. Client-credentials also gained FW's endpoint discovery when no token_url
    is typed; its existing grant path is untouched.
- **`/admin/mcp` now reads like FW's `/admin/mcp-servers`:** an *OAuth2 (authorization
  code)* auth option (client id/secret and endpoints all optional — discovery and DCR
  fill the gaps), an Authorize action per server row that leaves for consent and
  returns with a toast, and a warning-toned `needs_authorization` badge.
- FW's flow and token tests came along with the code (state round-trip, code exchange,
  tampered state, denial, replay-after-success, refresh, refresh-failure).

### Added
- **Six snippet types, closing the gap with Flow Weaver's catalogue.** Same type names,
  Nashira-native implementations — each is a thin adapter over a service Nashira
  already owns, not a port of FW's internals:
  - **`report`** renders markdown `content` into a stored report (markdown/html/pdf via
    the shared document builders) and persists it as a `ReportArtifact` — so a run's
    report lands in /reports, stamped with the run, workflow and triggering user. The
    output carries the file as `base64`, ready for a downstream attachment.
  - **`email_send`** sends through a named email channel (slug or id; full message
    shape) or, unnamed, the deployment's default relay — which honestly refuses `bcc`
    and multiple attachments rather than dropping them. A channel's default recipients
    apply when the step names none. `EmailChannelSender` grew the full message shape
    (cc/bcc/html/reply-to/attachments) to carry it.
  - **`slack_message`** posts through a MessagingChannel (Slack, Teams or webhook) via
    the existing dispatcher, so deliveries are recorded, retried sensibly, and linked
    to the run. FW's name, kept so shared workflows reference the same type.
  - **`ansible_playbook`** runs the snippet's YAML against one target — `device`
    (inventory, environment-gated exactly like ssh, password over env never argv) or a
    literal `host`. Linux worker only, clear error otherwise.
  - **`netconf` and `snmp_v3`** are registered stubs, as in FW: the types exist so a
    shared workflow validates and fails with `not_implemented` instead of
    `unknown_handler`. Deliberately no baseline snippet for these two.
- **Runs now expose their identity to steps.** The run id is allocated *before*
  execution and travels on the execution scope (with workflow id and triggering user),
  so artifact-producing steps can stamp what produced them — previously the id was
  minted after the walk and `SnippetRequest.WorkflowId` was always `Guid.Empty`.
- Baselines seeded for the four executable new types; the handler table in the agent's
  snippets skill, `create_snippet`'s type list and the docs' type catalogue all updated.

### Added
- **A motion system, ported from Flow Weaver.** `$lib/anim` wraps animejs behind the
  same contract as FW's module — `animate`, `stagger`, `safeAnimate`,
  `prefersReducedMotion` — plus the `animate-in fade-in` / `slide-up` CSS utilities under
  the same names. Every animation collapses to its final frame under
  `prefers-reduced-motion`, either through the guard or through the existing app-wide
  media rule.
- **Navigation has weight now.** Page content rises in (240ms) when the route changes —
  never on param-only moves like switching chat conversations — and the `<main>` scroll
  container resets to the top on forward navigation. It never reset before: SvelteKit
  only manages window scroll, and ours is an inner scroller, so a new page used to open
  wherever the previous one was scrolled to. Back/forward is left alone.
- **The sidebar cascades in on load** (FW's exact stagger: 28ms per item), and
  collapsing a group folds it shut instead of blinking it away.
- **Micro-interactions across the kit.** Modals settle into place with a fading
  backdrop; the command palette and the `g`-chord hint pop from their anchor; the user
  menu rises from its trigger; stat cards count up to numeric values (once per change,
  strings render as-is); dashboard bars grow from the baseline left-to-right — once,
  deliberately, because dashboards poll and a replayed entrance every 30s is a
  screensaver; buttons give slightly under the press.

## 2026-08-21 — one map across both consoles

### Changed
- **The sidebar now mirrors Flow Weaver's.** Same group order and labels — Overview alone
  at the top, then **Intelligence**, **Build**, **Integrate**, **Operate**, **Govern**,
  **Help** — so moving between the two consoles never means relearning the map. Items
  moved to where Flow Weaver keeps their counterparts: Chat sits under Intelligence with
  AI Studio; Workflows and Git under Build; Vendor commands under Integrate; Device
  pools, Credentials and Secrets under Operate; Tool permissions and Python modules under
  Govern next to Policies. The command palette groups and the admin hub follow, since all
  three derive from the same registry.
- **The "Build library" tab bar is gone.** Its four pages (snippets, device pools, vendor
  commands, Python modules) now live in different groups, so a shared tab bar would have
  jumped the sidebar selection around; each page stands on its own header, as every one
  of them is in Flow Weaver.

## 2026-08-21 — the agent can read the reports it writes

### Added
- **`list_reports`, `read_report`, `save_report`.** The agent could produce documents and
  never look at one again. `export_document` returned a download link and nothing else,
  the only route to `/api/reports` was `execute_operation` against `na_notifications`,
  and that route cannot work for this: the download endpoint returns bytes, so a PDF
  arrived UTF-8-decoded into a page of replacement characters. Asked what its own report
  said, the agent answered that it had no access to it — about a document it had written
  minutes earlier.
  - `list_reports` is the catalogue across **both** stores. `source` is `reports`
    (persisted `ReportArtifact` rows, the ones in `/reports`), `exports` (what
    `export_document` / `export_table` produced), or `all`; filters for
    `workflow_run_id`, a `search` over title and file name, and `include_expired`.
    The distinction between the two stores is ours, not the user's, and "the report you
    made" is either one depending on which tool made it.
  - `read_report` returns the text. It takes a `report_artifact_id` **or** the
    `export_artifact_id` a document export already handed back, so a report can be
    quoted, summarised or extended instead of described from memory. Long documents page
    through `offset` / `next_offset` rather than truncating silently — a body that stops
    mid-sentence with no marker is how an agent confidently summarises the first third of
    an incident write-up.
  - `save_report` persists one, so it survives the conversation and stays in `/reports`
    under a title. It rejects a `workflow_run_id` that matches no run, which
    `POST /api/reports` does not: a run id the agent inferred rather than read produces a
    report attributed to nothing.
- **A rendered file says so instead of returning mojibake.** Only text survives the round
  trip — markdown, html, csv, json, plain text. A pdf or an xlsx comes back
  `readable: false` with its download link and the reason, and `list_reports` carries the
  same flag per item so it is visible before the read. The content type is checked
  against the bytes, not trusted: a PDF labelled `text/plain` is still binary.
  Deliberately not an `error` field — the dispatcher reads that as a failed call worth
  self-correcting, and there is nothing here to correct.
- Both reads are autonomous; only `save_report` confirms. Re-reading a document the
  caller could have downloaded from `/reports` anyway is not worth a confirmation dialog,
  and dialogs nobody needs are what teach people to approve without reading. New
  permission domain `report`, which — like every domain — is default-allow until an
  administrator restricts it.

## 2026-08-21 — deleting something frees its name again

### Fixed
- **Re-creating a deleted resource by the same name no longer dies with a 500.**
  Deletes are soft everywhere — the row stays, flagged inactive — but sixteen unique
  indexes (credentials, skills, profiles, providers, devices, git repositories,
  knowledge slugs, inventory sources, device pools, vendor commands, messaging and
  email channels, allowed Python modules, policies, snippets, MCP servers) still
  covered every row ever written. The controllers' duplicate checks have always scoped
  uniqueness to live rows, so creating "router-core" after deleting "router-core"
  passed validation and then crashed on the index. `Phase11_SoftDeleteAwareUniqueIndexes`
  rebuilds those indexes filtered to `"IsActive"`, the same shape `ai_api_specs` and
  `integrations` already used. Deliberately untouched: `users.Username` and
  `secrets.Name` (both reserve names even after deletion, on purpose — a re-created
  secret must not silently repoint the templates that name it) and every generated
  slug except knowledge articles', because `Slug.Unique()` already dodges all rows
  ever created and a slug is an external identity.
- **A lost duplicate-name race now answers 409, not 500.** The pre-insert `AnyAsync`
  check can never close the gap between the SELECT and the INSERT. A new exception
  handler translates Postgres unique violations (23505) into the same
  `application/problem+json` conflict the ordinary "name already exists" path returns.

## 2026-08-21 — steps stop claiming they never ran

### Fixed
- **Every step of every existing run said "never ran".** `Phase11_RunDiagnostics` added
  `step_runs.Attempts` with a default of 0, and the same change taught the run panel to
  read 0 as "this step never reached its handler" — so every row written before that
  migration reported it, including steps sitting directly above their own output. A
  panel that contradicts itself is worse than one that says less: the reader stops
  trusting the parts that were right. `Phase11_BackfillStepAttempts` sets stored rows to
  1, which is the honest value — a `step_runs` row exists because the executor recorded
  an execution.
- **The badge no longer contradicts the step's result.** "never ran" is now shown only
  on a step that failed, which is the one case that actually produces 0: a node whose
  snippet reference never resolved, which cannot succeed. On any other result the badge
  defers to the result word rather than arguing with it. A test pins the other half —
  a node that reached its handler always records at least one attempt.

## 2026-08-21 — a run says enough to diagnose it without running it again

### Added
- **Steps record what they were actually handed.** `step_runs.InputJson` stores the
  node's configuration as the handler received it — `{{ … }}` references already
  resolved. This is the field that answers the most common "why did it do that": an
  unresolvable reference is left literal, so the handler gets the template text itself
  and fails on a type nobody expected, and the workflow definition cannot show you
  that. Redacted through the same helper the agent's tool telemetry uses, so a value an
  author typed under `password` does not get a second home on a screen any Viewer can
  read, and capped at 4 KB so a node carrying a file body does not store it twice.
- **A failure message in its own column.** `step_runs.Error` was only ever readable by
  digging `error` out of the output payload — which worked because `SnippetResult.Fail`
  put it there, a convention rather than a contract, invisible to anyone reading the row
  who did not already know to look.
- **Per-step timing and attempts.** `StartedAt`, `FinishedAt`, `duration_ms` and
  `Attempts`, so "which step was slow" and "which one hit its timeout" are answerable,
  and a step that only succeeded on the third try stops being indistinguishable from one
  that worked first time. Attempts used to be spliced into the output payload, and only
  when the count was interesting — a diagnostic in the middle of the data downstream
  nodes address, present on some steps and not others.
- **`step_runs.Logs`, the handler's own account of what it did.** The field that
  explains a `no_change`: `ping` says whether the host answered and how fast, `ssh` says
  how many commands ran and how many failed, `git` says which file at which ref and how
  big. A read-only step reporting `no_change` with no output is otherwise
  indistinguishable from one that never really ran.
- **`workflow_runs.Error`, for a run that no step can explain.** Orchestration fails
  before or between steps — a policy denied it, the DAG would not parse, the targets
  resolved to nothing — and those failures previously left **no run row at all**. The
  caller got an exception and the runs list showed nothing, so "I pressed run and
  nothing happened" was literally true and had no trail. A refused run is now recorded
  with `final_state: refused` and the reason, and the original exception still reaches
  the caller — the record is best-effort and never replaces the failure it describes.
- **`workflow_runs.Trigger`.** `manual`, `agent`, `schedule`, `webhook`, `git_webhook`
  or `test`. `TriggeredByUserId` separated a person from automation but not which
  automation, and now that a cron, an inbound webhook and a git push can all start the
  same workflow, "it ran at 3am and nobody knows why" needed the run itself to answer.
  It travels on the job payload rather than in a column, because the queue row's
  `WorkflowTriggerId` can only identify the first of those three. Existing rows are
  backfilled from whether a user was signed in.

### Changed
- **A failed SSH step names the command that failed.** It reported "one or more commands
  failed", which sent the reader into the payload to find out which one and why — on a
  step whose entire content is the commands. It now names the first failure with its
  first line of output, and says how many followed.
- **The run panel shows all of it.** Per-step duration, an attempts badge when it is not
  the ordinary single try, the handler's logs, and the resolved input behind a
  disclosure. A step with only logs used to render as unexpandable, so the one thing it
  had to say was unreachable.
- **The runs list has a "Started by" column and filter**, and marks a refused run as
  such — `failed` with 0 of 0 nodes reads like a broken platform rather than a run that
  was stopped on purpose, so the reason is shown on the row.
- **The runs skill and the `na_runs` spec teach the new fields**, including the one
  instruction that saves a wasted turn: a run carrying a top-level error with
  `node_count: 0` never executed, so there is no failed step to find, and hunting for
  one ends in "the run failed but no step reported an error".

## 2026-08-21 — a cron trigger stops rescheduling itself every time you edit it

### Fixed
- **Editing a trigger no longer postpones its next firing.** The update endpoint
  re-based `NextRunAt` whenever the request carried a cron expression *or* a timezone,
  and the console serialises the whole form on every save — so renaming a trigger, or
  ticking "enabled", silently rescheduled it. Edit a nightly `0 2 * * *` at 01:59 and
  it does not run that night; edit it at 03:00 and nothing looks wrong, which is why
  this presented as "sometimes it fires, sometimes it doesn't". The sharper version of
  the same bug: an edit landing in the seconds between a firing coming due and the
  30-second sweep noticing moved the due time forward and dropped that run outright.
  Only a change to the expression or the zone reschedules now, decided by
  `TriggerScheduleRules` and pinned by tests over every shape the console sends.
- **A trigger with no next run gets one.** The scheduler's backfill sat behind an early
  return taken only when nothing was due, so on an instance where something is due on
  most ticks, a trigger with a null `NextRunAt` was never given a schedule and simply
  never ran. It now runs on every sweep.
- **A cron expression with no occurrence after the current one no longer kills the
  trigger silently.** The sweep wrote the null next-occurrence straight onto the row,
  leaving a trigger that reads as enabled with an empty next run and never fires
  again — on screen, indistinguishable from a scheduler that has stopped working. It
  is now disabled with that reason recorded on the row.

### Changed
- **A skipped firing says so on the trigger, not only in the trace.** A schedule that
  came due while the platform was down and fell outside the catch-up window was logged
  and traced, but the trigger itself showed a next run and nothing else — so "why
  didn't it run last night" had no answer where the person asking was already looking.
  It now records which firing was skipped and how late it was, cleared by the next
  run's outcome.
- **`LastError` on a disabled trigger survives its final run.** The field means two
  things: on an enabled trigger it is why the last run failed, and a success clears it;
  on a disabled one it is why the scheduler switched it off. Clearing it on the very
  run that disabling let through left a trigger that is off for no stated reason.
- **A schedule that is fixed stops reporting the old failure.** Editing the expression
  of a trigger the scheduler had disabled as unschedulable, or re-enabling one whose
  due time had passed, clears the error rather than leaving it beside a working
  schedule until the trigger happens to run.

## 2026-08-21 — a run opens where you clicked it, and workflows list per environment

### Fixed
- **The run detail expands under the row that was clicked.** On a workflow's Runs tab
  the detail panel was rendered after the whole list, so opening the first of twenty
  runs put the answer twenty rows further down the page — far enough that the click
  looked like it had done nothing, and with nothing on screen tying the panel to the
  run it described. It now opens inline under its own row, with a chevron that turns,
  `aria-expanded`/`aria-controls` wired up, and the row highlighted the moment it is
  clicked rather than when the fetch returns.
- **A run whose detail fails to load says so.** `getRun` failures were caught and
  turned into a null selection, which on screen is identical to clicking and having
  the page ignore you. The row now un-selects and a toast names the failure. A slow
  response for a row you have already navigated away from is also discarded instead of
  painting over the run you are currently looking at.

### Changed
- **Workflows are listed per environment, as in FlowWeaver.** Draft, QA and Production
  are tabs over a server-side filter rather than one flat list with a badge on each
  row. A draft, its QA copy and its production copy are three rows carrying the same
  name, so mixing them meant the name stopped identifying anything — and "delete this
  one" was being aimed at a list where the environment was the smallest thing on the
  row. Each tab carries its count, because an empty Production tab and a filter that
  is silently returning nothing look identical otherwise. The environment is kept in
  the URL (`?env=qa`) so a reload or a shared link lands where it was left, and the
  empty state for QA and Production says how a workflow gets there — by promotion,
  never by being created in place.
- **Selection is cleared when the tab changes**, so a bulk delete can only ever act on
  the environment on screen.

## 2026-08-21 — a push can start a workflow, and a workflow can touch Git

### Added
- **The `git` workflow node.** A workflow that needed to read or write a repository had
  no way to do it: the worker knew `ping`, `transform`, `rest_call`, `integration_action`,
  `ssh`, `mcp_call` and `python_snippet`, and none of them reach Git. So "pull the
  configs every night and commit a compliance report back" — the request the scheduler,
  the git service and the snippet catalogue were each individually ready for — ended
  with the agent offering to do the commit by hand, which is not automation. `git`
  snippets take `operation` plus `repository_id` from the node's `config_overrides` and
  cover `read_file`, `write_file`, `commit`, `pull`, `push`, `list_files`, `status` and
  `diff`. `diff` also returns `has_changes`, because the question a condition edge
  actually asks is "did anything change", and every workflow should not have to answer
  it by parsing a patch. Three baseline snippets ship with it so a node has something
  legal to point at on a fresh install.
- **Inbound Git webhooks.** `POST /api/git/hooks/{route}` accepts a delivery from
  GitHub, GitLab or anything that can sign a body; a verified push pulls the working
  copy and enqueues a run of the bound workflow with `{ repository_id, branch,
  commit_sha, provider, git_webhook_id }` as its input. Nashira already had a generic
  webhook trigger, and it could not be used for this: it reads `X-Nashira-Signature`,
  while GitHub signs `X-Hub-Signature-256` and GitLab sends a bare token in
  `X-Gitlab-Token` — so pointing a real provider at it authenticated nothing, and
  nothing pulled the repository or extracted the branch either.
- **Delivery history, and a dry run.** Every hit is recorded — verified, rejected,
  dispatched or failed — with the event, branch, commit and the reason when nothing
  happened. Bodies and headers are deliberately not stored: a push payload carries
  commit messages and author emails, and this table would otherwise become the largest
  unreviewed store of them in the system. `GET …/webhooks/{id}/dry-run?branch=` answers
  what a push would do without a push, from the same rules the receiver uses rather
  than a second copy of them — a "test this" button that can disagree with the real
  path is worse than no button.
- **`git_list_webhooks` and `git_create_webhook`.** Listing is autonomous; creating one
  opens a path from outside the system to a workflow run, so it is single_confirm. The
  create tool returns the path, the header name and the secret, and says the secret is
  shown once — the half of the setup it cannot do is the half that happens on GitHub.

### Changed
- **The repository page has a Webhooks tab**, and the in-app docs and the `na_git` spec
  describe the endpoints. Two states are badged rather than left to be discovered from
  behaviour: a webhook with no secret and `allow_unsigned` set reads **unauthenticated**,
  and every row states what it fires for, because an unlisted branch is the reason a
  push "did nothing" far more often than a broken hook is.
- **The git skill teaches the two surfaces.** The chat tools and the `git` node run the
  same engine, and conflating them was the predictable failure — the skill now says
  outright that a workflow needing Git takes a `git` node rather than coming back to
  the conversation, and that `python_snippet` blocking `open` is a statement about the
  snippet sandbox, not about what a workflow can reach.

### Security
- **The signature is the authentication, and it is checked before anything else is
  read.** Comparisons are constant-time; an unknown route and a bad signature both
  answer 401, so the endpoint cannot be used to discover which webhooks exist; a
  webhook with no secret refuses every delivery unless an admin explicitly set
  `allow_unsigned`, because an unsigned route is an unauthenticated way to run a
  workflow. The route is random rather than derived from the repository, so guessing a
  repository name does not get you a target.
- **A retried delivery does not run twice.** The provider's own delivery id is stored
  with a unique index per webhook, so GitHub retrying after a timeout finds the
  recorded outcome instead of pulling and dispatching again — and the index, not the
  pre-check, is what decides when two retries race.
- **A rejected delivery is recorded at most once a minute.** The caller of a rejected
  delivery is by definition unauthenticated, so writing a row per attempt would let
  anyone who learns a route fill the table as fast as they can POST. One row per window
  still answers the question an operator has ("is GitHub reaching us at all?") without
  the amplification.
- **The auto-pull is bounded at eight seconds.** It runs inline because the run wants
  to see the commit that triggered it, but a first-time clone of a large repository
  would otherwise hold the request past the ten seconds GitHub allows. On timeout the
  delivery still dispatches and says so: the git node pulls on its own, so a slow clone
  delays the run instead of losing the push.

## 2026-08-20 — the agent can name the credential it can see

### Fixed
- **`list_credentials` returns `credential_id`.** It returned name, type, username and
  auth method — everything except the one field any consumer needs. `github_create_repo`
  requires the credential's uuid, so the natural request ("create the repo
  `git_nashira_test_uno` with the Github credential") ran `list_credentials`, got back a
  perfectly good credential, called `github_create_repo`, and failed with
  `credential_id is required (uuid of a token credential)`. There was no read tool
  anywhere in the catalogue that could turn that name into that uuid, so the turn ended
  with the agent asking the user to paste a uuid out of the UI — or offering to create a
  second, duplicate credential purely because `create_credential` happens to return the
  id it just generated. A credential the agent can see has to be a credential it can
  name. The tool now returns `credential_id` and takes `type`, `auth_method` and
  `name_contains` filters, so "the github credential" resolves in one autonomous call.

### Added
- **The `github_*` tools accept `credential_name` as well as `credential_id`.** Passing
  the id stays the unambiguous path and is what the tool descriptions push toward, but a
  name the user actually said now resolves too: case-insensitively, exact match first
  and substring only as a fallback, so `Github credential` is not shadowed by
  `Github credential (rotated)`. A name matching more than one credential is an error
  naming the candidates rather than a guess — resolving to the wrong token means pushing
  to somebody else's account, which is not a failure the user would catch in time.
- **`CredentialQuery`, the one lookup behind `list_credentials` and the `github_*`
  tools**, so the tool that lists credentials and the tools that consume them cannot
  disagree about what a name means. `CredentialQueryTests` pins the resolution matrix:
  by id, by name however it was typed, exact-over-substring, ambiguity, the unknown
  name, the missing reference, and both ways a deactivated row stays unreachable.
- **`github_create_repo` registers the repository it just created.** Creating on GitHub
  and registering here were two steps with nothing in between: the tool returned a clone
  URL, and no agent-facing tool could turn a clone URL into the `repository_id` that
  `git_write_file` and `git_pull` take. "Create a repo and put a README in it" — the most
  common git request there is — died one step in, on a repository that already existed.
  Registration is now part of the call (`register: true` by default, `register_name` to
  override the local name) and the result carries `repository_id`. When only the
  registration fails, the call still succeeds with `registered: false` and
  `register_error`: the GitHub repository exists at that point, and reporting a failure
  would describe a rollback that did not happen.

### Changed
- **The git and inventory skills teach the id-first flow.** The git skill said outright
  that creating a repository does not register it, which was true and left the agent
  with no next move; it now documents the `list_credentials` → `github_create_repo` →
  `git_write_file` recipe end to end, including not asking the user for a URL or an owner
  for a repository that does not exist yet.
- **The repository form steers to a credential that can actually authenticate.** The
  transport is HTTPS + PAT — `IsAllowedUrl` rejects `ssh://` and `git://`, and
  `BuildAuthHandlerAsync` reads the credential's token — but the form offered every
  stored credential, including SSH keys that cannot be used at all, and accepted an SSH
  remote that the server would reject on save. It now filters the picker to token and
  password credentials, flags an SSH-looking URL while it is being typed, and validates
  the scheme before the round trip. `CredentialPicker`'s hidden-credentials note names
  the methods the caller actually accepts instead of restating the HTTP-auth set
  verbatim, which read as a lie on the git form.

## 2026-08-20 — an allowlisted module is actually importable

### Fixed
- **The python_snippet import guard no longer blocks an allowed module's own
  dependencies.** The sandbox harness replaces `builtins.__import__` to enforce the
  admin allowlist, but the hook intercepted every import in the process — including the
  ones an approved module makes internally. `collections` imports `heapq`, `datetime`
  imports `time`, `csv` imports `io`, and none of those internals are on the allowlist
  (the seeder deliberately ships user-facing modules, not CPython's implementation
  details), so importing an approved module raised
  `ImportError: module 'heapq' is not on this deployment's allowlist` from inside a
  snippet that never mentioned `heapq`. Worse, it was intermittent by construction:
  whatever the harness's own `import json` chain happened to leave cached in
  `sys.modules` imported fine, so the same snippet worked or failed depending on the
  interpreter version. This is what was breaking scheduled workflow steps whose modules
  all showed `ready` in Admin → Python modules. The guard now enforces the allowlist
  only for imports the snippet itself wrote — code executing with the snippet's
  environment as globals, or a bare `__import__` with none — and lets an approved
  module's transitive imports through: the admin vetted the module, and how it is
  implemented is not a new capability the snippet asked for. This also unbreaks pip
  packages that import their own requirements. `import os` from a snippet is still
  refused.

### Added
- **A sandbox test suite that runs the real interpreter.** `PythonSandboxTests` pins the
  executor-side guarantees a workflow author leans on: input arrives typed and
  addressable, `result` round-trips typed, prints stay in stdout without corrupting the
  result envelope, a snippet exception surfaces as a legible `script_error`, an
  unresolved `{{ … }}` template left literal by the resolver fails inside the snippet
  naming the type error, the guard still blocks off-allowlist imports, a runaway loop
  dies at the deadline, and — the regression that found the bug above — a module on the
  allowlist imports together with its dependencies. Two tests bracket the stdout cap on
  purpose: a 100 KB result round-trips intact, and a result larger than the 200,000
  character cap can never survive, because the envelope travels over stdout and the
  reader kills the child at the cap. If that second test ever fails because the big
  result came back whole, the cap was raised or rerouted — delete the test with the fix.
  Every test no-ops on a machine with no Python on PATH so the suite still passes there.

## 2026-08-20 — the vendor command catalogue ships with commands in it

### Added
- **Seeded vendor command catalogues for twelve platforms.** `vendor_commands` is what
  `GET /api/vendor-commands/resolve` reads, and it is the entire mechanism behind "one
  workflow, many vendors": a node asks for the intent `show_ip_interfaces` and the
  device's platform decides whether that is `show ip interface brief` or `show interfaces
  terse`. The table shipped empty. The endpoint, the admin screen, the `na_snippets`
  operations and the snippets skill were all in place — the skill even tells the agent to
  resolve intents "instead of hard-coding `show version` per vendor into seven parallel
  nodes" — so the one instruction that made a workflow portable was the one instruction
  that could not be followed. Every resolve call 404'd on a fresh install, and what the
  agent did instead was the thing the skill told it not to.
- **`Skills/vendors/*.yaml` and `VendorCommandSeeder`.** Catalogues for `cisco_ios`,
  `cisco_xe`, `cisco_xr`, `cisco_nxos`, `cisco_asa`, `juniper_junos`, `arista_eos`,
  `nokia_sros`, `nokia_srl`, `huawei`, `fortinet` and `linux`, keyed
  `(intent, platform)`. Adding a platform is a file, not a recompile. Seeding is
  idempotent per key and counts soft-deleted rows as taken, for two reasons: the unique
  index on `(Intent, Platform)` is unfiltered, so inserting over a removed row would
  throw, and an operator who deleted `save_config` for a platform decided something a
  redeploy must not quietly reverse.
- **Bulk delete on the workflows list.** The per-row control added earlier the same day
  left clearing out a list of twelve drafts as twelve confirmations. Selection is
  Operator-gated like the row control, deletes run sequentially so a partial failure can
  name what survived, and the confirmation lists the promoted copies by name and
  environment rather than only counting rows — twelve drafts, and eleven drafts plus one
  production workflow, look identical at a glance otherwise.

### Fixed
- **`Skills/vendors/` would have been read as prompt text.** `SkillPromptLoader`
  enumerates `Skills/**/*.md` recursively, so the README documenting the catalogue schema
  would have been concatenated into every system prompt and listed in Admin → Skills as an
  editable built-in. The loader now skips that subtree, `PUT /api/skills/builtin/{name}`
  cannot reach into it, and `BuiltinSkillTests` no longer holds it to the skill rules.

### Changed
- **The snippets skill describes the catalogue that now exists.** It names the seeded
  platforms and the core intents, says to prefer `device_id` over `platform` when
  resolving inside a workflow, and states what the two kinds of miss mean: `aruba_aoscx`,
  `aruba_osswitch`, `hp_comware`, `mikrotik_routeros`, `risecom_ros` and `generic` ship no
  catalogue because their syntax has not been verified — resolving there 404s on purpose
  rather than borrowing a neighbouring vendor's syntax — and a 404 on a seeded platform
  means the intent is missing from the catalogue, not that the device cannot do it. The
  object-model table in the Nashira skill gained the row it was missing.
- **`Checkbox` accepts `ariaLabel`.** A per-row selector carries no visible text, and
  without a name it reaches a screen reader as an unnamed control.

## 2026-08-20 — deleting a workflow from the UI

### Added
- **A delete control on the workflows list and on a workflow's own page.** `DELETE
  /api/workflows/{id}` has been there since the controller was written, the in-app API
  docs listed it, and the agent has had `delete_workflow` as a tool — but the screen a
  person actually uses had no way to remove one. The result was a list that only ever
  grew, and the workaround was to ask the assistant to delete something the user was
  looking at.
- **The confirmation names the environment and the version.** The API deletes a promoted
  copy as readily as a draft, so "Delete workflow?" on its own does not tell someone they
  are about to remove the production one. The prompt says which copy, and says what
  survives: the row is soft-deleted, so past runs and the audit trail stay readable and a
  copy promoted into another environment is untouched.

Both controls are behind the Operator gate, matching the endpoint. From the list the row
disappears in place; from the detail page it returns to the list, since staying on a page
whose subject no longer exists is not a state worth rendering.

## 2026-08-20 — skills audited against the code they describe

### Fixed
- **The baseline python snippet could not run.** Its body read `payload`, and the sandbox
  harness binds the node's resolved `config_overrides` as `input` — the only name it
  binds. Every copy raised `NameError` on the first line that used it, which reads as
  "the python step is broken" rather than "the example is wrong", and it was the *seeded*
  example, so it is what an agent copies when asked for a python step. Seeding is
  idempotent by slug, so a redeploy alone would never have replaced it: the seeder now
  also repairs an existing `baseline-python-step` row still carrying the broken line, and
  only that line, so an operator who edited the baseline into something real keeps it.
- **`export_table` refused the format it had just gained.** The renderer, the service and
  the exports skill all offer `pdf` for a table; the tool's parameter schema was never
  updated, so the one route the agent has rejected it and the agent went back to
  substituting a format and apologising — the exact failure the previous release was
  written to end.
- **Device pools were unreachable from the agent.** The inventory skill sent it to
  `/api/device-pools` through `execute_operation`, and no shipped spec described that
  endpoint — the controller has existed for releases, catalogued nowhere. `na_inventory`
  now carries the six operations, `inventory_devicePoolMembers` among them, so the
  question that actually matters ("what will this run touch?") has an answer: pass
  `environment` and it returns `pool_allows_environment` plus the devices excluded by
  their own trio, rather than a membership list that is not what a run would touch.
- **`list_snippets` promised an ordering it did not deliver.** Its own description says
  results come back proven-first, and `proven` is resolved from run history rather than a
  column — so it cannot be an `ORDER BY`, and the page was being taken *before* it was
  known. With more snippets than fit on a page that meant "the first N by name, of which
  the proven ones came first": the snippet that has actually worked here, three pages
  down alphabetically, never surfaced and the agent wired in an unproven one that sorted
  early. The query now scans a bounded window (500 rows), resolves `proven` over it, and
  pages after ordering. `total` is the number of matches rather than the size of the
  page, and the response says so when the window truncated the catalogue.

### Added
- **A skill for the tools that had none.** Eleven registered tools were documented
  nowhere in the prompt: the LLM providers, the assistant profiles and the template
  validator. `administration.md` covers all three, and the profile section is mostly a
  correction — a profile's `skills` and `response_style` are stored, served and shown in
  the admin UI, and read by nothing when the system prompt is composed. Assigning one
  does not change how the agent behaves, so the skill says to offer a prompt skill
  instead of letting someone configure a persona that will never take effect.
- **The two specs nothing named.** `na_ai_meta` (the agent's own prompt skills, spec
  catalogue, providers and conversations) and `na_governance` (users, permissions,
  policies, credentials, secrets) were shipped, seeded and invisible: no skill mentioned
  either, so the agent could not discover that it is able to inspect its own prompt or
  dry-run a policy. Both are now named where they belong.
- **Three tests over the skills directory, which had none.** The skills are prose about
  an API rather than code that calls one, so drift is silent by construction — that is
  how `/api/device-pools` survived. Now: every `/api/…` path a skill names must be
  described by a shipped spec; every `na_*` spec it names must exist; and every skill
  must pass the security validator, because editing one through
  `PUT /api/skills/builtin/{name}` runs that same validator and a shipped file that
  trips a rule cannot be saved back by the admin who just opened it. The last of those
  caught the new administration skill on its first draft, which described the
  prompt-injection rules by quoting them.

### Changed
- **The skills now say what the code does.** Each built-in prompt skill
  was read against the handlers, schemas and specs it describes. The drift that mattered:
  `transform`'s `source` argument and its mapping-in-`code` form were undocumented, and
  so was the difference between a partially-resolved mapping (succeeds, reports) and a
  fully-unresolved one (fails); `ping` was described as never failing when it still
  fails on a host it cannot resolve; the SSH classification omitted `delete` and `format`
  from the destructive list, which is the difference between a command an operator
  expects to run and one the platform blocks; the promotion refusals omitted
  `approval_required`; the acceptance-test endpoints were named without saying they live
  in the `na_triggers` spec, where nobody would look for them.
- **The python snippet's contract is written down.** `input` in, `result` out, and the
  handler wraps it as `{"result": …, "stdout": …}` — so a downstream node reads
  `{{ steps.<node>.output.result.… }}` and `output.…` silently resolves to nothing. Three
  facts an author cannot discover from the tool schema and could previously only learn
  from a failed run.
- **`na_snippets` is named operation by operation.** The skill said the spec covered
  "everything past create-and-list" without naming a single `operationId`, which is a
  discovery round-trip per turn. Update, delete, the vendor-command catalogue and the
  python allowlist are now listed by name.
- **The idempotency override is described as the code implements it.** A snippet's
  declaration wins in either direction over the handler's default, not only downward; the
  absolute part is that a `non_reversible` handler stays `non_reversible`. The
  `ISnippetHandler` comment said the opposite of `Idempotency.Effective` and now agrees
  with it.
- **Built-in skills are not tenant skills.** `list_skills` returns only the uploaded
  rows, so an agent asked about its own prompt reported having none. The platform skill
  now points at `aimeta_listBuiltinSkills` on the `na_ai_meta` spec for the shipped
  files, and says they are edited from Admin → Skills rather than through the tool.
- **Learnings say which key holds the hint.** An `escalate` learning is read from
  `fix_params.message`; the base prompt asked for "an escalate hint message" without
  naming the key, which produces a stored learning that matches and then escalates
  nothing.

## 2026-08-20 — pdf and document exports

### Added
- **PDF, and a way to produce a report at all.** Asked for "a report in PDF with the
  answer and the workflow information", the agent answered that PDF was not available and
  handed over an HTML table to print from the browser. It was right about the formats —
  there was no PDF anywhere in the product — but the deeper gap was that a *report* had no
  tool behind it. `export_table` takes an array of flat objects and nothing else, so prose
  had to be bent into rows, and the summary of a workflow came out as a two-column table of
  field names. `export_document` takes the report as markdown — headings, paragraphs,
  lists, pipe tables, fenced code, quotes, bold, inline code, http links — and renders it
  as a paginated A4 PDF, a self-contained HTML file, or markdown. `export_table` gains
  `pdf` too, for a table meant to be read rather than edited, and both go through the same
  renderer so the two come out looking like the same product.
- **Documents are rendered, not printed.** PDF generation is QuestPDF (community licence,
  registered once at boot; the version flow-weaver already runs). It ships its own native
  renderer and bundles the Lato font, so nothing was added to the runtime image except
  `fonts-dejavu-core` — without a monospace font on the image, the code blocks in a report
  fall back to a proportional face and pasted CLI output loses the alignment that made it
  worth pasting. Verified inside `mcr.microsoft.com/dotnet/aspnet:10.0`, which is where
  this actually has to work.
- **Standard ligatures are off in the PDF.** Lato renders `fi`/`fl`/`ti` as single glyphs,
  which looks better and then quietly ruins the document: searching a reader for
  `workflow_id` finds nothing, and copying a command out pastes a glyph no shell
  understands. These reports are mostly identifiers, so accuracy wins over typography.

### Changed
- The exports skill now says a file is always possible and which tool produces which
  format, because the failure was not only missing code — the agent had also learned to
  substitute a format and apologise. Reports go to `export_document`, rows to
  `export_table`, and the in-app docs no longer describe exports as spreadsheets only.

## 2026-08-20 — chat downloads

### Fixed
- **Every file the chat produced was unreachable from the chat.** `export_table` writes
  the artifact, returns a `download_url`, and the agent hands it back as a markdown link —
  which the chat renderer then threw away. Links have been stripped to plain text since
  the renderer was lifted from flow-weaver, deliberately: a model-emitted `<a href>` is a
  phishing vector, and at the time no link the agent wrote was ever meant to be clicked.
  The export tool made one, and nothing was adjusted, so "Here is your file:
  interfaces.csv" rendered as exactly that — a file name, no click target, no download.
  The download endpoint is now the single whitelisted href: `/api/export/{id}/download`
  (with or without an origin, and also when written loose in prose) renders as a download
  button. Every other link — external, `javascript:`, another path on this API, or a
  lookalike host carrying the export path in its query string — is still reduced to text.
- **The href alone would not have worked either.** `/api/export/{id}/download` is
  `[Authorize(Policy = "Viewer")]`, and a plain anchor navigates without the bearer token,
  so an unstripped link would have answered 401 rather than downloading. The button fetches
  through the API client — bearer, refresh-on-401, typed errors — and saves the blob from
  memory, which is what the exports page has always done.
- **Downloads could be cancelled the instant they started.** The object URL behind a saved
  blob was revoked synchronously after `click()`; Firefox and Safari have not necessarily
  read the blob by then, so the file silently never arrived. It is released on a timer now.
  Saved files also take the name from the response's `Content-Disposition` when the caller
  has none of its own — a download started from a chat link knows the artifact id and
  nothing else, and would otherwise land as `export-<uuid>`.

### Changed
- The exports skill now states how to hand a file back: a markdown link whose text is the
  `file_name` and whose target is the `download_url` verbatim. A retyped or shortened path
  does not match the whitelist and comes out as dead text again.

## 2026-08-20 — snippets

### Fixed
- **The agent could not put a single real step into a workflow, and nobody noticed
  because the empty ones looked like successes.** A workflow node binds to a *snippet*,
  never to a handler type, so the only way to author one is to read a snippet id off the
  catalogue. The agent had no tool for the catalogue: the snippets skill sent it to
  `execute_operation` against the `na_snippets` spec, a self-API call that failed six
  times in a row on "spec 'na_snippets' has no base_url configured". Unable to obtain an
  id, it authored around the gap — a hundred stored workflows whose only nodes are
  `__start__` and `__end__`, and one that invented `__ping__` and failed at run time with
  `unbound_node`. Every one of those ran, reported no-change for each node, and finished
  green, which is indistinguishable from a workflow that did its job and found nothing to
  change. Discovery and creation are now native tools — `list_snippets` and
  `create_snippet` — running in-process on the chat turn's own scope, so neither depends
  on a base URL, a borrowed bearer, or any configuration at all. (The base-URL failure
  itself was fixed the day before, in "Make integrations, workflows, themes, and runs
  safer"; what remained was that the route was three hops long and silently optional.)
- **A fresh install had nothing a node could legally reference.** There was no baseline
  catalogue, so the first authoring turn always had to create a snippet before it could
  create a workflow — and if that failed, the shell got stored anyway. One runnable
  snippet is now seeded per registered handler at boot. Idempotent by slug rather than by
  type, deliberately: this deployment's snippets table already held forty-five `ping` rows
  left by the e2e suite, and a type match would have concluded ping was covered while
  leaving no usable ping snippet behind.
- **Snippet creation validated differently depending on how you arrived.** The REST
  controller held the type check, the name-collision check, the slug allocation and the
  admin gate on `network_enabled` inline, so a second write path meant a second copy kept
  in step by hand. All four now live in `SnippetCatalog`, shared by the controller, the
  agent tool and bundle import — a snippet one route accepts is one the others would
  accept too.

### Added
- **Workflows now cross between instances.** A workflow exported as YAML carries this
  installation's snippet UUIDs and nothing else, so importing one anywhere else was
  refused outright by the reference gate — which is what happened to every Flow Weaver
  workflow brought here. `GET /api/workflows/{id}/bundle` exports the portable form:
  nodes verbatim, plus the full definition of every snippet they name.
  `POST /api/workflows/import` detects a bundle by its `kind` marker, resolves each
  snippet by slug and then by exact name, recreates what is absent from the definition
  that travelled, rewrites the node references, and runs the result through the same
  schema, acyclicity and reference validation as any other write. The format is Flow
  Weaver's `flow_weaver.workflow_bundle` v2 rather than a Nashira-specific one, so
  bundles move in both directions.

  Three things it refuses to do, each on purpose. It never grants `network_enabled` — a
  snippet that had it arrives without it, and `import_notes` says so, because lifting the
  python sandbox's network isolation is an admin act performed here and not something a
  file can carry in. It never invents a missing integration or MCP server; the refusal
  lists exactly what is absent, because a fabricated stub produces a run that goes green
  having done no work. And it refuses a snippet whose handler type this build cannot
  execute — Flow Weaver ships `ansible`, `netconf` and `snmp_v3` — rather than storing it
  to fail later on a device. Imports always land in draft with no simulation link,
  whatever environment the source held, so the promotion gates apply in full.

- **A built-in spec seeded before an endpoint existed described that older API
  forever.** The seeder only ever inserted: a row whose `api` name was already present
  was skipped whole, so the shipped file could gain paths and parameters that never
  reached the catalog the agent actually reads. Two of the thirteen had already drifted
  — `na_workflows` had no `/bundle` or `/import`, and `na_admin_readonly` went a release
  without the `entityId`, `actionPrefix`, `actor`, `from` and `to` filters its endpoint
  had gained. For the agent, a parameter missing from the spec and a parameter missing
  from the API are the same thing.

  These documents describe this build's own endpoints, so the file is the source of
  truth and the row is a cache of it — but refreshing unconditionally would have been
  the same bug pointing the other way, silently reverting an admin who trimmed
  operations out of a spec to save agent context. Each row now records the hash of the
  file it was written from, and is refreshed only while its content is still exactly
  what we wrote; once someone edits it, their version stands. Third-party specs have no
  shipped file behind them and are never touched, and a spec deleted on purpose is
  never revived. Rows predating the column are adopted once and tracked from then on —
  the boot log names each one, because that single pass is the only moment the seeder
  can overwrite an edit it had no way to see.

### Changed
- **The snippets and workflows skills no longer send the agent to `execute_operation`
  for the catalogue,** and both now state plainly that a workflow of nothing but
  `__start__` and `__end__` is not a draft to refine later but a graph that will run and
  do nothing. `list_snippets` reports `proven` — whether a step using that snippet has
  actually completed on this instance — and orders proven-first, so a half-finished
  experiment is not picked over a working equivalent sitting in the same list.

## 2026-08-19 — git issue

### Fixed
- **Deleting an integration left its specs and actions behind, holding an api name
  nobody could reach.** The delete flipped one flag: the integration vanished from every
  list while its specs stayed active and still pointed at it. The name was then reserved
  against the unique index forever, the catalog's only advice was to "unlink that spec
  first" from a page that no longer opens, and the global spec list — filtering on
  `IntegrationId == null` — did not show it either, because an orphan does have an owner
  id. Invisible, uncallable, and blocking the name. Deleting now releases the specs and
  actions it owns and refreshes the agent's catalog; "global" means "not owned by an
  integration anyone can open", so an orphan created before this surfaces where it can
  be dealt with; and attaching a spec over one whose owner is gone reclaims it instead of
  refusing. When the owner really does exist, the refusal now names it.
- **Creating an integration whose name was held by a deleted one returned a 500.** The
  application checked uniqueness among live rows; the database index enforced it across
  all of them, deleted included. So the check passed, the insert hit the index, and the
  caller got a stack trace instead of a sentence. The index is now partial on `IsActive`,
  which is what the application always meant — a soft-deleted row has no business
  reserving a name nobody can see. A unique violation on the integrations table is also
  translated to a 409 now, so a lost race or a future path that forgets to check cannot
  produce a 500 again.

### Changed
- **A new integration starts on the `token` auth method, not `none`.** Flow Weaver's form
  has always defaulted this way, and it is why the same NetBox token works there without
  anyone thinking about it. Defaulting to `none` made the common case — an integration
  that authenticates — the one requiring a deliberate choice, and the first option on
  offer was the OAuth-style scheme, which is wrong for the Django REST Framework APIs
  this tool talks to most. `token` is also listed before `bearer` now, matching Flow
  Weaver's ordering. Editing an existing integration still shows whatever it has.
- **The edit dialog now says where skills and specs live.** It hides their tabs, because
  attaching to a saved integration takes effect immediately rather than on save — but it
  hid them silently, leaving the panel that owns them reachable only from a book icon
  among four icon buttons in the row. Anyone who did the obvious thing and opened Edit
  found nothing and concluded it was not possible.

### Added
- **The theme editor only ever recolored things.** Seven color pickers and a preview,
  which is most of a theme and none of its shape: the corners, the type and the size of
  the interface were fixed, and the only way past them was editing `app.css` and
  redeploying. A theme now carries **style settings** alongside its colors — corner
  roundness, interface scale, body/heading/code font, heading weight. Every one is
  optional and an absent key inherits `app.css`, so the themes already saved behave
  exactly as they did; a knob parked on its default is not written at all, which keeps
  it tracking the design system instead of pinning today's value.
  They are wired to Nashira's *own* tokens rather than to a framework's: roundness
  scales `--srf-radius`, `--ctl-radius` and `--btn-radius` (what `.ui-surface`,
  `.ui-control` and `.ui-raised` are built from) together with Skeleton's
  `--radius-base` / `--radius-container` and Tailwind's `--radius-*` scale — the app
  reaches for `rounded-lg` about three times as often as for the `.ui-*` primitives, so
  moving only one group would have rounded the cards and left every badge square.
  Interface scale moves the root `font-size`, which every rem-based size follows. The
  fonts are a closed list of stacks the bundle already carries (IBM Plex Sans/Mono, via
  `@fontsource`) plus system-safe ones; there is deliberately no free-text font field,
  because a shared theme with one would make every reader's browser fetch a third
  party's asset. `--heading-font-family` and `--heading-font-weight` were declared in
  `app.css` and consumed by nothing — the `h1`–`h6` rule restated `650` as a literal —
  so they are now read through, which is what makes the heading knobs do anything.
- **Somewhere to start, and a way out.** The editor gained seven curated presets
  (Nashira, Midnight, Ember, Moss, Orchid, Graphite, High contrast), a *Surprise me*
  roll, per-theme **Duplicate**, and JSON **import/export**. The randomiser is
  constrained rather than free: one brand hue drives primary and the surface tint, the
  accents sit at deliberate offsets, and success/warning/error stay inside their
  conventional bands — a run list is read at a glance for "healthy / degraded / down",
  and a palette that makes failures teal costs more than it buys. Success is taken from
  whichever end of the green band sits furthest from the rolled brand hue, preserving
  the ~50° separation `app.css` holds between the two so they do not read alike as the
  low-opacity tints alerts and badges use. Duplicate is also how somebody starts from a
  shared theme they are not allowed to edit.
- **Style settings are validated on the server; colors still are not.** `Theme` gained a
  `SettingsJson` column (migration `Phase11_ThemeStyleSettings`, backfilled `{}`), and
  `ThemeController` checks it against a closed vocabulary: roundness 0–2, interface
  scale 0.85–1.15, heading weight 300–900, fonts against fixed lists, and an unknown key
  is a `settings_unknown_key` error rather than a silent drop. The asymmetry with
  `ColorsJson` is deliberate and is written down next to both: a color can only ever
  come back out as a color and which token names exist is the frontend's business, while
  these six values are interpolated into `font-family` and `border-radius` declarations
  on a page every user loads the moment a theme is shared.

### Fixed
- **A NetBox integration could be configured in a way that cannot work, and nothing said
  so.** The two token-carrying auth methods were labelled "Bearer token" and "Token with
  a custom scheme", which sorts them by how exotic they sound rather than by what they
  do — so somebody holding a NetBox API token picks the first, and NetBox answers
  `403 authentication credentials were not provided`. That message is not about a bad
  secret: Django REST Framework only recognises the scheme word `Token`, and anything
  else reads to it as no credential at all. Both labels now lead with the wire format
  (`Authorization: Bearer <token>` / `Authorization: Token <token>`) and name who uses
  which. The combination is also refused at write time for a type known to reject it.
- **`healthy` meant "the host answered", not "our credentials work".** With no
  `health_check_path`, the probe hits the base URL — which for NetBox is its web UI,
  answering an anonymous caller with a 302 to the login page. 3xx counts as healthy, so
  an integration whose every API call was being refused reported healthy on the strength
  of a redirect to a login form, and `has_credentials: true` only ever said a credential
  was stored.
  Known types now fall back to a path that requires authentication (`/api/status/` for
  NetBox), so the probe fails when the credentials do.

### Changed
- **A refusal that looks like a scheme problem now says so.** When an upstream answers
  401/403 with "credentials were not provided" *after* Nashira attached an
  Authorization header, the executor logs `rest.executor.auth_scheme_suspect` and
  returns a `diagnosis` alongside the status. It reaches the agent as well as the log —
  it is the agent that decides what to try next, and a bare 403 sent it reading the
  spec instead of the one field that was wrong.
- **A workflow whose nodes referenced nothing real ran green.** `SnippetNodeExecutor`
  treated any `snippet_id` that was not a UUID as an unimplemented node type: it logged
  `node.unbound` and returned `no_change`, so the DAG kept walking and the run finished
  `completed` with "0 changed, 0 failed". That is the same summary a workflow produces
  when it does its job and finds everything already correct — the two were
  indistinguishable. An agent-authored workflow referencing `__ping__` executed nothing
  at all and reported success. An unresolvable reference now fails the node with
  `unbound_node`, matching what a UUID that resolves to nothing already did, and the
  message names both the node and what it pointed at. `__start__` and `__end__` stay a
  genuine no-op. No test covered this path, which is why it survived; there are now six.
- **A finished run never said what it produced.** The engine has always stored each
  node's output and the API has always carried the run's input and target devices —
  `StepRunResponse` did not expose the output, and the client dropped the input and
  targets on the floor. On a read-only workflow the output *is* the result, so the run
  detail could only report `no_change` per node and nothing about what any of them saw.
  A failed node was worse: `error_code` names a category and the message explaining it
  was in the output nobody could read.
- **The agent could read its own API and not use it.** Nashira ships thirteen OpenAPI
  documents describing its own endpoints — 161 operations covering triggers, snippets,
  workflows, runs, everything the agent needs to actually build something. They were
  seeded with no base URL and `auth_type: none`, so `list_apis`, `discover_operations`
  and `operation_detail` all worked and `execute_operation` answered `spec 'na_triggers'
  has no base_url configured` for every one of them. The behaviour was a catalog the
  agent could browse and never call, and since there is no native trigger handler to
  fall back on, asking for "a workflow that pings 4.2.2.2 every hour" produced a
  workflow built around a snippet id the model invented because listing the real ones
  had bounced. The base URL came from `Ai:SelfBaseUrl`, which was empty in
  `appsettings.json` and unset in `docker-compose.yml`; it now defaults to
  `http://localhost:8080`, the container's own Kestrel — the same address the compose
  healthcheck already proves answers. The setting remains for installs that answer
  somewhere else, and a trailing `/api` is stripped from it, because the shipped paths
  are absolute and already carry one.
- **The self-call authenticates as the user who is talking.** The bearer for those specs
  is `${secret:session:current:jwt}`, a new reference the resolver understands: with an
  HTTP request in flight it forwards that request's own token, and on the background
  paths — messaging channels, scheduled agent runs — it mints a short-lived one for the
  identity bound on `ICurrentUser`, carrying that identity's roles and nothing more.
  This is deliberately not a service token. Per-user tool permissions became real one
  commit ago, and a long-lived credential of its own would have handed the agent a
  ceiling above the person it is answering; instead every check the platform already
  performs applies to the agent's calls unchanged, and it can reach nothing the user
  could not reach themselves. The credential belongs to whoever is chatting rather than
  to the admin who configured the spec, so it resolves only for requests the executor
  has established land on this backend — an admin who pointed a spec at a host they
  control and put the reference in its auth config would otherwise have harvested every
  user's token as they used the agent. Everywhere else in the codebase the marker stays
  literal. For the same reason the SSRF guard is skipped for self-calls specifically,
  rather than relaxed: loopback is blocked outright and `allow_private_network` does not
  bypass it, and opening either would have widened every other spec in the catalog.
  Seeded rows are upgraded in place, since the seeder is keyed on api name and could
  never have reached the thirteen that already exist — but only rows that are ours, are
  not deleted, and are untouched in every field the upgrade writes, so its standing
  promise not to revert an admin's edit still holds.

### Changed
- **The run detail panel shows the run.** Per-step output, expandable, with failed steps
  opened already. The failure message is pulled onto its own line. Counts now include
  skipped and no-change rather than only changed and failed, a run with no changes says
  so explicitly, and the elapsed time and trigger input are on the panel.
- **Every long AI turn died at 100 seconds with a raw .NET error.** The `llm`
  `HttpClient` was registered without a timeout, so it kept the framework default of
  100s — twenty seconds *under* the runner's own 120s turn deadline. The transport won
  that race every time, and the deadline it was supposed to back up became decoration:
  instead of `the turn exceeded the 120s deadline`, its partial text and a `timeout`
  turn status, the user got `The request was canceled due to the configured
  HttpClient.Timeout of 100 seconds elapsing` and the turn was recorded as an error.
  Asking the assistant to build a workflow and a schedule for it — a chain of tool
  calls, each a round-trip to the model — hit this reliably. The transport timeout is
  now a backstop at five minutes, and a test asserts the invariant that broke: it must
  outlive the turn deadline, with margin rather than a shave.

### Changed
- **The AI turn budget is configuration, not constants.** `StreamDeadlineSeconds`,
  `MaxIterations` and `DeadlineWarnPercent` moved out of `AgentConversationRunner` into
  an `AiChat` section, overridable per environment (`AiChat__StreamDeadlineSeconds`).
  The iteration cap rises from 10 to 50: it is runaway protection, not a budget, and the
  deadline is the honest limit because iterations vary wildly in cost — a cap of 10 cut
  legitimately deep tasks off mid-chain for reasons unrelated to how long they took.
- **A turn now warns before it busts its deadline.** At 75% of the budget the runner
  logs `ai.chat.deadline_warning` with the elapsed time, iteration count and tool-call
  count. A deadline that is simply too small for the work was previously only visible
  after it truncated somebody's answer.
- **The throughput objective was missed on every idle install.** It divided succeeded
  jobs by elapsed hours, so a platform nobody had asked to do anything reported a rate
  of zero and failed the objective every day — with the daily sweep dutifully writing a
  breach row about work that was never requested. This is precisely the false alarm the
  breach rule's null handling exists to prevent, reintroduced one file later by
  measuring it that way. Throughput now needs a denominator of demand: with nothing
  queued there is no signal, and once work exists a low rate is a real problem again.
- **The git integration had never worked in Docker.** `Git:Root` resolves to
  `/app/data/git`, which the `backend_git` named volume mounts over — but the directory
  did not exist in the image, so Docker created the mount point as `root` while the
  backend runs as `app`. Every clone, commit and checkout failed with a permission
  error, and the directory had sat empty since the volume was created. The Dockerfile
  already carried a comment explaining this exact failure mode, written for
  `/app/pyenv`; git was simply left out of the `mkdir`. Both the image and the existing
  volume are corrected — the volume in place rather than recreated, since nothing was
  ever written to it to lose.

## 2026-08-19 — runs and schedules across the fleet

Everything about a run already existed, but only underneath the workflow that owns it.
That answers "what has this one done" and cannot answer "what happened last night" —
which is the question the day starts with, and the one the dashboard's top-failing list
pointed at without being able to open.

### Added
- **`/runs` and `GET /api/runs`** — every execution across every workflow, filterable by
  status, environment, outcome and workflow name. Each row carries the workflow's name
  from the server; resolving names in a second round of requests is a poor trade on the
  page you open when something is already wrong.
- **`/schedules` and `GET /api/schedules`** — every trigger, cron and webhook alike,
  ordered by what fires next. **Overdue is decided server-side**: a cron whose next
  firing is in the past means the scheduler has not been round to it, and a client
  comparing against its own clock would call everything overdue the moment a laptop's
  time drifted. The page links straight into the scheduler's traces.
- Both are in the sidebar under Operate, beside Workflows.

### Changed
- **The `Connect` group is now `Integrate`, and the `Connections` section is
  `Integrations`**, matching flow-weaver — the group is the verb, the surface inside it
  is the noun.
- **Filtering is server-side.** Flow-weaver's runs page narrows the current page in the
  browser while the pager still counts the whole server set, which its own comment
  admits; the list then says "3 results" beside a total of 412 and the reader believes
  the smaller number. Here the count describes the same set as the rows.
- **Schedules are one query, not one per workflow.** Flow-weaver assembles this page in
  the browser by listing the workflows and then fetching each one's triggers — a request
  per workflow, on a page whose whole purpose is to be opened when you do not yet know
  which workflow is involved.
- **A run whose workflow has been deleted stays in the list**, under "(deleted)". The run
  still happened, and dropping it would quietly rewrite the record every time somebody
  tidied up.
- **A run still going reports no duration rather than zero.** Zero reads as an instant
  run and sorts to the top of "fastest".
- The runs list shows the outcome beside the status: a failed run that **rolled back** is
  contained, one that **left changes behind** is not, and the status alone does not carry
  that difference.

### Fixed
- **The name search would not have been covered by any test.** It was written with
  `EF.Functions.ILike`, which is Npgsql-only and does not translate on the in-memory
  provider — so the two tests over it failed and the path would have shipped unverified.
  Rewritten as a lower-cased `Contains`, which translates on both. The comment
  justifying `ILike` ("so the column's index is still usable") was also wrong: there is
  an index on `Workflow.Name`, but a substring search begins with a wildcard and cannot
  use it however it is written.

## 2026-08-19 — traces and settings

The last two screens of flow-weaver's admin panel. Neither is a copy, and the reasons
differ: one had a real gap to fill, the other had four controls that would have
controlled nothing here.

### Added
- **`/admin/traces` and `trace_events`** — the third trail, and each of the three
  answers a question the others cannot: `audit_events` says what **changed**, with
  before/after, hash-chained; `auth_events` says who signed in; this says what
  **happened**, including everything that changed nothing. The gap it fills is work
  with no HTTP request behind it — a job the worker claimed and never finished, a cron
  that came due while the platform was down, a pip install that hung, a tool call the
  agent was refused. None of that mutates an entity, so none of it reached the audit
  trail, and the only record was a container log that scrolls away.
- **Start→complete pairs with a measured duration.** A `started` row with no completion
  is the whole design: work that is stuck looks exactly like work that never began
  unless something wrote down that it began. Both halves share an id, so a pair that
  arrives together collapses into one row — meaning only slow or stuck operations leave
  a `started` row visible, and a screen full of them is a screen full of problems.
- **Instrumented**: HTTP (mutations, and every failure on any verb), agent turns, every
  tool call including the refusals, the job worker, expired leases, cron firings and
  stale skips, pip installs, retention sweeps and the SLO sweep.
- **`/admin/settings` over the `system_settings` table** Nashira already had — with its
  display metadata, empty, unread and unwritten since the migration that created it. The
  screen renders from the rows, so adding a setting is a backend change.
- **`requestId` as an audit filter.** Both tables have stored the value since their first
  row and neither could filter on it, so the correlation they were built for did not
  exist. Traces now links across: *see what this request changed*.

### Changed
- **Trace writes are buffered, not inline.** Flow-weaver inserts each row as it happens;
  at this scope — a row per mutating request, plus start and completion for every job
  and tool call — that puts an extra database round-trip on the critical path of
  everything the platform does, in order to record that the platform did it. Rows go
  into a bounded queue drained in batches by its own scope, never the request's. The
  queue drops the **oldest** row when full: a burst that outruns the writer loses its
  beginning, and the end is where the failure is. Rows buffered at a hard kill are lost,
  which is the right trade — the evidence lives in `audit_events`, written synchronously
  and chained.
- **Settings are read at the moment they are used.** The SSH destructive-command gate is
  a singleton that captured its flag at construction, so turning it on needed a restart
  that nothing on the screen would have mentioned; the pip provisioner checked its
  enable flag once at startup and then never again. Both now read through
  `AppSettingsProvider`, which is what makes the screen mean anything. Fallback order is
  stored row, then configuration, then default — configuration still wins where nothing
  is stored, so an operator's environment variable is not silently ignored.
- **Flow-weaver's four settings were not ported.** `rbac_mode` and
  `permissions_granular_gating_enabled` are rollout switches for an RBAC migration
  Nashira never had — its permissions are already enforced — and the two fuzzy-match
  thresholds belong to a workflow importer Nashira does not have. Copying the shape
  would have produced a page of dead controls, and a setting that settles nothing
  teaches people the screen is decoration. The catalog was built the other way round:
  start from values the code reads at use-time, expose only those.
- **`/admin/traces` and the health endpoints are excluded from HTTP tracing.** The live
  tail polls its own endpoint every few seconds; tracing it would fill the table with
  the act of reading it, and push the rows an operator came to find off the top faster
  the harder they looked.
- **Trace retention is 14 days**, swept by the existing retention service, which now also
  records what it deleted — in the trail it just pruned, because "the rows I wanted are
  gone" has exactly one innocent explanation.

### Fixed
- **Refusals were not being traced.** The middleware sat after `UseAuthorization`, which
  short-circuits a 401 or a 403 without reaching the rest of the pipeline — so every
  denial went unrecorded, and "why was I denied" is the question this trail gets opened
  for. It now sits between authentication and authorization: late enough to name who
  made the request, early enough to see it refused.
- **Every HTTP trace recorded a duration of zero**, because the single-shot write hard-
  coded it. The "slowest" panel could therefore never surface an HTTP request, which is
  most of what it exists to find.

## 2026-08-19 — service-level objectives

Five objectives, one computation, and a daily sweep that writes what it finds where it
survives the outage that caused it.

### Added
- **`/admin/slo` and `GET /api/admin/slo`** — run latency (p95), error rate, throughput
  and promotion latency, the four flow-weaver measures, plus one it structurally cannot:
  **contained failures**. Nashira's engine records whether a failed run rolled its
  changes back, and that is what decides whether a failure was an incident — a run that
  reversed everything it did is contained, one that stopped halfway left devices in a
  state nobody chose.
- **Editable thresholds** (`PUT`/`DELETE /api/admin/slo/targets/{key}`). Flow-weaver left
  this as an open follow-up and pointed at the deployment config; an objective nobody can
  move is an objective everybody learns to ignore. Only the number is data — what an
  objective *is*, and above all how it is measured, stays code, because adding one means
  writing the query. A row exists only where somebody moved a threshold, so its absence
  is not missing configuration.
- **A daily breach sweep** (`SloBreachWatcherService`) writing one audit row per missed
  objective per sweep, actor `slo-watcher`, filterable as `slo.breach`. Nothing emails or
  pages: an outage is exactly when a notification path is least likely to work, and the
  row is what answers "when did this start" afterwards. Written **through `AuditLogger`**,
  not inserted directly as flow-weaver does — every audit row hashes its predecessor, and
  a row that skips the logger enters the chain unlinked and makes the verifier report the
  whole trail broken from that point on.
- The screen and the sweep share **one `SloComputeService`**, so a green dashboard and a
  trail full of breach rows cannot describe the same window.

### Changed
- **Promotion latency is measured, not inferred.** Flow-weaver approximates it with the
  creation timestamp of the promoted row because it has nothing better; Nashira stamps
  `PromotedAt`, so this is the elapsed time itself. A promotion whose source has since
  been deleted is skipped rather than counted as zero, which would report instant
  promotions the moment somebody tidied up an old draft.
- **"No signal" is a fourth state, not a good one.** An objective with nothing to measure
  reports null and never counts as a breach. Zero would page somebody every night on a
  quiet install about throughput nothing was trying to meet, and green would claim the
  platform contained failures it never had.
- **The median is a real median.** On an even count it averages the middle pair; taking
  the upper of the two reports a week as typical when half the promotions took an hour.
- **The dashboard follows flow-weaver's layout**: queue, tools, runs activity beside top
  failing, then sign-ins. It had been an index of *every* destination in the product,
  which put Git and Knowledge on a page about running the platform. The tools section is
  a hand-written list of admin destinations — deliberately not derived from the nav
  registry, since deriving from it is how those ended up there.

### Fixed
- **The hidden chart table escaped its card.** `sr-only` sat on the `<table>`, where the
  positioning and clipping land on the wrapper box while the `<caption>` sits outside the
  clipped area — so "Totals per bucket" painted itself across the page. The class belongs
  on a wrapping `div`. The caption is now a prop as well: two charts on one page were
  announcing the same title, which identifies neither.

## 2026-08-19 — the admin dashboard

`/admin` was an index: a grid of links to everywhere else, and nothing about the state of
the platform. Opening the admin panel should answer "is anything wrong right now?" before
it answers "where is everything?" — flow-weaver's does; this one made you visit four
screens to find out.

### Added
- **`GET /api/admin/metrics/{runs,auth,queue,devices}`** — read-only aggregates, Admin
  only. (`devices` has no caller since the inventory panel came off the dashboard;
  `/overview` already answered that question better.) Windowed series (1–90 days,
  clamped) return **one bucket per day including the quiet ones**: backfilling gaps
  client-side means every consumer reimplements the same loop, and a chart whose x-axis
  silently skips empty days reads as continuous activity.
  The window is inclusive of today, so seven days is today and the six before it.
- **`final_states` on the run metrics**, a distinction flow-weaver has no equivalent for:
  a failed run whose changes were all reversed (`rolled_back`) is a different operational
  fact from one that left the world half-changed (`failed`). Only the second needs
  somebody, and the dashboard says which is which under the failure count.
- **The age of the oldest queued job**, not just the depth. Ten jobs queued for a second
  is a healthy queue; one queued for an hour is a worker that stopped. Depth alone cannot
  tell those apart, so past five minutes the dashboard raises it as a warning rather than
  leaving it as a number nobody knows how to read.
- **`StackedBarChart`** — bars drawn as divs rather than by pulling in a charting library
  to render thirty rectangles. It inherits the theme tokens, so light/dark needs no second
  palette, and it carries an `sr-only` table of the same figures: a chart that exists only
  as coloured rectangles is unreadable to anyone not looking at it. A day with nothing on
  it still gets a hairline, because an empty column is ambiguous between "nothing
  happened" and "no data".
- **`/admin` as a dashboard**, laid out after flow-weaver's: queue depth, the admin
  tools, run activity beside what is failing, then sign-ins — over a selectable window,
  with pausable 30-second auto-refresh. Every number links into the list it came from; a
  count of failures with no way to reach them just makes you rebuild the filter by hand.

### Fixed
- **The model had drifted from its migration snapshot**, and the backend would not
  start: the `defaultValue: 1` on `HashVersion` was written into the audit migration by
  hand without regenerating the snapshot, and the next migration stacked on top of the
  omission. `Phase11_AuditHashVersionDefault` reconciles them; it is a no-op against a
  database that already ran the audit migration.
- **A failed migration no longer pretends the database is down.** The startup retry
  caught every exception and logged "database not ready" for all of them, so a model
  mismatch — which no amount of waiting can fix — was retried for the full timeout under
  a message that sent you to inspect a database that was healthy the whole time. Only
  connection-level failures are retried now; anything else fails immediately, saying so.
- **The identity chip is a menu, not a link.** It used to go straight to `/account`,
  which made the word under your name — your role, "Admin" — read as the way into the
  admin panel. People clicked it expecting the dashboard and landed on their own
  profile. Following flow-weaver, it now opens a menu: *Admin dashboard* (admins only),
  *Account and password*, *Sign out*. The standalone sign-out icon above it is gone,
  since it would otherwise sit two clicks from its own duplicate.
- **`/admin` had no navigation entry at all.** It was reachable only by typing the URL.
  The way in is the user menu, as in flow-weaver.

### Changed
- **The link index the page used to be is gone.** It was derived from the nav registry,
  which knows every destination in the product — so a page about running the platform
  listed Git and Knowledge. The tools section that replaces it is a hand-written list of
  admin destinations; the sidebar is still the map.
- A refresh that fails once there is already data on screen **raises a toast instead of
  blanking the page**. The numbers are stale, not wrong, and the timestamp says how stale.

## 2026-08-19 — the audit module, reconciled

Two trails now, where there was one and a half. Nashira's audit log was hash-chained and
verifiable, which flow-weaver's is not — but it could not say who acted when nobody was
signed in, could not be filtered by the questions people actually ask, and had no record
of authentication at all. A failed sign-in, a lockout, a token replayed: none of it was
written anywhere that survives log rotation.

### Added
- **`actor` on every audit row, bound into the hash.** Automation has no `UserId` —
  nobody is signed in — so a scheduled run and a human change were the same
  indistinguishable `user: null` row. `AuditActor` carries an ambient identity into
  background work; the job worker binds `workflow-runner`.
- **A versioned hash chain.** Binding a new field into the canonical form would have
  invalidated every row already signed, so `HashVersion` says which form each row was
  signed with: v1 rows keep verifying under the old one, v2 covers `actor`. Re-signing
  the old rows instead would have made them verify by destroying the evidence they
  exist to provide. The migration backfills v1 explicitly — EF's default of `0` would
  have had `/verify` declare the whole trail broken the moment it ran.
- **The filters the screen is actually used with**: `entityId` (the history of one
  object), `actionPrefix`, `actor`, and `entityTypes` for a category spanning several
  at once.
- **An authentication trail** (`auth_events`, `GET /api/auth/events`): sign-ins,
  failures, logouts, lockouts, refreshes, token revocations, password changes.
  Deliberately a separate table and no hash chain — the chain serialises writes behind
  one process-wide gate, and a burst of failed sign-ins is exactly when that would turn
  the login endpoint into a queue. The attempt that *trips* a lockout gets its own row,
  because the failure counter is reset in the same breath and "when did this lock" was
  otherwise unanswerable.
- **`/admin/audit`**, laid out after flow-weaver's — tabs, category chips, an ANDed
  filter row, rows that expand in place — with the chain verifier, the hash column and
  real server-side totals on top. `/audit` redirects, query string intact.

### Changed
- **Nineteen security-sensitive controllers now write their own rows** across 44 actions, with real
  before/after snapshots, and those actions are `[SkipAudit]` so the global mutation
  filter does not double-log them. Secret material never travels: credentials go as
  `has_password` / `has_token`, secrets as provider and key, users as
  `password_changed` without the value.
- **The mutation filter records automation** instead of dropping every unauthenticated
  request, and normalises `entity_type` from the controller name — an explicit map,
  because inflecting English by rule turns "Policies" into "policie" and nobody notices
  until the filter returns nothing. It still skips a request that can be attributed to
  neither a user nor an ambient actor: such a row is the indistinguishable `user: null`
  the actor field exists to abolish.
- **A failed audit write no longer fails the request.** Callers write after committing,
  so throwing answered a mutation that had already taken effect with a 500 — the client
  retries and applies it twice. The failed row is now detached from the change tracker,
  which is the load-bearing half: left `Added` in a scoped context it would poison every
  later save in the same request, turning one transient failure into the guaranteed loss
  of every remaining row and taking unrelated writes down with it.

### Security
- **Capped what an anonymous caller can write.** The failed-sign-in path serialised the
  attempted username, which is a request-body field on an unauthenticated endpoint: one
  POST with a multi-megabyte username wrote a multi-megabyte row into a table nothing
  prunes. Clipped at the source, with a 2 KB ceiling on the whole metadata object.
- **A forged `actor` on a pre-v2 row is now detected.** v1's canonical form does not
  cover the field, so an `actor` written onto such a row afterwards was invisible to its
  hash and `/verify` would have certified it — and on rows with no user that field is
  the only statement of who acted, which makes it precisely the forgery worth making.
- **`actionPrefix` escapes LIKE wildcards.** The actions here are full of underscores;
  unescaped, `retry_install` also matched `retryXinstall` and a lone `%` matched
  everything while looking like a filter.
- The CSV export neutralises spreadsheet formula prefixes. This table holds
  attacker-influenced text — a username someone tried, a User-Agent — and a value
  starting `=` is executed on open.

## Phase 9 — an upload that fails should look like it failed

Four reports, and the first two turned out to be one bug wearing two hats. Attaching a
skill to an integration "did nothing", and the same skill showed up under Prompt Skills
instead. What actually happened: the name already belonged to a **global** skill, so the
attach was refused from the first press — and the error rendered at the top of a drawer
holding a list, an alert and a ten-row editor, while the button sits at the bottom. The
message was off-screen. Pressing Attach and being refused was pixel-for-pixel identical
to pressing Attach and nothing happening, so the natural next move was to press it
again, and only then read "already exists".

### Fixed
- **Attach feedback sits next to the button that produced it**, success and failure
  both, and is cleared when the tab changes or the form is re-submitted. Feedback that
  describes one button while sitting under another is worse than none.
- **A name collision is no longer a dead end.** A conflict with a global skill or spec
  now comes back with a code (`skill_name_global` / `spec_api_global`) and the drawer
  offers to take it over. Deliberately labelled *Replace it and attach* rather than
  *attach the existing one*, because that is what it does — the stored content is
  replaced with what is in the editor, and an admin who has not read the original
  should know that before clicking. A name owned by **another** integration is never
  offered: taking it would strip operations that integration is calling today.
- **Attaching a spec now really does make it inherit the integration.** Setting
  `integration_id` was not enough — `RestOperationExecutor` prefers a spec's own
  `base_url` and honours its `auth_type` whenever it is not `none`, so an adopted
  self-contained spec kept calling its old host with its own token while the
  integration screen listed those operations as its own. The attach path clears
  `base_url` / `auth_type` / `auth_config` outright.
- **A spec that describes no operations is refused.** Parsing does not throw on the
  wrong file — plain prose is valid YAML — and zero operations was only a warning, so
  a README uploaded by mistake saved cleanly and left the action catalogue untouched:
  the exact failure the strict parse was meant to prevent. Found by a test written to
  assert the opposite.
- **`list_integrations` stopped being confidently wrong about absence.** The `name`
  filter was exact and case-sensitive, so asking for `netbox` missed an integration
  called *NetBox* — and the agent told the user NetBox was not configured while it sat
  there configured and health-checked. Matching is now name/slug/type, case-insensitive,
  substring. A filtered query that finds nothing additionally returns how many
  integrations exist and names them, because an empty filtered result is not evidence.
- **Integration-scoped skills and specs no longer appear in the global catalogues.**
  `/admin/skills` and `/admin/specs` request `globalOnly` unless a `?integration=`
  filter is present; the scoped rows are reached from the integration that owns them.
  Both pages now follow a row that changes scope, in either direction, instead of
  leaving the user on a list it just vanished from.

### Changed
- **`/admin/permissions` redesigned.** Search across every group at once (keys
  included, so `integration:netbox` pasted from an audit event just works), a
  Restricted-only filter that turns the page into an audit of what is in force, one
  two-state control per row instead of four checkboxes with three of them disabled, an
  explicit **denied** badge for a restriction that grants nothing, unsaved-change
  tracking with a sticky save bar, and a confirmation before a user switch throws
  pending edits away.
- `Tabs` takes an optional `onchange`, for callers that have work to do on the switch
  itself rather than on the value afterwards.

## Phase 9 — approving a package now installs it

`allowed_python_modules` had carried `source`, `pip_spec`, `status`, `installed_version`
and `error` since the Phase 9 migration, and nothing ever wrote to them. The API accepted
a module name and nothing else, so a `pip` row could not be created at all — and if one
had been, no code installed it. An admin allowing `netmiko` got a row that said the
module was permitted, an interpreter that had never heard of it, and an ImportError
pointing at the snippet instead of at the missing install.

### Added
- **`PythonPackageProvisionerHostedService`** — claims pending pip rows with one
  conditional UPDATE (so replicas never install the same package twice), runs
  `pip3 install --target <PackagesDir>/site --upgrade`, then **verifies the module
  actually imports** before marking it ready. That second step is the one that catches
  the common mistake: `pip install pyyaml` succeeds and `import pyyaml` does not, and
  without the check the row would go green and fail later in somebody's workflow run.
  Wheels-only by default (`Python:PipOnlyBinary`), so no sdist packaging code runs at
  approval time. A claim left behind by a process that died is reclaimed after 10
  minutes; shutdown mid-install leaves the row `installing` rather than recording a
  failure that did not happen.
- **`source` / `pip_spec` on POST, `status` / `installed_version` / `error` on the read**,
  plus `POST /api/python-modules/{id}/retry` to re-queue a failed install. Creating and
  deleting a module row is now audited, with `pip_spec` recorded verbatim — the exact
  version pin is the artefact an incident review needs.
- **`/admin/python-modules`** gained a source selector, a pip-requirement field, a status
  column with the installed version, a retry action, and a poll that runs only while
  something is installing.
- **`GET /api/integrations/{id}/bundle`**, **`POST /api/integrations/{id}/specs`** and
  **`POST /api/integrations/{id}/skills`**: an integration's specs and prompt skills,
  managed from the integration itself. Both rows always carried an `integration_id` and
  both controllers accepted it — but only as a field on a form somewhere else, so giving
  NetBox a spec meant three screens and a sync step that was easy to skip. Attaching a
  spec here upserts it, links it and **re-materialises the action catalogue in the same
  call**, and the response reports how many actions moved.
- **`POST /api/integrations/bundle`** — the integration and everything that belongs to
  it in one transaction, including the actions its specs produce. An integration on its
  own is a base URL: it does nothing until a spec gives it operations, so creating them
  in three calls meant any failure left something half-wired. Every staged item is
  validated before any of them is written, and two items sharing a spec `api` or skill
  `name` are refused rather than silently collapsed into one.
- `/admin/integrations` gained a skills-and-specs panel per row: what is attached, and
  file-upload or paste to attach more. **The New-integration modal is now tabbed** —
  Integration / API specs / Skills — staging files in the browser and submitting them as
  one bundle. Editing keeps using the panel instead, where an upload lands immediately
  rather than waiting for a Save.

### Changed
- **Only `ready` modules are offered to a snippet.** A pip package still installing, or
  one whose install failed, is on the allowlist and not on disk. The refusal now
  distinguishes the three cases — not approved, approved and installing, approved and
  failed — because only one of them is the snippet author's problem.
- `PythonSnippetHandler` passes the packages directory to the sandbox, and only when
  something is actually installed there, so a deployment that never approved a pip
  package runs the byte-identical harness it did before.
- Deploy: a `backend_pyenv` volume, `Python__PackagesDir`, and `/app/pyenv/site`
  pre-created in the image owned by `app` — Docker seeds a named volume from the mount
  point, so a directory created at runtime would have given a root-owned volume the
  non-root process cannot write to. Provisioning is off in Development, where the
  container default path resolves somewhere nobody expects.
- The `na_snippets` and `na_integrations` specs and the `snippets` / `integrations`
  skills describe all of the above, so the agent stops treating "on the allowlist" and
  "importable" as the same state.

## Phase 9 — a skill and a spec per part of the platform

The agent shipped with two built-in skills: `base.md` and `nashira.md`, one overview of
the whole product. Everything else it knew about workflows, snippets, MCP, git or
governance it inferred from tool descriptions at call time — which is how a promotion
gate gets explained wrongly, or `list_apis` gets treated as proof a system is not
configured. The API catalogue had the same shape of gap: seven specs against a backend
with roughly twice that many functional areas, so whole subsystems were unreachable by
`discover_operations` and invisible in Admin → Specs: integrations, MCP, snippets,
triggers, policies, users and permissions had no document at all.

### Added
- **Eleven built-in skills**, one per functional area, alongside `base.md` and
  `nashira.md`: `workflows`, `snippets`, `runs`, `inventory`, `integrations`, `mcp`,
  `git`, `knowledge`, `governance`, `exports`, `troubleshooting`. Each is 3–5 KB and
  carries the things a tool description cannot: the promotion gate's four refusal codes
  and what each one means, the idempotency ceiling a `non_reversible` handler imposes on
  its author, why `rolled_back` and `failed` are not synonyms, the SSH read/mutation/
  destructive classification and its unknown-command default, and the fact that a
  permission row is a restriction rather than a grant. They appear in Admin → Skills as
  editable built-ins, ahead of any tenant-authored row.
- **Six built-in API specs**, taking the catalogue from 7 to 13 documents and 67 to 156
  operations: `na_integrations` (integrations + the action catalogue),
  `na_mcp` (servers, tools, sync, call), `na_snippets` (snippets, vendor commands, the
  Python import allowlist), `na_triggers` (cron/webhook triggers + acceptance tests),
  `na_notifications` (SMTP channels, chat webhooks, reports) and `na_governance` (users,
  profiles, permissions, policies, credential writes, the secret store).
- The policy rule shape is now **described in the spec** rather than only in the
  evaluator: `deny` clauses AND while values within a clause OR, an absent clause places
  no restriction, a present clause against an empty context does not match, and a `gate`
  with no `require` list can never be satisfied.

### Changed
- Both the `governance` skill and `na_mcp` now state the **per-call** autonomy
  refinement rather than the per-tool tier alone: `execute_operation` runs autonomously
  on `GET`/`HEAD`, and `mcp_call` only when the tool declares `readOnlyHint` *and* the
  server has `trust_tool_hints` set. Documenting the blanket tier would have taught the
  agent to ask for approval it does not need, and to trust a hint it should not.

## Phase 9 — an integration form you can edit

Editing an integration meant re-entering what was already configured, because the API
returned almost none of it. The auth was write-only *in full* — including the parts that
are not secret — so the NetBox "Token" prefix had to be remembered and retyped, and the
static headers were a JSON textarea that opened empty however many headers were stored.

### Added
- **`auth_shape`** on the integration response: method, scheme prefix, header name,
  username, token URL, client id, scope. The non-secret half of `auth_config`. Tokens,
  passwords and client secrets still never leave, and any value carrying a
  `${secret:...}` reference is dropped rather than echoed — the reference names the
  secret store's layout, which is exactly what should not be handed out.
- **`headers`** on the response, so the form can show what is configured. They are
  declared non-secret by the field's own contract: `Authorization` is refused there
  because it belongs to `auth_config`.

### Changed
- **Static headers are edited as rows**, not as a JSON object (`KeyValueRows`). Adding
  one header should not require typing braces, and a stray comma should not be the
  difference between a saved integration and a 400.
- The edit form **loads the stored shape and headers**, so an edit about the description
  no longer silently resets the prefix that made NetBox work.
- The form now sends `headers` as a string rather than null. Null means "leave the stored
  value alone" — harmless while the form could not read them back, and a trap the moment
  it can: deleting every row would have saved nothing.

## Phase 9 — confirmations that mean something

Every `execute_operation` and every `mcp_call` was confirmed, and consent lasted exactly
one request. Asking NetBox how many devices it had cost the same approval as deleting
one, and a conversation spent reading anything at all became a wall of dialogs — which
teaches people to approve without reading, the precise opposite of what a confirmation
gate is for. The friction was not just annoying; it was corrosive to the control itself.

### Changed
- **Reads are no longer confirmed.** The gate is now evaluated per *call*, not per tool:
  `execute_operation` on a `GET`/`HEAD` operation runs autonomously. The method comes
  from an OpenAPI document an administrator uploaded to this installation, so it is
  evidence rather than a claim.
- **Approval lasts for the conversation.** `AIConversation.ApprovedToolsJson` remembers
  what the user confirmed, so the same tool is not re-asked on every message. A new chat
  still starts from nothing — the scope of a permission stays something a user can hold
  in their head.
- **`elevated_confirm` is never waived.** Deleting a user, merging a PR, promoting a
  workflow: no argument inspection turns one of those into a read.

### Added
- **MCP `readOnlyHint` is captured** from `tools/list` and stored per tool, shown in the
  admin UI. It only skips a confirmation when an admin has set **`trust_tool_hints`** on
  that server — off by default. The MCP specification says plainly that a client must not
  make tool-use decisions on annotations from a server it does not trust, and the server
  is the party that gains from calling a destructive tool harmless. Trusting one is a
  statement only an administrator can make, so it is a switch with a warning next to it,
  not a default.

## Phase 9 — permissions that exist: enforced, and built from what is registered

### Fixed — per-user permissions were stored and never read
- **`UserToolPermission` rows are now enforced** on every agent tool call
  (`ToolResourceGuard`). They were written by `/api/permissions` and by the agent's own
  `set_user_permissions`, and then read by **nothing**: an administrator could revoke a
  user's access to NetBox, see it saved, and watch the agent keep calling NetBox on their
  behalf. Role was the only real gate, and `PermissionClassifier.IsAllowed` took an
  `ICurrentUser` it never looked at.
- A row is a **restriction**, not a grant — default-allow, opt-in-deny, the same posture
  as the Policy engine. A target nobody has written a row for falls back to the user's
  role, so an installation that has never opened the permissions screen behaves exactly
  as it does today. That is what makes this safe to deploy.
- Grants are checked against the **arguments**, not just the tool name: `mcp_call` on the
  Splunk server, `discover_operations` on the NetBox API, `execute_operation` on the
  integration a spec is bound to. Both the capability domain and the specific system are
  evaluated, so "no MCP at all" and "no Splunk" are both expressible and the narrow
  restriction cannot be lifted by the broad grant.

### Changed — the catalogue is derived, not declared
- **`GET /api/permissions/domains` lists what is actually registered**: the capability
  domains the tool matrix carries, plus every integration, MCP server and API spec, keyed
  `integration:<slug>`, `mcp:<server>`, `api:<api>`. It used to be a fixed
  `["awx", "netbox", "infoblox", "servicenow", "device", "common", "dynamic"]` — four
  vendor names this installation may never have heard of, none of the domains the tools
  use, and no way to name the NetBox registered yesterday.
- **`inherit`** on a permission item removes the row. Absence, all-false and inherit were
  previously one state; they are three answers — "no opinion", "deny everything here" and
  "stop having an opinion" — and the screen could not say the first without saying the
  second.
- **Admin → Permissions** is grouped by kind with an explicit Restrict toggle per target,
  because a grid of checkboxes cannot distinguish "follows their role" from "denied".

## Phase 9 — operator feedback: session forensics, self-knowledge, bulk skills

A second round from the same lab work, this time about using the product rather than
connecting it. The theme: the platform knew things it never told anyone — what the agent
was about to do, what it did afterwards, and how the application it is operating actually
fits together.

### Added — Sessions and turn telemetry
- **`agent_turns`**: one append-only row per agent turn, recorded as it happens — the
  prompt, the model, every tool call with its arguments and its result, tokens,
  iterations, and how the turn ended. `AuditEvent` remains what it was (mutations only,
  hash-chained); a turn that read twenty things and changed nothing left no trace
  anywhere, and tool *results* were never recorded at all. That is the gap this fills.
- **`GET /api/sessions`** (Admin): every conversation in the installation with its user,
  turn and tool-call counts, failures and token spend — the oversight view.
  `/api/ai/conversations` stays scoped to `UserId == me` and always will.
  `GET /api/sessions/{id}` returns the transcript plus the recorded turns;
  `GET /api/sessions/turns` is the flat view, and deliberately does not join
  conversations — turns whose conversation was deleted are exactly the trail someone
  would want gone.
- **Admin → Sessions** page: sessions list, transcript, and per-turn detail with each
  tool call's payloads expandable.
- **Secret-bearing tool arguments are redacted before they are stored** (`ToolTelemetry`).
  This also fixes an existing leak: `agent.tool` audit rows recorded `set_secret`'s value
  and `create_credential`'s password verbatim, in the one table built to be handed to an
  auditor. Matching is on whole argument names, so `api_key_header` — which names a
  header, not a key — still comes through.
- Audit events carry the **username**, and the list filters by **user and date range**. A
  trail whose actor column is a GUID is technically complete and gets read once.

### Added — bulk skill import
- **`POST /api/ai/skills/import`**: many markdown files in one call, with a per-file
  verdict (created / updated / skipped / failed). Skills are written as a directory of
  `.md` and shared that way; importing them one POST at a time means the first security
  rejection strands the rest half-loaded. Existing names are **skipped, not overwritten**,
  unless the caller asks — with the UI offering "replace N existing" once the result is
  visible. Each file is validated exactly as a single upload is, and saved on its own so
  one refusal cannot roll back the forty that were fine.

### Added — the agent knows what it is operating
- **`Skills/nashira.md`**: the platform's own model, in the system prompt. What a
  Credential, a Secret and an `auth_config` each are and why storing a token in Secrets
  does not authenticate an integration; which `auth_config.method` emits which header;
  end-to-end procedures for connecting an HTTP system, an MCP server, adding a device and
  importing inventory; and how to read the failures that actually happen (403 with no
  header vs. a rejected token, `degraded` vs. `unreachable`, an expired MCP session).
  Users ask the agent how to do things in the application it lives in, and it had no
  more idea than a stranger.
- **base.md now requires the agent to say what it is about to do** before the first tool
  call, and to name what will change before asking for confirmation. The user watched
  tool names go past with no idea what was being attempted.

### Fixed
- **The chat composer keeps the caret.** It never called `focus()`: clicking Send moved
  focus to the button, which was then replaced by the Stop button and destroyed, leaving
  focus on `<body>` — so every message began by clicking back into the box. It now takes
  focus on mount, after sending, when a turn ends and after attaching a file, and only
  ever takes it from `body` or from a button.
- **Validation errors say which field.** ASP.NET's model-binding 400 carries no `detail`:
  the useful part is the per-field `errors` map under a title that only says "One or more
  validation errors occurred". The client read the title and showed "The submitted data is
  invalid", which reads as a bug in the app rather than a rejected field.

## Phase 9 — field-report fixes: integration auth, capability discovery, honest health

Thirteen issues logged while wiring NetBox and Splunk into a live lab through the chat agent. The
common thread is not a crash anywhere: every one of them reported success — a green health badge, a
`200`, a `has_credentials: true`, an agent that confidently answered "not registered" — while the
thing underneath did not work. Each fix below is about making the system say what is actually true.

### Fixed — integrations
- **An absolute `health_check_path` is used as-is** (`IntegrationHealthChecker.BuildProbeUrl`).
  Pasting the full probe URL into that field is what everyone does; concatenating it onto `base_url`
  produced `…/api/http://…/api/status/`, whose 404 was reported as **`unreachable`** — so the operator
  went looking at the network for a system that was up and answering.
- **A 404 from the probe is `degraded`, not `unreachable`**, and names the field to check. 401/403
  already were. "Unreachable" now means the host did not answer, which is the only reading that sends
  someone to the right place.
- **`has_credentials` means "this integration will send something"**: a linked credential, or an
  `auth_config` that resolves to a method other than `none`. It used to be true for any non-empty
  `auth_config` — including `{"method":"none"}` — which masked the missing token behind a flag that
  said the credentials were there, and made the resulting 401 unexplainable. The agent's
  `list_integrations` used a *third*, laxer definition; both now share one.
- **`has_inline_credentials`** is reported alongside it. Secret material inside `auth_config` takes
  precedence over the linked credential (deliberately — the config holds the shape, the credential the
  material), but nothing said so: an integration created with an inlined token ignored every credential
  it was later pointed at, survived two restarts, and looked unrepairable. The API now reports the
  condition, the admin list shows *"inline secret overrides <credential>"*, and the applier logs
  `integration.auth.inline_overrides_credential` on each request that takes that path.
- **`auth_config` accepts the object form** (`{"auth_config": {"method":"token"}}`) as well as the
  JSON string. The field was writable all along; sending it the way anyone would write it produced a
  bare 400 from the model binder, which is what made the stale-token state look permanent.
- **`auth_method` in a create/update body is refused with a message naming the right knob.** It is
  derived from `auth_config` (or from the linked credential), so it was silently dropped and then
  contradicted by the response — a `PUT` with `"auth_method":"token"` answered `"api_key"`.
- **Filtering by `type` also matches the slug** (`IntegrationQuery.FilterByType`), for both the HTTP
  endpoint and the agent tool. `type` is free text an admin may leave empty, so `type=netbox` returned
  `count: 0` for an integration named NetBox — and the agent, having asked the obvious question and
  been told nothing was there, went looking elsewhere.

### Fixed — the agent
- **A tool that needs confirmation no longer produces an empty assistant turn.** The model rarely
  streams prose alongside a tool call, so the turn ended on `confirmation_required` + `done` with no
  text: over the API it read as the agent going silent, and an empty message is what got persisted
  into the conversation. It now says which tool it is waiting on and how to approve it.
- **Capability discovery covers all three registries.** Asked about NetBox or Splunk, the agent called
  `list_apis`, found only the built-in specs and answered *"I don't see a NetBox API registered for
  this tenant"* — while both were registered and healthy **MCP servers**. `list_apis` never sees those.
  `Skills/base.md` gains a "Rule 0" naming API specs, MCP servers and integrations as three
  independent places a system can live, and forbidding "not registered" without checking all three;
  `list_apis`'s own description now says what it does not cover.

### Fixed — API specs and secrets
- **A spec that declares an auth type but carries no material falls back to its linked integration**
  instead of going out anonymous. `auth_type: token` with an empty `auth_config` won the precedence
  contest and then sent no header at all, so NetBox answered *"Authentication credentials were not
  provided"* while the linked integration sat there holding a working token. Both the fallback and the
  anonymous case are logged (`rest.executor.spec_auth_empty`).
- **A spec's `integration_id` is validated on create and update.** A link pointing at nothing produced
  exactly the same silent anonymity.
- **List endpoints materialize their projections before returning them** (`SecretsController`,
  `PermissionsController`, `AiPromptSkillController`). A deferred `Select` inside an `ObjectResult` is
  evaluated *during serialization* — after the 200 and the headers are on the wire — so anything that
  throws there truncates the body with no status to carry the error: the client hangs, the log records
  a success, and the admin page never loads.

### Fixed — MCP
- **A tool call that fails to authenticate upstream marks the server `degraded`.** `tools/list` is the
  health probe, and a server that logs into its own upstream once at startup keeps answering it long
  after every real call has begun failing — Nashira showed *healthy, 53 tools* while every call
  returned `Session is not logged in.` The match is deliberately narrow (session/auth wording only):
  a tool rejecting bad arguments is not a sick server.

### Added
- **`GET /api/ai/models`** — the models a chat request may name, flattened across enabled providers
  (each provider's `default_model` plus anything under `config.models`). The frontend was already
  requesting this route on load and getting a 404. Operator rather than Admin: choosing a model is not
  a configuration change, and the response carries no secrets.

## Phase 9 — flow-weaver convergence: integrations, MCP, the snippet engine, governance

### Added — integrations and MCP
- `Integration` + `IntegrationAction`: the base URL, credentials and operation catalogue of an external
  system. The catalogue is **projected from the linked OpenAPI specs** (`AiApiSpec.IntegrationId`), so
  one upload feeds both the agent's catalogue and the workflow builder's instead of two lists that
  drift apart. `RestOperationExecutor` resolves the URL and credentials through the integration when
  the spec does not carry its own.
- `McpServer` + `McpTool`: Nashira as an MCP **client** over Streamable HTTP (official
  `ModelContextProtocol.Core` SDK), with catalogue sync, a health probe and invocation. No MCP server
  is exposed — that stays out of scope (§6.4).
- Agent tools: `list_integrations`, `list_mcp_tools`, `mcp_call`.

### Added — the workflow engine (`snippet_id` stops pointing at nothing)
- `Snippet` + `ISnippetHandler` + `SnippetNodeExecutor`, **replacing `ToolNodeExecutor`**. Until now
  `workflow.v1` defined `snippet_id` as a UUID and no `Snippet` table existed: a conforming workflow
  could be stored and validated but **not executed**, because the executor read a
  `config_overrides.tool` binding that is not in the schema.
- Seven handlers: `ping`, `transform`, `rest_call`, `mcp_call`, `ssh`, `integration_action`,
  `python_snippet`.
- `VariableResolver` (`{{ steps.x.output.path }}`, `{{ device.x }}`, `{{ input.x }}`) and
  `ConditionEvaluator`. `conditional` edges were previously treated as `success`, so **a workflow with
  one branch executed both**.
- `per_device` fan-out, a retry policy (two gates: the failure must be retryable **and** the step
  idempotent), and `POST /workflows/{id}/run` now accepts `{ input, target_devices }`.
- Import/export: `POST /workflows/import` and `GET /{id}/yaml?download=true`. Import ignores the `id`
  and `environment` in the file — it always creates a new draft, so an imported file can neither
  overwrite a local workflow nor arrive already marked production and skip the promotion gate.

### Added — governance and platform surface
- `Policy`: default-allow / opt-in-deny guardrails in two shapes. `deny` is evaluated before a run
  (`WorkflowRunService`); `gate` is evaluated before a promotion (`PromotionService`) against the run
  history. Both evaluators **fail closed**: a rule that cannot be evaluated blocks and names itself.
- `WorkflowVersion` (an immutable snapshot written in the same transaction as the promotion, with
  `/restore` producing a new draft), `WorkflowAcceptanceTest` (whose verdict is invalidated when the
  workflow changes), `WorkflowTrigger` (cron with real timezone handling + webhook with HMAC-SHA256
  and constant-time comparison), `DevicePool`, `VendorCommand`.
- Convergence fields on existing entities: `Device` (`SourceId`, `ExternalId`, `LastSyncAt`,
  `Properties`, the `AllowDraft`/`AllowQa`/`AllowProduction` trio), `Workflow.ConversationId`,
  `AiPromptSkill`/`AiApiSpec` (`IntegrationId`, `CreatedBy`).
- Seven built-in OpenAPI specs describing Nashira's own API, plus `BuiltinSpecSeeder`.

### Security
- The device `Allow*` trio is actually enforced: `WorkflowRunService` resolves targets once and
  **refuses by naming the device** instead of filtering silently. A run that executed on 3 of 5 devices
  would report success having done 60% of the work.
- The `python_snippet` sandbox **scrubs the child process environment** (allowlist, not denylist), so
  the connection string and the JWT key are not there to be read. It is the one control that survives
  an interpreter escape. See `PythonSandbox` for what it does **not** protect.
- Webhooks: an unknown route and a bad signature return the same 401, so routes cannot be enumerated.
  `allow_target_override` is `false` by default — the caller authenticates with a shared secret and
  does not pass the RBAC a manual run does, so the body may only **narrow** the target list.
- MCP credentials are encrypted with `ISecretProtector`; `Integration` credentials are
  `${secret:provider:key}` references. The asymmetry is deliberate: an OAuth token is obtained at
  runtime and has nothing to point at.

### Fixed
- Three EF migrations scaffolded `defaultValue: ""` on NOT NULL JSON columns and `false` on booleans
  whose C# initializer is `true`. Left as generated, the `Allow*` trio would have made **every**
  existing device unreachable from any environment, and the JSON columns would have thrown on reading
  any pre-existing row.
- NetBox sync matched by name, so a rename upstream duplicated the device. It now matches on
  `(SourceId, ExternalId)`.
- `VariableResolver` returned the **whole output object** when a path was malformed (an unclosed
  `list[0`), because "empty list" meant both "no path" and "broken path".
- The skills form said *"Higher is included first"*; `SkillPromptLoader` sorts ascending.

### Added — messaging, email, reports and themes
- `MessagingChannel` + `MessagingDelivery`: **outbound** notification channels (Slack, Teams, generic
  webhook) with retries, and every attempt persisted — "did the on-call channel actually get the
  alert?" is a question asked afterwards, when the log has already rotated.
- `EmailChannel`: named SMTP relays, so different workflows can send through different relays without
  touching `appsettings`. The configured `Smtp:*` remains the deployment default.
- `ReportArtifact`: generated reports with optional retention. Separate from `ExportArtifact` on
  purpose — an export is a throwaway CSV, a report is evidence someone may want to re-read.
- `Theme`: per-user UI themes; sharing one with everybody is an admin act in both directions.
- Frontend `/admin/messaging`, with manual sends, a probe and delivery history.

### Added — governance frontend
- `/admin/policies`: CRUD with `deny` and `gate` templates, plus a **dry run** answering "would this
  rule block that run?" before arming it. A policy is born **disarmed** on purpose — a guardrail first
  used in production is one nobody read carefully.
- Navigation: `Policies` under a new "Governance" group in the admin index.

### Fixed — internal code review (6 findings)
1. **`EmailChannel` was a table with no API.** Added `EmailChannelController` (`/api/email/channels`,
   Admin, with `POST /{id}/test` that sends a real email) and `EmailChannelSender` — kept separate from
   `EmailService` so the `appsettings` path remains the deployment default.
2. **`python_snippet` was unusable:** an allowlist with no CRUD and no seed, and `network_enabled` not
   exposed. Added `AllowedPythonModuleController` (`/api/python-modules`, Admin), `PythonModuleSeeder`
   (18 purely computational stdlib modules; no network-capable entries on purpose — opting in should
   mean an admin added a network module knowingly), and `network_enabled` in the snippets API and
   frontend. **Turning it on is an Admin act; turning it off is any Operator's** — asymmetric on
   purpose.
3. **`ConditionEvaluator`: parenthesised groups could evaluate TRUE on garbage.** The `||`/`&&` split
   is blind to parentheses, so `(a || b) && c` produced fragments like `(a` that fell into non-empty
   string truthiness. An expression with unbalanced parentheses now fails closed **as a whole** (the
   per-fragment check was not enough: in `(a || b` the fragment `b` was valid on its own and decided
   the OR — the regression test itself exposed that).
4. **`per_device` fan-out ignored an explicit `Changed=false` from every device**, and the tier
   overwrote it to Changed, polluting the audit trail and the rollback plan. Extracted
   `AggregateChanged` with its truth table in tests: the tier fallback applies only to handlers that
   said nothing either way.
5. **`MessagingDispatcher` recorded `attempts=3` even when it broke on the first 400**, and discarded
   the Slack/Teams error body. It now records the real attempt count and attaches the truncated body.
6. **`device_pool` policies did not fire on rule-based pools** — the evaluator looked only at
   `StaticMembersJson`. Membership now resolves through `IDevicePoolResolver`, rules included.

### Added — themes frontend
- `/themes` (sidebar + palette): create, edit, apply, share (admin) and delete themes, with a live
  preview. **The theme engine does not replace colours: it replaces hues.** A theme picks a base colour
  per family; the engine keeps the original lightness of every stop in `app.css`'s OKLCH ramp and swaps
  only the hue (with chroma clamped). Because contrast is dominated by lightness, the design system's
  verified AA pairs survive recolouring — a user can make Nashira purple, but not illegible.
- The theme is applied as inline custom properties on `<html>` (beating `[data-theme]` without touching
  the stylesheet), persists per browser and coexists with light/dark mode, which is orthogonal
  (`data-mode`).
- The editor preview uses the SAME ramp function as apply, so what you see is by construction what will
  be applied. A theme is a diff: families left unticked fall back to the stock ramp.

### Added — job queue and final hardening
Closes the three design decisions the code review left open — all three had the same answer: the job
queue that had been deferred by choice at the start of block B.
- **Job queue** (`Job` + `JobQueue` + `JobWorkerHostedService`): atomic claim via `UPDATE … RETURNING`
  with `FOR UPDATE SKIP LOCKED` (safe with N replicas), and a 30-minute lease whose expiry marks the
  job **FAILED** rather than re-queueing it — a workflow run is not idempotent, and blindly repeating
  one is worse than asking a human to look. Concurrency via `Jobs:MaxConcurrent` (default 3).
- **Asynchronous webhooks**: `POST /api/hooks/{route}` now enqueues and answers **202 immediately**
  with a `job_id`. Deduplication by the `X-Nashira-Delivery` header (unique filtered index per
  trigger): a sender retrying after a timeout no longer duplicates the run.
- **Scheduler rewritten**: re-basing `NextRunAt` is an `ExecuteUpdateAsync` conditional on the value
  that was read — with two replicas only one wins the tick and enqueues; and because it enqueues
  instead of executing, one long run no longer delays every other cron. Catch-up window for missed
  ticks.
- **Retention sweeper** (`RetentionHostedService`): hourly deletion of expired `ReportArtifact` rows
  and finished jobs older than 30 days.
- **`EmailChannelSender` rewritten on MailKit**: the `ssl` option (implicit TLS, 465) genuinely exists;
  `starttls` is strict (it fails if the relay cannot offer TLS rather than downgrading to plaintext);
  TLS/auth/SMTP errors are mapped to actionable messages. The `appsettings` path (`EmailService`,
  `System.Net.Mail`) stays the deployment default and keeps not offering "ssl" — its `EnableSsl` means
  STARTTLS and would be lying.
- **`AiApiSpec.AllowPrivateNetwork`**: the SSRF guard in `RestOperationExecutor` is no longer an
  unconditional `allowPrivate: true` — the spec's flag governs when the spec carries its own base URL,
  and the linked integration's flag governs when it does not. Default `true` (on-prem network gear is
  the normal case), with the migration default hand-corrected once more.
- **Frontend**: `/admin/email` (SMTP channel CRUD with a write-only password + `clear_password`, a
  plaintext warning, a port suggestion when the security mode changes, and a real test send),
  `/reports` (list with "Show expired", create with retention, delete, authenticated blob download), an
  "Allow private/internal addresses" checkbox in `/admin/specs`, and navigation (sidebar `Reports`,
  palette `Email Channels`/`Reports`, a card in the admin index).

### Added — in-product documentation (`/docs`)
- **38 sections in English** covering the whole surface of Nashira, each with the same structure: *what
  it is for* → *how it works* → *parameters* (table with type, required and default) → *endpoints*
  (verb, path and **minimum role**) → notes on the decisions that surprise people. Grouped in reading
  order: Getting started (architecture, roles, environments, audit, job queue), Operations (chat,
  devices, pools, inventory, knowledge, git, reports, exports, themes), Workflow platform (workflows,
  runs, snippets, triggers, tests, versions, vendor commands), Governance (policies), Integrations & AI
  (integrations, MCP, specs, skills, providers, messaging, email, Python modules) and Administration
  (users, permissions, profiles, credentials, secrets, learnings, loader).
- The content is **typed data** (`lib/docs/`), not loose markdown: a parameter table cannot silently
  lose its "required" column nor an endpoint its role, and the whole corpus is searchable without
  parsing prose. The inline grammar is deliberately tiny (`code` and **bold**) and is applied **after**
  HTML escaping, which is what makes the renderer's `{@html}` safe.
- `/docs` (index with cards per group) and `/docs/[slug]` (a section with previous/next and a link to
  the real screen when its path carries no placeholders); a sidebar search that indexes the full text —
  searching "HMAC" lands on Triggers even though the word only appears in one note.
- Navigation: `Docs` in the sidebar and `Documentation` in the command palette.

### Fixed — theme engine: real scope, and a preview that previews
- **The baseline ramp is no longer read from the DOM.** The engine captured the stops with
  `getComputedStyle` when the module booted; if the stylesheet had not been applied at that instant the
  capture came back empty, **every** ramp returned `null`, and `apply()` ended up *removing* the
  variables instead of setting them. Visible consequences: the stored theme was dropped on every reload
  (while its card still read "active") and previews rendered blank. The baseline is now a constant
  table mirroring `app.css`, so `ramp()` is **pure**: it works during SSR, before first paint, and
  inside a scoped preview.
  - Drift guard: `npm run check:theme` compares all 77 stops (7 families × 11) and the 3 shell tokens
    against `app.css` and fails naming the one that moved. No new dependencies.
- **Two families were missing.** `secondary` and `tertiary` exist in `app.css` and the engine ignored
  them; there are now 7 themable families instead of 5.
- **The shell did not follow the theme.** `--app-bg` and the dark-mode control fill were fixed OKLCH
  literals — deliberately off the ramp, so a panel does not merge with its background — so the two
  largest areas on screen stayed stock and a surface theme looked like it had done nothing. They are
  now declared as `var(--app-bg-light, <the value it always had>)`, and a theme that tints `surface`
  moves them by the same rule as everything else: keep the lightness, swap the hue. The default is
  byte-identical to before.
- **A real preview.** `ThemePreview` paints an actual slice of the interface — primary / secondary /
  ghost / destructive buttons, status badges, a control, table rows, a link — with the theme's tokens
  scoped to a container, so they are the same components the app uses rather than a drawing of them. It
  is used in the editor (with a **Light / Dark / yours** switch, so a theme that falls apart in the
  other mode is discovered right there) and on every card in the list, which also states which families
  it overrides.

### Changed — navigation and information architecture
The console was organised by entity; it is now organised by what you are trying to do. The change that
prompted it: prompt skills and API specs are two halves of one thing — a spec says what an integration
can be asked to do, a skill says how to operate it — and reaching one from the other cost a trip
through a hub of twenty cards.
- **One navigation registry** (`lib/nav/registry.ts`). The sidebar, the command palette and the
  `/admin` index all derive from it. They were three hand-maintained lists that had already diverged:
  `/api/python-modules` shipped with a full CRUD API and no screen at all, while the docs advertised a
  `uiPath` that never existed. Declaring a destination once removes the class of bug.
- **Seven groups instead of one drawer**: Operate (day to day) · Build (the material workflows are made
  of) · Agent (what the agent knows and may do) · Connect (external systems and their keys) · Govern ·
  Resources. Collapsible, with the group holding the current page always open — navigation must never
  hide where you are. Themes and Account move to the footer: personalisation is not work.
- **Build leaves Admin.** Snippets, device pools and vendor commands are Operator-owned by the API, so
  hiding them behind an admin hub was a fiction the permissions never agreed with.
- **Four tabbed surfaces** (`SectionNav`) for pages edited together: **AI Studio** (skills · API specs ·
  providers · learnings · profiles · validation), **Connections** (integrations · MCP · messaging ·
  email), **Build library** (snippets · pools · vendor commands · Python modules) and **Artifacts**
  (reports · exports). These are plain links — **no route moved**, so no bookmark broke to gain the
  grouping.
- **The hub stays as a map, not a toll gate**: `/admin` now shows the same groups as the sidebar, and
  everything on it also has a direct sidebar entry.

### Added — the screen that was missing, and the flow between screens
- **`/admin/python-modules`**: the `python_snippet` import allowlist had a complete admin API, a seeder
  and no UI whatsoever — the list could only be changed by calling the API by hand. Full CRUD, with the
  network-capable flag explained where it is set.
- **Cross-links across the integration triangle.** Integrations, specs and skills were related in the
  data by `integration_id` and in no screen at all. An integration's catalogue column now links to
  exactly its specs and its skills (`?integration=<id>`), both listings filter on it with a visible,
  removable chip and a row count, and each spec/skill names its integration as a link. Empty states in
  the filtered views explain the relationship rather than reporting zero.
- **Snippet authoring points at what it talks to**: a `python_snippet` links to the module allowlist, an
  `integration_action` to integrations, an `mcp_call` to MCP servers, and an `ssh` step to vendor
  commands.
- **Fourteen dead-end empty states rewritten** to say what the thing is for and what to do next.
- **Command palette does things, not just navigation**: create actions sort first and open the right
  form directly (18 listings now honour `?new=1`). A `g` + letter chord jumps without opening anything,
  with an on-screen hint of the available letters.
- **`npm run check:nav`**: fails when a registry destination has no route, when the sidebar references
  something undeclared, or when a docs `uiPath` does not resolve. No new dependencies.
- Docs: a new **Finding things** section covering the groups, the tabbed surfaces and the keyboard.

### Added — end-to-end API runner (`nashira_e2e`)
- A third project in the solution: a **console runner, not a test project** — `dotnet test` and the CI
  pipeline are untouched — that drives the real HTTP API of a running deployment and records exactly
  what every endpoint returned. Black-box on purpose: no `ProjectReference` to the backend, bodies as
  anonymous snake_case objects, responses as raw JSON, zero NuGet dependencies.
- **171 scenarios across all 36 controllers**: CRUD chains, validation/negative cases, the role matrix
  (anonymous → 401 via the fallback policy, viewer → 403 on writes, operator/admin allowed) and one
  read-only bench target per controller — including the SSE chat stream, file downloads, and
  HMAC-signed webhook deliveries. Steps that depend on systems a dev environment does not have (SMTP,
  Slack, MCP servers, remote Git, NetBox, a paid LLM key) are *probes*: the response is recorded
  rather than asserted, because seeing what the endpoint really does is the deliverable.
- Every run writes **one folder per test case** (`reports/<stamp>/cases/<Controller>/<case>/`): a
  formal document (use case, preconditions — declared plus observed, API interaction table, and per
  step the input data, expected results, output data and the comparison between them), the
  machine-readable result, and the exact payloads sent/received as replayable per-step files. The
  pass criterion is not prose: every `Expect` condition records itself as it runs, pass or fail.
- `bench` repeats a scenario N times, optionally in parallel (min/avg/p50/p90/p95/p99/max + status
  distribution); `coverage` diffs the exercised routes against `/openapi/v1.json`. Coverage stands at
  **192 of 193 documented operations (99.5%)**; the one exclusion is deliberate —
  `PUT /api/Profiles/users/me/profile` would rewrite the profile of the account the runner
  authenticates with.

### Fixed — audit hash chain reported broken on every real PostgreSQL deployment
- Found by the E2E runner on the first live run: `GET /api/audit/verify` answered
  `valid:false, broken_at_sequence:1` while the unit suite stayed green. `AuditChain.ComputeHash`
  hashed the timestamp at .NET tick precision (seven fractional digits) *before* the row was written,
  but `timestamp with time zone` stores microseconds — so the row that came back from the database
  was never the row that was hashed. The unit tests used whole-second timestamps, which survive the
  round-trip unchanged; only a run against real Postgres could surface it (the per-case report shows
  the final digit vanish between the POST's response and the following GET).
- The hash is now taken over the timestamp **at storage resolution**, and `AuditLogger` persists the
  truncated value, so forward writes verify trivially. Rows written before the fix are **not
  re-signed** — rewriting an audit trail so that it verifies would destroy the evidence it exists to
  provide. Their lost digit had ten possible values, so `Verify` tries all ten: a match still proves
  the content agrees with a hash committed at write time, and a tampered row matches none of them.
  Verified against the live database: 2,086 rows spanning both eras, `valid:true`.
- Three regression tests added: a tick-precision chain that simulates the database round-trip (fails
  against the old code), hash equality across sub-microsecond differences, and tampering detection on
  a legacy-format row.

### Removed
- **Workflow plans.** The `WorkflowPlan` entity, `/api/plans`, the `/plans` screen, its docs section and
  its tests are gone, along with the `workflow_plans` table (migration
  `Phase9_RemoveWorkflowPlans`). Approval before a change still exists where it is enforced rather than
  optional: the four-eyes check and gate policies on promotion.
  - The E2E suite was pruned in the same change: its 5 WorkflowPlan scenarios (46 requests, including
    the draft→submitted→approved→executed state-machine chain) went with the controller, taking the
    suite from 176 to **171 scenarios**, and the coverage denominator from 203 to **193** documented
    operations (the ratio survives at 99.5%). E2E reports generated before this removal still show
    the old numbers and exercise `/api/plans` — read them against the changelog entry they predate.

### Verified
- Frontend `npm run check:theme` OK (7 families × 11 stops + 3 shell tokens match `app.css`).
- Frontend `npm run check:nav` OK (32 destinations, 4 tabbed surfaces, every route present, every docs
  `uiPath` resolving).
- Backend `dotnet build` 0/0 and `dotnet test` **359/359** (356 at the WorkflowPlan removal, +3 audit
  regression tests; previous baseline: 128).
- Frontend `npm run check` 0/0 and `npm run build` OK.
- **Live deployment against real PostgreSQL 17** (docker compose): every Phase 9 migration applies on
  startup and the full E2E suite passes — **171/171 scenarios, 934 requests** — exercising the Phase 9
  surface (policies, themes, messaging, email channels, reports, MCP, python modules, job queue) end
  to end. The asynchronous webhook contract is confirmed live: 202 with `{job_id}` and delivery
  deduplication. An earlier run had observed the pre-queue synchronous response
  (`{run_id, status:"completed"}`) — that was a stale container image predating the job-queue build,
  not a code regression; it disappeared on rebuild.

### Known follow-ups
- **Inbound messaging not implemented** (`MessagingInboundEvent`, identity linking, link tokens,
  per-vendor signature verification). That is identity linking + replay protection + OAuth: a feature
  of its own, not the tail of this one. Nothing shipped is inbound-shaped, so adding it later is
  additive. **A deliberate exclusion, not an oversight.**
- No snippet handler and no agent tool for sending messages; `IMessagingDispatcher` is the seam.
- The Python sandbox has **no OS-level isolation by default**. `Python:SandboxCommand` is the hook for
  bwrap/firejail; without it, authoring a `python_snippet` is close to having a shell on the host.

## Phase 7 — Agent tools waves 3 + 4: full app coverage from chat (83 tools)

### Added
- 42 new agent tools completing chat coverage of essentially the whole application — the agent now has
  83 tools.
  - Wave 3 (config): settings (list/set), profiles (list/create/update/delete/assign), prompt skills
    (list/get/create/update/delete), API specs (list/get/create/update/delete), learnings
    (list/create/update/delete), and workflow authoring (create/update/delete/simulate/promote).
  - Wave 4 (admin/sensitive): users (list/create/update/delete), permissions (list-domains/get/set),
    secrets (list/set/clear), credentials (create/update/delete), AI providers (create/update/delete).
- Security posture: admin-only domains use the classifier's `dangerous` level as the role gate;
  destructive ops (`delete_user`, `set_user_permissions`, `promote_workflow`) require `elevated_confirm`;
  other mutations `single_confirm`; reads `autonomous`. Secret material only ever flows IN — every
  secret-bearing tool (secrets/credentials/providers/users) returns booleans (`is_set`, `has_api_key`,
  `has_password`) or non-secret metadata, never a value or hash; no tool reads secret values and
  `get_spec` omits `auth_config`. Verified by a repo-wide grep (the only `Decrypt` is the pre-existing
  `device_connect`). `PermissionsController.Domains` was made public so the permission tools reuse it.
- Every tool is tenant-scoped (`CompanyId == _tenant.CompanyId`), mirrors its REST controller's
  validation, and resolves targets by id or human name where unique. (26 of the config/authoring
  handlers were drafted by parallel subagents from the established template, then centrally reviewed,
  classified, and wired.)

### Verified
- Backend `dotnet build` 0/0 and `dotnet test` 128/128; frontend `npm run check` 0/0. All 83 tool names
  are unique (no registry collision at startup); the new tools are registered, classified, and labelled.

### Known follow-ups
- A reflection-based test asserting every `IToolHandler` implementation has a `PermissionClassifier`
  entry (guards against a future tool silently defaulting to `human_only`).
- By design NOT exposed: reading secret VALUES. `list_credentials` keeps its earlier `read`
  classification (metadata only).

## Phase 7 — Agent tools wave 2: knowledge, audit, loader, workflows

### Added
- Ten new agent tools extending chat coverage into knowledge editing, the audit trail, the loader, and
  workflows:
  - `update_knowledge_article`, `delete_knowledge_article` (by article id from search_knowledge; update
    reuses a new static `KnowledgeController.UpdateAsync` so slug regeneration on rename is shared, not
    duplicated).
  - `list_audit_events`, `get_audit_event`, `verify_audit` — read + integrity check of the
    tamper-evident audit trail. Admin only.
  - `validate_template`, `list_validations` — dry-run template security validation + history. Admin only.
  - `list_workflows`, `get_workflow`, `run_workflow` — inspect and execute workflows; `run_workflow`
    performs real changes so it is classified `elevated_confirm` (operator+, confirmed before running).
- All tenant-scoped, registered in the tool registry, classified in `PermissionClassifier`, and labelled
  in the frontend. Admin-only reads (audit/loader) use the classifier's `dangerous` level as the admin
  role-gate (there is no admin-only `read` level) with an `autonomous` tier — noted in a comment. The
  agent now has 41 tools; knowledge and the audit/loader read surfaces are complete.

### Verified
- Backend `dotnet build` + `dotnet test` (128) pass; frontend `npm run check` 0/0.

## Phase 7 — Agent tools: complete device + inventory-source CRUD from chat

### Added
- Six new agent tools so the chat can fully manage devices and inventory sources: `update_device`,
  `delete_device` (resolved by device_name); `list_inventory_sources`, `create_inventory_source`,
  `update_inventory_source`, `delete_inventory_source` (resolved by source_id or name). All are
  tenant-scoped and mirror the REST controllers; the inventory create/update paths validate `base_url`
  as an absolute http(s) URL and take a `token_secret_ref` (the name of a stored secret, never a raw
  token). Registered in the tool registry, classified in `PermissionClassifier` (device/inventory ·
  write · single_confirm; list is read · autonomous), and labelled in the frontend. Devices and
  inventory sources now have full agent CRUD (create/read/update/delete alongside ping/connect/sync).

### Verified
- Backend `dotnet build` + `dotnet test` (128) pass; frontend `npm run check` 0/0.

## Phase 7 — Agent tool: create_device (add a device by IP from chat)

### Added
- New agent tool `create_device` so nashira can add a device to the inventory from chat. Previously the
  agent had only read/diagnostic device tools (`query_devices`, `device_ping`, `device_connect`) and
  correctly reported it could not create one. `CreateDeviceHandler` requires an `ip_address` (validated
  as a real IP; `device_name` defaults to the IP if omitted), accepts optional
  platform/vendor/os_version/site/role, refuses duplicates (same name or IP in the tenant), and mirrors
  `DeviceController.Post`. It is registered in the tool registry and classified in `PermissionClassifier`
  as domain `device`, level `write` (operator+), tier `single_confirm` — so the user confirms before it
  runs. The frontend `toolLabel` maps it to "Add a device".

### Verified
- Backend `dotnet build` + `dotnet test` (128) pass; frontend `npm run check` 0/0.

## Phase 7 — Permissions page: crash on selecting a user

### Fixed (frontend)
- `/admin/permissions` threw `TypeError: can't access property "read", …[l] is undefined` and rendered
  nothing when a user was picked. Selecting a user flips `selectedUserId` truthy and re-renders the
  matrix table, but the permission load runs in an `$effect` that fires AFTER that render — so the
  table read `matrix[domain].read` while `matrix` was still empty. The table now only renders once
  `matrix` has been built for the selected user (`loadedUserId === selectedUserId`), showing a spinner
  during the gap, and a proper `ErrorState` (with retry) if the permission load fails instead of
  crashing on an empty matrix.

### Added
- `e2e/shot.spec.ts`: a regression test that mocks the users/domains/permissions endpoints, selects a
  user, asserts the matrix renders, and fails on ANY `pageerror` — reproducing the exact crash path.
  Verified passing.

## Phase 7 — Inventory sync: graceful 400 for a bad source URL (was a 500)

### Fixed (backend)
- Running a NetBox sync against a source whose `base_url` was not an absolute http(s) URL (e.g. a bare
  `www.google.com`) threw an unhandled `InvalidOperationException` from `UrlGuard.EnsureSafe` and
  returned a 500. `NetBoxSyncService.SyncAsync` now validates `base_url` is an absolute http(s) URL up
  front and returns a clear 400 ("base_url must be an absolute http(s) URL, e.g. https://…"); the
  `EnsureSafe` call is also wrapped so any SSRF/host rejection surfaces as a 400 instead of a 500.
- `InventoryController.Create`/`Update` now validate the `base_url` format at save time (shared
  `ValidateBaseUrl` helper), so a malformed URL is rejected when the source is saved rather than only
  when a sync is triggered. `dotnet build` + `dotnet test` (128) verified.

## Phase 7 — Chat layout: reclaim the gap between the conversation list and the chat

### Fixed
- In the chat view the message/composer column was `max-w-3xl` centered inside a `flex-1` pane, so on
  wide screens it floated far to the right, leaving a large empty band between the conversation list
  and the chat. The chat row is now width-capped (`max-w-7xl`) and the message/composer column widened
  to `max-w-5xl` so it fills the pane and sits next to the list (≈16px gutter) instead of floating —
  the conversation list stays anchored beside the nav; only the outer right edge carries slack on very
  wide monitors. `npm run check` 0/0; verified by screenshot (composer adjacent to the list).

## Phase 7 — Theme menu placement fix

### Fixed
- The sidebar theme picker (`ThemeMenu`) opened downward and right-aligned; from its bottom-left mount
  in the sidebar the popover fell off the bottom of the screen and was unreachable. Added `openUp` /
  `alignLeft` props (defaults preserve the login top-right behaviour) and the sidebar now opens the
  menu upward, left-aligned — verified fully visible via a click-and-screenshot capture.

## Phase 7 — Full-stack review + fixes (services wired / correctness / UI continuity)

A end-to-end audit of backend and frontend was run to guarantee (1) all services are used and
connected, (2) no functional errors, (3) consistent UI/UX. Baseline evidence: backend `dotnet build`
0/0 and `dotnet test` 128/128; frontend `npm run check` 0/0 and `npm run build` clean; EF migrations
auto-apply on startup with retry (`Program.cs:266`); Docker services (`db → backend → frontend`) share
one network with the frontend proxying to `http://backend:8080`. The DI graph is clean (no missing or
orphan registrations; all 24 tool handlers and 24 `DbSet`s wired); multi-tenancy, authZ, async, JWT
rotation, and the AI tool/SSE loop were reviewed and found correct. The API contract has no broken
calls and no read-side DTO mismatches.

### Fixed (backend)
- `Services/Ai/Tools/ToolDispatcher.cs` — a tool that actually failed (handler returned `{error}` or
  threw) was reported to the client as `success: true`; only gate rejections were `false`. The
  `tool_result` SSE frame now carries the real outcome (`Success = error is null`, or the retry's
  result on self-correction), so the chat tool-call badge reflects failures. `Success` feeds only the
  SSE frame, not the agent loop, so behavior is otherwise unchanged.

### Fixed (frontend)
- Admin route guard gap: 10 of 11 `/admin/*` pages had no client-side gate, so a non-admin reaching
  them by URL saw the admin chrome + a failed load. Added `routes/admin/+layout.svelte` with a single
  `RoleGate require="admin"`, guarding the whole subtree uniformly (also closes an operator-level gate
  on `/admin/settings`).
- `routes/audit/+page.svelte` — the page header and its "Verify chain" action rendered OUTSIDE the
  `RoleGate`; a non-admin saw/could click an action that then failed. Moved the header + action inside
  the gate and aligned the denied-access fallback with the hub's (`ui-surface`).
- `routes/account/+page.svelte` — rebuilt on the design system (`PageHeader` + `Card` + `Input` +
  `Button` + `Alert`) instead of hand-rolled inputs/buttons/alerts, and given a `<title>`. Added a
  `<title>` to `routes/login/+page.svelte` too (the only two pages that lacked one).
- Navigation icon collisions: the sidebar used `ShieldCheck` for Admin (also the hub's Permissions
  glyph) and `ScrollText` for Audit (also the hub's Prompt Skills glyph). Sidebar Admin → `LayoutGrid`,
  Audit → `FileClock` (matching the hub), so each glyph maps to one concept.
- `lib/api/inventory.api.ts` — `updateSource` sent a `kind` field the backend `UpdateInventorySource`
  DTO silently ignores; the update body now omits it (`kind` is create-only).
- Terminology: the admin hub's Permissions description now matches the page ("Per-user tool-domain
  access").

### Verified
- Backend `dotnet build` 0/0 and `dotnet test` 128/128 after the `Success` fix. Frontend `npm run
  check` 0/0 and `npm run build` clean. `/account` (rebuilt) and `/admin` (under the new layout guard)
  screenshotted via the injected-session helper — both render consistently.

### Known follow-ups (not yet actioned)
- Backend LOW findings: concurrent same-conversation turns can lose messages (read-modify-write of
  `MessagesJson`, no optimistic concurrency); self-correction retry re-invokes a handler without
  spending mutation budget (up to 2× budget for a non-idempotent tool); workflow node execution omits
  the `human_only` tier check the chat path enforces (not reachable today — no `human_only` tool);
  OpenAPI path-vs-operation parameter shadowing and duplicate CSV/XLSX headers in the file parser;
  audit filter records `entityId = null` for routes whose param is `userId`.
- Frontend consistency: empty states are rendered three ways (DataTable `empty` vs hand-rolled box vs
  the unused `EmptyState` component); `/workflows` and `/exports` hand-roll row lists instead of
  `DataTable`; `/kitchen-sink` is reachable by any authenticated user (dev showcase, unlinked).
- Unwired backend surfaces with no UI yet: workflow authoring (create/edit/simulate/promote) and
  user↔profile assignment.

## Phase 7 — Admin hub grouping + Phoenix-inspired visual polish

### Changed
- Admin hub (`routes/admin/+page.svelte`): the flat 12-card grid is now grouped by type into labelled
  sections — Access, Credentials & secrets, AI & agent, Configuration, Audit — with Phoenix-style cards
  (soft-tinted rounded icon box + title + description, subtle hover lift).
- Theme refinement toward the Phoenix aesthetic: light mode is now the DEFAULT (`app.html` pre-paint
  script, `prefs` store); the `modern` style gained a softer layered card shadow and slightly tighter
  radii; the light neutral palette was retuned (app `#f5f7fa`, white surfaces, hairline borders).
- `PageHeader`: larger, cleaner page title (`text-2xl`) with more vertical rhythm.
- Contrast sweep: single-shade accent text colors that were dark-mode-optimised
  (`text-primary-300/400`, `text-success/error/warning-300/400`) were replaced with mode-aware paired
  tokens (`text-*-700-300` / `text-*-600-400`) across 7 components, so accents read correctly in both
  light and dark. Affects the sidebar active state, chat tool-call status icons, toasts, the git file
  browser, the loader, and the conversation list.

### Added
- `e2e/shot.spec.ts`: a non-assertion visual-capture helper that seeds a fake admin session + theme
  prefs into `localStorage` and screenshots authed pages without a backend; output under `e2e/shots/`
  (git-ignored).

### Verified
- Frontend `npm run check` (0 errors / 0 warnings) and `npm run build` clean. The admin hub was
  screenshotted in both light and dark (injected admin session): grouped sections, Phoenix cards, and
  legible accents in both modes; the kitchen sink confirmed the design system renders cleanly in light.

## Phase 7 — Left sidebar shell + AI-provider create fix

### Fixed (backend)
- Creating an AI provider 500'd: `AIProvider.Config` (a jsonb `JsonElement`) defaulted to
  `default(JsonElement)` (Undefined), which Npgsql can't serialize ("Operation is not valid due to the
  current state of the object"). It now seeds a valid empty `{}` object. `dotnet build` clean.

### Changed
- Frontend shell: the top nav became a LEFT SIDEBAR (flow-weaver-inspired) — grouped nav (Operations /
  Admin) with icons and a tinted-pill + accent active state, a bottom user card (avatar + role) with
  the theme controls, and an off-canvas drawer on mobile. Audit moved into the sidebar's Admin group;
  the chat surface height was retuned for the nav-less top.
- The frontend proxy (`hooks.server.ts`) strips `Origin` / `Referer` when forwarding to the backend
  (server-to-server), so the backend no longer logs spurious CORS failures.

### Verified
- Backend `dotnet build` clean; frontend `npm run check` (0 / 0) and `npm run build`. The sidebar was
  screenshotted on Devices + Chat (injected session): grouped nav, active pill, and the full-height
  chat all render correctly.

## Phase 7 — Frontend: nav polish (flow-weaver-inspired)
- Nav items gained icons and a tinted-pill active state (bg + ring + accent); the signed-in user now
  shows a role badge, and the header uses a stronger blur. `check` 0/0, `build` ok.

## Phase 7 — Frontend: dedicated light/dark toggle
- Split the colour-mode control into a dedicated sun/moon `ModeToggle` (visible in the nav and on the
  login), next to the style `ThemeMenu` (now style-only). `check` 0/0, `build` ok.

## Phase 7 — Frontend: full-width layout (95%)
- The app shell (nav + main) now uses 95% of the viewport width instead of the fixed `max-w-6xl` cap,
  so it scales to any screen / resolution. Content-specific inner widths (chat thread, forms) keep
  their readable constraints.

## Phase 7 — Frontend: switchable visual styles + theme menu

### Added
- A switchable visual-style system: a `data-style` on `<html>` selects the surface treatment
  (modern / glass / neu / material / flat) and `data-mode` selects light/dark. Components consume
  semantic classes (`.ui-surface`, `.ui-control`, `.ui-raised`, `.ui-app`) whose CSS variables each
  style redefines, so switching restyles the whole app at once. Text + accent stay on the mode-aware
  Skeleton tokens for legibility across every style.
- `prefs` store (`lib/stores/prefs.svelte.ts`): persists style + mode to localStorage; a pre-paint
  script in `app.html` applies them before hydration (no flash).
- `ThemeMenu` (in the app nav and on the login): picks the style and toggles light/dark, with a
  per-style swatch preview.
- Login redesigned onto the design system (Card + Input + Button) so it showcases the active style.
- Retrofitted the core surfaces (Card, Modal, Input, Textarea, Select, Button, StatCard, DataTable)
  to the semantic classes — the style cascades everywhere they are used.

### Changed
- Frontend host port -> 3006 (`FRONTEND_PORT` / `FRONTEND_ORIGIN` in `.env` / `.env.example` +
  compose default).

### Verified
- `npm run check` (0 / 0), `npm run build` (adapter-node), `npm run test:e2e` (3 / 3). The five styles
  were screenshotted on the login (dark + light) and render distinctly — glassmorphism (colour
  gradient + frosted blur), neumorphism (soft-UI extruded / inset), material (elevation + underline
  inputs), flat, and modern.

### Notes
- The style reaches every page through the shared components; a few bespoke surfaces (chat bubbles,
  some page-local panels, the nav header, Alert / EmptyState) still use raw Skeleton tokens and can be
  folded into the system in a follow-up polish pass.

## Phase 7 — Frontend serving: split into its own container (Node adapter)

Reverses the single-image model (the frontend was baked into the backend `wwwroot`) in favour of a
separate frontend service, mirroring flow-weaver — the two stacks are different technologies and now
build / deploy / scale independently.

### Changed
- Frontend adapter: `@sveltejs/adapter-static` -> `@sveltejs/adapter-node`; the app runs as its own
  Node server (`node build` on :3000). Routes stay client-rendered (`ssr = false`).
- `src/hooks.server.ts` (new): a server proxy forwards `/api`, `/health`, `/openapi`, `/scalar` to
  `BACKEND_URL` (`http://backend:8080`) and streams the response 1:1 — including the SSE chat stream —
  so the browser stays same-origin with the frontend. The Vite dev proxy still handles `npm run dev`.
- `deploy/Dockerfile.frontend` (new) builds and runs the Node frontend; the `frontend` service in
  `docker-compose.yml` (`${FRONTEND_PORT:-3000}`, `ORIGIN`, `BACKEND_URL`) sits alongside db + backend.
  The backend `Dockerfile` drops the frontend build stage and the `wwwroot` copy — it is now API-only.
- `.env.example`: adds `FRONTEND_PORT` + `FRONTEND_ORIGIN`; the app is opened at
  `http://localhost:3000` (the backend API stays on `:8080`).
- `playwright.config.ts`: the e2e web server now runs the real `node build` (not `vite preview`).

### Verified
- `npm run check` (0 / 0), `npm run build` (adapter-node), and `npm run test:e2e` (3 / 3, against the
  real `node build` server) pass. The frontend Docker image builds and serves the SPA (`GET /login`
  -> 200 with the app shell); the backend image builds API-only.

## Phase 7 — Full-stack completions (chat uploads, in-chat confirmation, devices, git)

Closes the PLAN §13 chat gaps and the scoped-down device/git bits so the stack is fully testable.

### Backend
- Chat attachments: `ChatRequest` gains `attachments` (`[{filename, content_base64}]`). The
  `AgentConversationRunner` inlines each attachment's text into the turn's user message (binary is
  noted, not dumped; capped at 128 KiB/file) so the agent can read uploaded files; the persisted
  message stays the original text (no history bloat).
- In-chat governance confirmation: wired the existing `PermissionClassifier` tiers (`single_confirm`
  / `elevated_confirm`) into the turn. The runner emits a `confirmation_required` SSE event and
  pauses the turn when the agent calls an unapproved confirm-tier tool (git commit/push, SSH exec,
  send email, NetBox sync, API execute); `ChatRequest.approvals` carries the user's approval so the
  re-sent turn proceeds. `ToolDispatcher` gained `ApproveTools` + `RequiresConfirmation`;
  `IAgentEventSink` / `SseAgentEventSink` gained the event. Additive — autonomous reads and
  create-knowledge are unaffected.

### Frontend
- Chat file upload: the composer attaches files (paperclip -> base64), shows chips, and sends them
  with the turn; the user bubble lists the attachments.
- In-chat confirmation card: on `confirmation_required`, the assistant message renders an
  Approve / Cancel card (humanized tool label + args preview); Approve re-runs the turn with the tool
  authorized via `approvals`.
- Devices: the form gains a credential picker (hidden when the caller can't list credentials, which
  is Admin-gated) and an SSH host-key fingerprint field.
- Git: files open in an editable viewer — Operators can edit and commit (optionally push) a file
  in-browser (`PUT /repositories/{id}/file`); the repo form gains an auth-credential picker.
- Shared `toolLabel` util used by the tool-call block and the confirmation card.

### Verified
- Backend: `dotnet build` clean (0 warnings); `dotnet test` 128 / 128 pass.
- Frontend: `npm run check` (0 errors / 0 warnings), `npm run build`, and `npm run test:e2e` (3 / 3)
  pass.
- Docker: the full image builds and its `/app/wwwroot` contains the SPA (`index.html` + `_app/`), so
  the backend serves the frontend same-origin. A stale image (empty `wwwroot`, JSON at `/`) predates
  the SPA — rebuild with `docker compose up --build`.
- Runtime (live streaming of uploads + the approve -> re-run confirmation loop) still needs a running
  backend with an AI provider + a login session to exercise end to end.

## Phase 7 — Frontend (Slice 8: hardening)

### Added / Changed
- Role-aware navigation: `AppNav` hides the Admin section from non-admins and marks the active section
  with `aria-current="page"`; the link row scrolls on narrow viewports.
- App-level error page (`routes/+error.svelte`): a branded status/message page for 404s and load
  errors, with a link back to chat.
- Expanded the Playwright smoke suite to 3 tests (root-redirect-to-login, protected-route guard,
  login-form rendering) — still backend-free.

### Notes
- Offline / air-gap (T3) verified: the built SPA has no external CDN / font / script references
  (system font stack, bundled `lucide` icons, Tailwind + Skeleton compiled in). The single-image
  Docker wiring (frontend build stage -> backend `wwwroot`) landed in Slice 0.
- English-only copy and explicit empty / loading / error states are consistent across every data
  surface built in Phase 7.

### Verified
- `npm run check` (svelte-check: 0 errors / 0 warnings), `npm run build` (adapter-static), and
  `npm run test:e2e` (3 passing smoke tests) all pass. Phase 7's frontend slices (0-8) are now
  complete; end-to-end runtime verification against a live backend remains the outstanding step.

## Phase 7 — Frontend (Slice 7: audit)

### Added
- Audit trail (`/audit`, `lib/api/audit.api.ts`): the auditability surface — a filterable
  (entity type / action), paginated event list; a per-event detail modal (before/after JSON, hash and
  prev-hash); and a "Verify chain" action that calls `/api/audit/verify` and reports whether the
  hash chain is intact (or the sequence where it broke). Admin-gated (`RoleGate` + API).

### Notes
- The trail is append-only (no create/update/delete surface); the UI reflects that — read + verify only.

### Verified
- `npm run check` (0/0) and `npm run build` pass. Runtime smoke (list, detail, verify) is pending a
  running backend and an admin session.

## Phase 7 — Frontend (Slice 6: admin CRUD)

### Added
- Admin hub (`/admin`): a role-gated launcher for every admin area (auth + role are enforced by the
  API; the hub shows an "administrator access required" state to non-admins).
- Admin CRUD areas, each reusing the operations CRUD pattern (DataTable + modal form + confirm), with
  write actions gated to each endpoint's policy:
  - Users (`/admin/users`) — accounts, roles (viewer/operator/admin), activate/deactivate, password reset.
  - AI Providers (`/admin/providers`) — LLM providers; the API key is write-only.
  - Credentials (`/admin/credentials`) — SSH/device credentials; secret material write-only.
  - Prompt Skills (`/admin/skills`), API Specs (`/admin/specs`, spec content lazy-loaded on edit),
    Learnings (`/admin/learnings`, built-in ones read-only), Profiles (`/admin/profiles`).
- Non-CRUD admin surfaces:
  - Settings (`/admin/settings`) — values grouped by provider, edited per key (Operator-gated saves,
    input widget per setting type).
  - Secrets (`/admin/secrets`) — write-only encrypted store: set-value / clear per provider+key.
  - Permissions (`/admin/permissions`) — per-user tool-domain matrix (read / write / execute).
  - Loader (`/admin/loader`) — dry-run skill/spec security validation + validation history.

### Notes
- Every admin DTO is mapped snake_case -> idiomatic; DataTable row types are `type` aliases
  (interfaces don't satisfy its `Record<string, unknown>` constraint). Write-only secret fields
  (passwords, API keys, private keys) are only sent when the user enters a value; blank keeps the
  stored value.
- Role tiers vary per area (Users / Providers / Credentials / Secrets / Permissions / Loader = Admin;
  Settings write = Operator) — mirrored with `RoleGate`, enforced by the API.

### Verified
- `npm run check` (svelte-check: 0 errors / 0 warnings) and `npm run build` (all `/admin/*` routes)
  pass. Runtime smoke of the admin CRUD is pending a running backend and an admin session.

## Phase 7 — Frontend (Slice 5 cont. — git browser/diff/commit)

Completes the operations slice with the Git surface (the specialized, multi-view area).

### Added
- Git API client (`lib/api/git.api.ts`): repositories CRUD, branches, pull/push/checkout, file listing
  (by path/ref), file read, working-tree diff, and commit. snake_case DTOs mapped; `GitRepo` is a
  `type` alias for DataTable rows.
- Repositories list (`routes/git/+page.svelte`): DataTable with Admin-gated create/edit/delete; the
  repository name links to its detail.
- Repo detail (`routes/git/[id]/+page.svelte`): a branch selector (browse any ref read-only),
  Operator-gated Pull / Checkout / Push, and Files / Changes tabs.
  - `FileBrowser` (`components/git/`): breadcrumb navigation through directories; blobs open in a
    viewer modal (binary files are flagged, not dumped). Re-keyed by ref, so switching branch reloads.
  - `DiffView`: a unified-diff patch with per-line add / remove / hunk coloring.
  - The Changes tab shows the working-tree diff and offers an Operator-gated Commit (message + optional
    push).
- `AppNav` gains a "Git" link. This completes Slice 5 (devices, exports, knowledge, inventory, git).

### Notes
- Repo CRUD is Admin; git operations (pull / push / checkout / commit) are Operator; reads are Viewer —
  each affordance is `RoleGate`-gated over an API that enforces the same split.
- Browsing a ref is read-only and needs no checkout (the file/diff endpoints take a `ref`); Checkout is
  a distinct Operator action that moves the working tree. In-browser file editing (write) and
  auth-credential selection are intentionally out of scope for this slice.

### Verified
- `npm run check` (svelte-check: 0 errors / 0 warnings) and `npm run build` (adapter-static, incl. the
  `/git` and dynamic `/git/[id]` routes) pass. The Playwright smoke still passes.
- Runtime smoke (repo CRUD, browse, diff, commit, pull/push) is pending a running backend, a login
  session, and a registered repository.

## Phase 7 — Frontend (Slice 5 cont. — knowledge + inventory)

Second part of the operations slice: Knowledge (CRUD) and Inventory (NetBox sources + sync).
Git (repo browser / diff / commit) follows in a later part.

### Added
- Knowledge CRUD (`lib/api/knowledge.api.ts`, `routes/knowledge/+page.svelte`,
  `components/knowledge/ArticleForm`): list + client-side filter, create/edit (title, tags, markdown
  content) via a modal form, delete via `confirm()`. Operator-gated writes. Reuses the devices CRUD
  pattern verbatim.
- Inventory (`lib/api/inventory.api.ts`, `routes/inventory/+page.svelte`,
  `components/inventory/SourceForm`): NetBox source list, per-source Dry run / Sync (Operator; a sync
  reports created/updated/unchanged via toast and is confirmed before it mutates), and source
  create/edit/delete gated behind Admin — matching the backend's split policy (read Viewer, sync
  Operator, source CRUD Admin).
- `AppNav` gains "Inventory" and "Knowledge" links.

### Notes
- Inventory reuses the CRUD scaffolding but layers two role tiers on it: Operator for the sync action,
  Admin for source CRUD. Both are `RoleGate`-gated affordances over an API that enforces the same.
- Update DTOs are partial (send-what-you-have); the forms send their full field set, which the backend
  applies field-by-field.

### Verified
- `npm run check` (svelte-check: 0 errors / 0 warnings) and `npm run build` (adapter-static, incl. the
  `/knowledge` and `/inventory` routes) pass. The Playwright smoke still passes.
- Runtime smoke (article CRUD, source CRUD, NetBox sync) is pending a running backend and a login
  session.

## Phase 7 — Frontend (Slice 5: operations CRUD — devices + exports)

First part of the operations slice: the reusable CRUD pattern plus two areas (Devices, Exports).
Knowledge, Inventory/NetBox, and Git follow in a later part.

### Added
- API client: `apiBlob()` for authenticated binary downloads (reuses the shared `request()` core; the
  bearer is required, so a download can't be a bare `<a href>`).
- Devices CRUD (`lib/api/devices.api.ts`, `routes/devices/+page.svelte`, `components/device/DeviceForm`):
  list (DataTable) with a client-side filter (Toolbar + SearchInput), create/edit via a modal form,
  and delete via `confirm()`. Write actions (new / edit / delete) are gated behind the Operator role
  (`RoleGate`), mirroring the API policy. This is the generic CRUD pattern (DataTable + Dialog form +
  confirm) that the remaining operations areas reuse.
- Exports (`lib/api/exports.api.ts`, `routes/exports/+page.svelte`): list export artifacts and download
  each (authenticated blob -> object URL) with per-row progress and empty/error states. `AppNav` gains
  an "Exports" link.

### Notes
- `Device` (and other DataTable row types) are declared as `type` aliases, not interfaces, so they
  satisfy DataTable's `T extends Record<string, unknown>` row constraint — interfaces aren't assignable
  to it in TypeScript.
- The device form omits the optional credential and SSH host-key fingerprint (credential listing is
  Admin-gated; the fingerprint is advanced); both are left untouched on edit.

### Verified
- `npm run check` (svelte-check: 0 errors / 0 warnings) and `npm run build` (adapter-static, incl. the
  `/devices` and `/exports` routes) pass. The Playwright smoke still passes (root -> `/chat` ->
  `/login`).
- Runtime smoke (device create / edit / delete, export download) is pending a running backend and a
  login session.

## Phase 7 — Frontend (Slice 4: governance confirmation)

### Added
- Workflows API client (`lib/api/workflows.api.ts`): typed access to the canonical workflow surface
  — `listWorkflows`, `getWorkflow`, `getPlan` (topological order + rollback safety), `getYaml`
  (compiled YAML), `runWorkflow` (POST), `listRuns`, `getRun` (detail + per-step results). snake_case
  DTOs mapped to idiomatic types (the auth-slice *Shape pattern).
- API client: extracted a shared `request()` core from `api<T>` and added `apiText()` for non-JSON
  endpoints (the workflow YAML compiler returns `text/yaml`) — same bearer / rotating-refresh /
  timeout / ProblemDetails handling, no behavior change to `api<T>`.
- `WorkflowConfirmCard` (`lib/components/workflow/`): the Governance-C confirmation surface — a
  read-only plan preview (numbered topological order with node type + id) plus rollback safety
  (fully-reversible vs. flagged non-reversible steps), gated behind the Operator role (`RoleGate`),
  Confirm -> run via `confirm()`, then the run outcome inline. NOT an editable builder (no DAG
  canvas, no draft/qa/prod promotion UI), per PLAN §8.
- `RunResult`: terminal status + final state + changed/failed/total counts + per-step results.
- Workflows routes: `/workflows` (list -> detail) and `/workflows/[id]` (Plan / YAML / Runs tabs).
  Plan is the confirm card; YAML shows the compiled output; Runs lists past runs (status, final
  state, time) and expands a selected one to its steps. `AppNav`'s "Workflows" link now resolves.

### Notes
- The run executes synchronously server-side (`POST /{id}/run` returns the terminal run), so no
  polling loop is needed; the card fetches `GET /workflows/runs/{runId}` once for the step detail.
- The in-chat confirmation variant stays deferred: today's chat SSE set has no
  "confirmation_required" frame (PLAN §13), so governance confirmation lives on these standalone
  Workflows screens for now.

### Verified
- `npm run check` (svelte-check: 0 errors / 0 warnings) and `npm run build` (adapter-static, incl.
  the `/workflows` and dynamic `/workflows/[id]` routes) pass. The Playwright smoke still passes
  (root -> `/chat` -> `/login`, wordmark renders).
- Runtime smoke of the workflows surface (plan preview, confirm, run, run status) is pending: it
  needs a running backend, a login session, and at least one materialized workflow.

## Phase 7 — Frontend (Slice 3: chat — primary surface)

### Added
- SSE adapter (`lib/api/ai-stream.ts`): a `streamChat()` async generator targeting nashira's
  contract -> `POST /api/ai/chat` via `fetch` + `ReadableStream` (bearer header, not EventSource),
  manual `\n\n` frame splitting, and a preemptive / one-shot refresh so a long turn never sends an
  expired JWT (the SSE path bypasses the REST client's 401 interceptor). Typed events mirror
  `SseAgentEventSink` exactly: `conversation | token | tool_start | tool_result | done | error`.
- Chat session store (`lib/stores/chat.svelte.ts`): the transcript plus the streaming state machine.
  `send()` drives the generator through a PURE REDUCER that rebuilds the assistant message per event
  (required for Svelte 5 `$state`), folding interleaved text deltas and tool calls; `stop()` aborts
  via `AbortController`; `setHistory()` / `reset()` swap conversations.
- Conversation history/CRUD client (`lib/api/conversations.api.ts`): `list`, `get`, `delete` against
  `/api/ai/conversations`, mapping the snake_case DTOs to idiomatic types (the auth-slice *Shape
  pattern). Persisted history is text-only turns (tool calls are live-only), so a reloaded thread
  shows just user/assistant bubbles.
- Chat components (`lib/components/chat/`): `ChatMessage` (user/assistant bubbles), `ChatMarkdown`
  (hardened `marked` render), `ChatToolCalls` (collapsible tool-call block with nashira tool labels,
  auto-expand while running), `ChatThinkingDots` (CSS-only, no animation lib), `ChatComposer`
  (autosizing textarea, Enter-to-send / Shift+Enter newline, send<->stop), and `ConversationList`
  (sidebar: list, active highlight, delete via `confirm()`, new chat).
- Hardened markdown (`lib/utils/markdown.ts`, lifted from flow-weaver): HTML-escape -> `marked` ->
  whitelisted renderer (strips `<a>` / `<img>`, GFM subset), so `{@html}` of LLM output is safe
  against injection.
- Chat route (`routes/chat/[[conversationId]]/+page.svelte`): the primary surface — sidebar +
  streaming thread + composer, auto-scroll (follow-the-stream / jump-to-latest), and route<->store
  sync (deep-linkable conversations; a brand-new turn's server id is reflected into the URL
  mid-stream).
- Root `/` now redirects to `/chat` (the app's home); `AppNav`'s "Chat" points at `/chat`.

### Notes
- Tool calls stream live only; the backend persists just `{ role, content }` turns, so history load
  intentionally omits tool cards. `ChatToolCalls` labels nashira's real tools (devices, git,
  knowledge, APIs, email, NetBox, …) and falls back to the de-underscored name for anything new.
- In-chat file upload and the "confirmation required" chat signal remain backend-coordination gaps
  (PLAN §13) and are out of scope for this slice.

### Verified
- `npm run check` (svelte-check: 0 errors / 0 warnings) and `npm run build` (adapter-static, incl.
  the dynamic `chat/[[conversationId]]` route served via the SPA fallback) pass. The Playwright smoke
  (`app shell mounts and guards to login`) passes against the built SPA: root redirects through
  `/chat` to `/login` and the wordmark renders.
- End-to-end streaming (token / tool_start / tool_result / done) is not yet smoked in a browser: it
  needs a running backend with an AI provider configured and a login session. Reducer and route<->store
  sync were verified by reasoning + typecheck; a live smoke is the next step once a backend is up.

## Phase 7 — Frontend (Slice 2: design system)

### Added
- UI kit barrel (`lib/components/ui/index.ts`): a single import surface for the whole design system,
  so feature pages `import { Button, Card, toast, confirm } from '$lib/components/ui'`.
- App-wide singletons, lifted/adapted from flow-weaver and mounted once in the root layout:
  - `toast` store + `Toaster`: tones (success/error/warning/info), auto-dismiss vs. sticky, optional
    action buttons (undo/retry), and a `fromError` helper wired to the API client's `errorMessage`.
    Accessible live regions (assertive for errors, polite otherwise).
  - `confirm()` promise API + `ConfirmHost`: a drop-in `window.confirm` replacement rendered through
    nashira's `Modal` (native `<dialog>` focus-trap / Esc / inert background), with primary/danger
    tones.
- Components completing the PLAN §3 kit: `ErrorState` (offline vs. server error, with retry),
  `SortableTh`, `Toolbar`, `SearchInput`, and `Kbd` — adapted to nashira's conventions (its `Button`
  has no `icon` prop / `xs` size; the generic modal is `Modal`, not flow-weaver's `Dialog`).
- `/kitchen-sink` dev page exercising every component (buttons, icon buttons, toasts, modal, confirm
  dialog, inline alerts, stat/empty/error cards, badges, status badges, kbd, data table with a custom
  cell, sortable header, pagination, toolbar + search, forms, tabs, spinner, skeleton) — the slice's
  acceptance surface.

### Changed
- `Alert` and `IconButton`: compute their tone-dependent role/classes with `$derived` so they react
  to prop changes (clears the `state_referenced_locally` warnings).
- `Input`: type the `autocomplete` prop as `HTMLInputAttributes['autocomplete']` (was `string`) so it
  matches the DOM attribute type under `svelte-check`.

### Notes
- The kit reuses flow-weaver's mature components by copy-and-adapt (no live dependency), rebranded to
  nashira's Skeleton `cerberus` placeholder tokens. flow-weaver's flow-centric pieces and the
  chart/Mermaid components are intentionally not brought over (PLAN §4).
- `Toaster` and `ConfirmHost` live once in `+layout.svelte`, so every screen gets consistent
  transient-notification and confirmation UX for free.

### Verified
- `npm run check` (svelte-check: 0 errors / 0 warnings) and `npm run build` (adapter-static emits the
  static SPA to `build/`) both pass. Runtime smoke of `/kitchen-sink` is still pending: the page sits
  behind the auth guard, so it needs a running backend + a login session to exercise in the browser.

## Phase 7 — Frontend (Slice 1: auth & session)

### Added
- Auth store (`lib/stores/auth.svelte.ts`, lifted/adapted from flow-weaver): the JWT session
  (access + rotating refresh token + identity) persisted to localStorage under `nashira:auth`, with
  cross-tab `storage` sync so a token rotation in one tab is adopted by the others (avoids the
  backend's reuse-detection logging every tab out), plus an `isExpiringSoon` helper for preemptive
  refresh.
- API client core (`lib/api/client.ts`): the `api<T>` fetch wrapper — bearer injection, a
  single-flight rotating-refresh interceptor (Web Locks cross-tab + per-tab fallback) that replays
  once on 401 and kicks to `/login` on failure, request timeout/abort handling, and typed
  `ApiError`s. Reads nashira's RFC 7807 problem+json (`detail`/`title`) for user-facing messages, and
  ships the `{ items, total, limit, offset }` list envelope for later slices.
- Auth calls (`lib/api/auth.api.ts`): `login` (sets the session), `logout` (best-effort revoke,
  always clears locally), `me`, `changePassword` — mapped to `/api/auth/*`.
- Login page (`/login`) and account page (`/account`: change-password + sign-out), both accessible
  (labelled inputs, alert/status roles, disabled-while-submitting).
- Route guard in the root layout: unauthenticated users are redirected to `/login` with a
  redirect-back param; authenticated users landing on `/login` are bounced to their target;
  protected content is never rendered pre-redirect. `AppNav` now shows the signed-in user + sign-out.
- Role helpers (`lib/guards/roles.ts`) and a reactive `RoleGate` component mirroring the backend
  Viewer / Operator / Admin policies, for gating write/admin affordances in later slices.

### Verified
- `npm install` (249 packages), `npm run check` (svelte-check: 0 errors / 0 warnings across 315
  files), and `npm run build` (adapter-static emits the static SPA to `build/`) all pass.
  `package-lock.json` is committed for reproducible / `npm ci` (Docker) installs.

### Notes
- `lucide-svelte` is kept at flow-weaver's pinned version despite an upstream deprecation notice, so
  lifted FW components import unchanged.

## Phase 7 — Frontend (Slice 0: foundations + backend enablers)

### Added
- `frontend/`: SvelteKit-as-SPA scaffold (Svelte 5 runes, TypeScript, Vite 7,
  `@sveltejs/adapter-static` in fallback mode). Tailwind CSS 4 + Skeleton Labs with a placeholder
  `cerberus` theme (pending the nashira rebrand — a token/theme swap, not a component rewrite).
  English-only UI, no i18n layer.
- App shell: root `+layout.svelte` (header + main container), `+layout.ts`
  (`ssr=false`/`prerender=false` — pure client SPA), an `AppNav` placeholder nav, and a landing page
  whose boot check pings `/health/live` through the dev proxy to confirm backend connectivity.
  `app.css` keeps only air-gap-safe, brand-neutral globals (system font stack, focus-visible ring,
  reduced-motion, scrollbars).
- Dev tooling: the Vite dev server proxies `/api` (SSE-friendly), `/health`, `/openapi`, `/scalar`
  to the backend (`http://localhost:5280`), so the browser is same-origin in dev (no CORS).
  Playwright smoke test (app shell mounts); `check` via svelte-check; `.npmrc` documents the
  air-gapped/offline install path.
- `frontend/PLAN.md`: the Phase 7 work plan (layered/feature-module architecture, the reusable
  component library, role-based routing, chat + governance-C confirmation surfaces, serving model,
  and the 9-slice roadmap).
- Backend enablers for the SPA (`Program.cs`): CORS policy `spa` bound to the existing
  `Cors:AllowedOrigins` config; `UseDefaultFiles` + `UseStaticFiles` to serve the built SPA from
  `wwwroot`; and `MapFallbackToFile("index.html")` (anonymous, lowest priority) so client-side deep
  links resolve — all no-ops in dev where Vite serves the SPA.
- `deploy/Dockerfile`: a `node:22-alpine` frontend stage builds the SPA and copies `build/` into the
  runtime image `wwwroot`, so a single image serves the API and the SPA same-origin (air-gap
  friendly, no CORS in prod).

### Notes
- Framework decisions (resolved): SvelteKit + adapter-static (SPA), static serving from .NET
  `wwwroot`, English-only, placeholder theme with a later rebrand. UI/UX best practices (a11y,
  responsive, explicit empty/loading/error states) are a standing requirement.
- The chat SSE adapter will target nashira's contract (`POST /api/ai/chat`, `token` delta events),
  which differs from flow-weaver's (`/api/ai/chat/stream`, `text`) — reconciled in the chat slice.
  In-chat file upload and the chat "confirmation required" signal are open backend-coordination
  items (PLAN §13).
- Verify the SPA with `npm install && npm run dev` in `frontend/` against a running backend; the
  .NET build is unaffected by the frontend scaffold.

## Backend — audit coverage for human CRUD mutations

### Added
- `AuditMutationFilter`: a global MVC filter that audits every successful mutating controller
  action (POST / PUT / PATCH / DELETE) as one hash-chained AuditEvent — so human CRUD is audited
  without touching each controller (§7.1-bis, "every mutation is audited"). It records the entity
  type (controller), the action (HTTP method → create / update / delete), the route id, and the
  response payload as `after`; reads, failed actions (non-2xx / unhandled exception), and anonymous
  requests are skipped, and it never breaks the response if the audit write fails.
- `[SkipAudit]` opt-out attribute, applied to `AuthController` (auth are security events, not
  business mutations) and `AiChatController` (chat turns — their tools are already audited by the
  ToolDispatcher) at the class level, and to the operation endpoints that self-audit or are
  dry-runs: `loader/validate`, `workflows/{id}/simulate | run | promote`.
- Filter unit tests (POST/DELETE audited with id; reads / failures / anonymous / [SkipAudit] skipped).

### Notes
- Audits at the API boundary (method + route + response). True per-entity before/after snapshots
  would need an EF SaveChanges interceptor; the boundary record satisfies "every mutation is
  audited" and keeps the hash chain simple and reentrancy-free.
- Every mutation path now emits a hash-chained audit event: human CRUD (this filter), agent tools
  (Phase 5 Slice A), and workflow promotion/run (audit.v1 events).

## Phase 5.5 — Canonical engine + governance (Slice 5: SSH command policy + op-risk reconciliation)

### Added
- `SshCommandPolicy` (`ISshCommandPolicy`): per-command SSH governance — classifies each command
  as `read` / `mutation` / `destructive` (unknown → mutation, conservatively) and blocks
  destructive commands (reload / erase / delete / format / factory-reset / clear-config) unless an
  admin opts in via `Ssh:AllowDestructiveCommands`. The flow-weaver PolicyEvaluator equivalent —
  the safety net beyond `device_connect`'s tool-level single_confirm gate.
- `device_connect` now evaluates the policy before running and refuses a batch containing a blocked
  destructive command, naming the offending commands.
- `PermissionClassifier.RiskOf`: maps the tool-level level (read/write/execute/dangerous) to the
  workflow.v1 kit's operation-risk taxonomy (read / mutation / high_risk_mutation), reconciling the
  two. Nashira's real risk model is this + IdempotencyKind for workflow nodes; the oracle has no
  separate op-level classifier (idempotency is snippet-scoped) — documented.
- Config `Ssh:AllowDestructiveCommands` (default false); SSH-command-policy unit tests.

### Notes
- A per-tenant destructive-command policy (a SystemSetting instead of app config) is a later
  refinement; today it is a deployment-wide flag.
- **Phase 5.5 is complete** (all Nashira-side items). The canonical engine has model / DAG /
  compiler / simulation / promotion gate / executor / run persistence, and governance C has the
  simulation-gated promotion, per-command SSH policy, and the reconciled risk taxonomy. The
  `compiler` + `executor` conformance families remain pending FW-captured goldens (external to
  Nashira, needs a running oracle); everything else in the workflow.v1 conformance kit is green.

## Phase 5.5 — Canonical engine + governance (Slice 4: run persistence + tool-backed execution)

### Added
- `INodeExecutor` / `WorkflowExecutor` are now async, and node outcomes carry an `Output` payload.
- `WorkflowRun` + `StepRun` entities + EF migration `Phase5_5_WorkflowRuns`.
- `ToolNodeExecutor` (`INodeExecutor`): runs a node by invoking a Nashira tool bound in
  `config_overrides` (`{ "tool": "...", "args": {...} }`); sentinel + unbound nodes are no-ops;
  RBAC is enforced (the triggering user's role must permit the tool) but the interactive
  confirm/budget gates are bypassed — a promoted workflow is the pre-authorized artifact
  (governance C). Tool error → node failure; success → changed.
- `WorkflowRunService`: builds the DAG, runs the executor, persists the WorkflowRun + StepRun rows,
  and chains each node mutation into the hash-chained audit log as an audit.v1-shaped event.
- Endpoints: `POST api/workflows/{id}/run`, `GET api/workflows/{id}/runs`,
  `GET api/workflows/runs/{runId}` (detail with steps).
- Tests: end-to-end run persistence over the in-memory provider (completed run + steps; failed
  node → failed run with the error code recorded).

### Notes
- Run execution is synchronous within the request (bounded workflows); a queued worker with leases
  + replay recovery is a scaling follow-up.
- A first-class `Snippet` entity (reusable, named node bindings) can replace the inline
  `config_overrides.tool` binding later; the observable execution contract is unchanged.

## Phase 5.5 — Canonical engine + governance (Slice 3: DAG executor + rollback analyzer)

### Added
- `IdempotencyKind` (reified from FW: Idempotent / RequiresCompensation / NonReversible) with
  parse + wire-form + reversibility helpers; a node's tier is read from
  `config_overrides.idempotency` until a snippet registry exists (an implementation detail, not
  contract — the contract is behavior given the tier, not where it is stored).
- `WorkflowExecutor`: deterministic DAG runner — topological order, edge firing by predecessor
  result (success / failure / always; conditional treated as success pending a condition
  evaluator), status aggregation, an `audit.v1` event per state-changing node, and on failure a
  rollback plan (reversible changed nodes, reversed) + a final state (rolled_back / failed). Node
  execution is abstracted behind `INodeExecutor` (production maps to snippets/tools;
  conformance / dry-run drives from a mocked context).
- `WorkflowRollbackAnalyzer`: classifies nodes by idempotency; a workflow is rollback-safe only
  if it has no NonReversible node (the oracle blocks rollback otherwise).
- `GET api/workflows/{id}/plan`: the topological execution order + rollback safety
  (`rollback_reversible` / `non_reversible_nodes`) — reachable, honest planning without real
  node handlers.
- Unit tests: executor (topological order + audit, no_change → no event, reversible failure →
  rolled_back, non-reversible failure → failed, failure-edge routing) and rollback analyzer.

### Notes
- The `executor` conformance family stays pending: its goldens (idempotency results, rollback
  plans, audit-event shapes) must be captured from a running flow-weaver (the oracle), not
  authored from Nashira's implementation (kit spec §4.3 anti-pattern). The executor is covered by
  Nashira unit tests meanwhile.
- Run persistence (WorkflowRun / StepRun) + mapping nodes to real snippets/handlers is a later
  slice; the executor core is engine logic operating on the already-persisted Workflow.
- Phase 5.5 now has the schema / canonicalization / compiler / simulation / gate / executor pieces
  of the canonical engine. The `schema` + `canonicalization` + `gate` conformance families are
  green; `compiler` + `executor` await FW-captured goldens. Remaining: run persistence + node
  wiring, and governance refinements (per-command SSH policy, per-operation autonomy tiers).

## Phase 5.5 — Canonical engine + governance (Slice 2: compiler + simulation + promotion gate)

### Added
- `WorkflowYamlCompiler`: deterministic YAML artifact (`version` / `workflow{id,name,description,
  environment,schema_version}` / `nodes` / `edges` / `input_schema` / `metadata`, mirroring the
  oracle's compiled shape) exposed at `GET api/workflows/{id}/yaml`.
- `SimulationResult` entity + EF migration `Phase5_5_SimulationResults`;
  `WorkflowSimulationAnalyzer` (pure structural checks — invalid DAG, cycle, empty workflow,
  conditional edge without condition as issues; islands disconnected from the `__start__`
  sentinel as warnings) + `WorkflowSimulationService` (persists the result with the canonical
  SchemaHash of what was simulated and points `Workflow.LastSimulationId` at it).
  `POST api/workflows/{id}/simulate`.
- `PromotionGate` (pure, contract-visible state machine): draft → qa → production;
  draft→qa requires a simulation that is present, ok, and hash-fresh — 412 codes
  `simulation_missing` / `simulation_failed` / `simulation_stale` reified from the oracle;
  invalid transitions are 400 `invalid_transition`. `PromotionService` adds the qa→production
  approval rule (`approved_by` required and different from the promoter → 412
  `approval_required`), creates the promoted copy (`PromotedFrom` link, version bump, source
  untouched), and emits the `workflow` / `promote` audit event. `POST api/workflows/{id}/promote`.
- `PreconditionFailedException` (412) added to the DomainException hierarchy.
- Conformance: the **gate family is live** — the adapter now runs vectors through
  `PromotionGate` (4 vectors: missing / failed / stale / ok, all passing; the CI gate now
  requires them). The `simulation_stale` golden was corrected during reification (kit spec §4.3
  bucket 3, pre-1.0 draft): the oracle applies the simulation gate on draft→qa, not qa→production
  as the kit-spec illustration suggested — documented in the vector.
- Unit tests: promotion-gate paths (9), simulation analyzer (5), compiler determinism + shape (2).

### Fixed
- `WorkflowController.Update` no longer clears `LastSimulationId` on a structural edit: the
  oracle keeps the pointer and the gate reports `simulation_stale` (clearing it would surface
  `simulation_missing` instead — an observable contract divergence).

### Notes
- Compiler conformance vectors stay pending deliberately: compiled-YAML goldens must be captured
  from a running flow-weaver (the oracle), not hand-authored from nashira's implementation —
  authoring them from nashira would compare the implementation with itself (kit spec §4.3
  anti-pattern). The compiler is covered by nashira unit tests meanwhile.
- Remaining for Phase 5.5: the executor slice (DAG execution, IdempotencyKind semantics, error
  taxonomy, audit.v1 event emission — lights up the `executor` family) and governance
  refinements (per-command SSH policy, per-operation autonomy tiers).

## Phase 5.5 — Canonical engine + governance (Slice 1: workflow model + Dag)

### Added
- `Workflow` entity (tenant-scoped): workflow.v1 nodes/edges as JSON text, `SchemaVersion`,
  `Environment` (draft / qa / production), `SchemaHash`, `LastSimulationId`, and promotion fields.
  EF migration `Phase5_5_Workflows`.
- `Dag` (`Services/Workflow`): parses nodes/edges into nodes-by-id + adjacency + in-degree,
  enforcing unique node ids and edges that reference existing nodes; `TopologicalOrder` (Kahn)
  rejects cycles; `StartNodes` / `NextNodes`. Adapted from flow-weaver.
- `WorkflowValidator`: the write-time gate — validates nodes/edges against workflow.v1
  (JsonSchema.Net, from the shipped `Data/Schemas/workflow.v1.schema.json`) + acyclic DAG, and
  returns the canonical `SchemaHash` (via `WorkflowCanonicalizer`).
- `WorkflowController` (`api/workflows`): read = viewer, write = operator+. Create/update validate
  + recompute the hash; only draft workflows are editable, and a structural edit clears the
  now-stale `LastSimulationId`.
- Runtime copy of the contract schema at `nashira_backend/Data/Schemas/workflow.v1.schema.json`
  (kept in sync with the conformance kit).
- Dag unit tests (topological order, cycle detection, edge-references-node, duplicate id).

### Notes
- First slice of the canonical engine, built against the workflow.v1 conformance kit (Phase 5.6).
  It exercises the `schema` + `canonicalization` families through the real write path.
- Next slices: the compiler (workflow → YAML) + simulation + SchemaHash-staleness promotion gate
  (the `compiler` + `gate` conformance families), then the executor (`executor` family).

## Phase 5.6 — workflow.v1 conformance kit (contract reified from flow-weaver)

### Added
- `workflow-v1-conformance/` kit artifact (versioned, product-agnostic): `VERSION` (contract
  1.0.0-draft, oracle `fw@95fc69e`), `README.md`, `ci/run-conformance.md` (runner contract +
  equivalence modes + exit codes), `adapters/README.md`, and the `vectors/` family layout.
- `schema/workflow.v1.schema.json`: the structural schema reified from FW's workflow model
  (nodes with id/snippet_id/type; edges with source/target/type; `additionalProperties: false`).
- `canonicalization/SPEC.md`: the normative SchemaHash algorithm (object keys sorted ordinal,
  array order preserved, number trailing-zero normalization, `canonical(nodes) | canonical(edges)`
  → SHA-256 hex), reified from FW's `ComputeSchemaHash`.
- `nashira_backend/Services/Workflow/WorkflowCanonicalizer` (the SchemaHash — also the fingerprint
  the Phase 5.5 staleness gate will use) and `WorkflowSchemaValidator` (JsonSchema.Net) — the first
  native pieces of the canonical engine, built against the kit.
- Golden vectors (pure JSON): schema family (one valid + three invalid), canonicalization family
  (equivalent serializations → equal hash; edit → changed hash), gate family (simulation_missing /
  simulation_stale codes).
- Conformance runner + Nashira adapter in the test project (`Conformance/`): loads vectors,
  dispatches per family, applies equivalence (subset/exact), reports pass/fail/skip. The
  `ConformanceTests` fact is the **CI hard gate** — a failing vector fails `dotnet test`.
- Canonicalizer unit tests (key-reorder stability, number normalization, edit sensitivity).

### Notes
- Nashira's adapter implements the two highest-drift-risk families now — **schema** and
  **canonicalization** (both green). compiler / executor / gate return `not_implemented` →
  **skipped** until the canonical engine (Phase 5.5); their vectors are captured as documentation.
- FW's real `workflow.v1` uses `snippet_id` + node/edge `type` (idempotency is a snippet property,
  `IdempotencyKind` = idempotent / requires_compensation / non_reversible); the kit spec's inline
  `op` examples were illustrative — the schema is reified from FW's actual model.
- Vectors assert hash **relations**, never literal hash values (pinning a literal would couple the
  contract to one implementation).
- Phase 5.6 reifies the contract and gates the implementable families; it is the prerequisite for
  Phase 5.5 (canonical engine + governance built against this kit).

## Phase 5 — Self-correction, loader, audit (Slice C: loader + template security)

### Added
- `TemplateSecurityValidator` (net-new): screens community-authored skill uploads for
  prompt-override / secret-leak / governance-bypass attempts and script markers, and validates
  spec uploads as parseable OpenAPI within size/operation bounds. Returns typed issues
  (error / warning); ok = no error-severity issue.
- `ValidationRecord` entity + `IValidationRecorder` / `ValidationRecorder`: persists every
  validation outcome (kind, target, ok, issues) for history/audit.
- DB-backed prompt skills: `AiPromptSkill` entity + `AiPromptSkillController` (`api/skills`,
  admin) — validated upload / CRUD. `SkillPromptLoader` now merges the built-in `Skills/*.md`
  files with the tenant's `AiPromptSkill` rows (ordered by priority), caches per company, and
  hot-reloads via a version bump on change (`ISkillPromptLoader.LoadAsync(companyId, …)` +
  `Invalidate`).
- `LoaderController` (`api/loader`, admin): dry-run `POST /validate` (skill | spec) and
  `GET /validations` history.
- `AiApiSpecController` create/update now run the security validator + record the outcome (on top
  of the existing OpenAPI parse + index hot-reload).
- EF migration `Phase5_LoaderSkillsValidation` (`ai_prompt_skills`, `validation_records`).
- Template-security-validator unit tests (clean vs injection/leak/bypass skills; valid vs invalid
  spec YAML).

### Notes
- Skills merge order: built-in files (base.md first), then tenant DB skills by priority; the
  fallback prompt is used only when neither exists.
- Phase 5 (self-correction, loader, audit) is complete. Governance model C + the canonical engine
  (Phase 5.5) and the workflow.v1 conformance kit (Phase 5.6) are the next milestones.

## Phase 5 — Self-correction, loader, audit (Slice B: self-correction / learnings)

### Added
- `AgentLearning` entity (tenant-scoped; `CompanyId = Guid.Empty` = system-wide curated
  knowledge): error pattern, category, service/tool scope, fix strategy, fix params, category
  (knowledge | learning), confidence, success/failure counts. EF migration `Phase5_AgentLearnings`.
- `ErrorClassifier`: regex classification of tool errors (timeout / validation / auth / connection /
  field_error / duplicate / not_found), ported from nashira_agent.
- `ILearningsStore` / `LearningsStore`: confidence-ranked fix lookup (tenant + system knowledge,
  with in-memory regex/substring pattern matching), outcome recording (updates confidence,
  tenant-owned only), and discovery of new learnings.
- `SelfCorrectionEngine`: the three-layer loop — (1) a known fix from the learnings store
  (parameter_adjust or escalate hint), (2) a generic static fix (coerce numeric/bool fields the
  error names to strings), (3) an escalate hint by category. Caps corrections per tool per turn.
- `ToolDispatcher` now self-corrects after a tool returns an error: parameter_adjust retries with
  corrected args (recording the outcome as a learning); escalate enriches the result with a
  `_correction_hint`. Formatter/export/bulk tools are skipped; each physical execution (original +
  retry) is still audited.
- `LearningsSeedService`: seeds network-focused system knowledge at boot (SSH host-key mismatch,
  device-not-in-inventory, no-credential, git push rejected); idempotent.
- `LearningController` (`api/learnings`, admin): curate/inspect learnings; reads include read-only
  system knowledge, writes only touch the tenant's own.
- Unit tests: classifier categories, pattern matching + confidence, and the engine's coerce /
  escalate / learning-hint / no-match paths.

### Notes
- The ServiceNow-specific static field corrections from the original are dropped; nashira keeps the
  generic string-coercion rule plus the learnings-store path (curated + discovered).
- A correction retry spends the mutation budget once (one logical mutation), not per physical retry.

## Phase 5 — Self-correction, loader, audit (Slice A: audit trail)

### Added
- `AuditEvent` entity: append-only, immutable, hash-chained per company (PrevHash → Hash),
  capturing the executed artifact (entity type + action + before/after) with actor, ip,
  user-agent, request id, and a per-company monotonic `Sequence`. Not a `BaseModel` — an audit
  row is never soft-deleted or updated.
- `AuditChain` (SHA-256 canonical hash + `Verify`): a deterministic per-row hash bound to the
  sequence, previous hash, actor, artifact, and timestamp; `Verify` walks a chain and reports the
  first sequence where linkage or content breaks.
- `IAuditLogger` / `AuditLogger`: writes hash-chained rows, serialized per company by a semaphore
  so `Sequence` + `PrevHash` stay consistent; actor/context come from `ICurrentTenant` +
  `IHttpContextAccessor`.
- `ToolDispatcher` now audits every mutating tool execution (tier ≠ autonomous) as
  `agent.tool` / `<tool_name>` with the arguments + outcome; read-only tools are not audited.
  Audit failures are logged loudly but never break tool dispatch.
- `AuditController` (`api/audit`, admin, read-only): list (filter by entity_type / action),
  detail (with before/after), and `GET /verify` (recomputes the chain to prove integrity).
- EF migration `Phase5_AuditEvents` (`audit_events`, unique `(CompanyId, Sequence)`).
- Audit hash-chain unit tests (determinism, intact chain, content tampering, broken linkage).

### Notes
- Continuity from T₀ (§7.1-bis): the trail is complete and tamper-evident from the first event;
  signing the chain head is a later hardening step.
- Per-company chaining is single-process (semaphore); a multi-replica deployment needs a
  distributed lock or a DB sequence — Phase 5.5.
- before/after are stored as JSON text (nashira's string-JSON convention) and parsed back to JSON
  in the detail endpoint.
- Controller-level (human CRUD) auditing can be layered on via the same `IAuditLogger`; Slice A
  wires the agent tool-execution path — the primary "executed artifact".
- Remaining Phase 5 slices: self-correction / learnings (§6.6) and the loader
  (upload / validate / hot-reload + template security validator).

## Phase 4 — Integrations (Slices D–H: export, files, knowledge, email, NetBox)

### Added
- Export (Slice D): `ExportArtifact` entity + `ExportService` (ClosedXML) that builds CSV/XLSX,
  `ExportController` (`api/export`, list + download), and the `export_table` tool
  (`export` / read / autonomous) turning an array of row objects into a downloadable file.
- File parsing (Slice E): `FileParsingService` (RFC 4180 CSV parser + XLSX via ClosedXML) and
  the `parse_file` tool (`file` / read / autonomous) for csv/xlsx/json/text → structured rows.
- Knowledge base (Slice F): `KnowledgeArticle` entity (unique slug, `text[]` tags),
  `KnowledgeController` CRUD, and tools `search_knowledge` / `get_knowledge_article`
  (read / autonomous) + `create_knowledge_article` (write / autonomous).
- Email (Slice G): `EmailOptions` (`Smtp:*`, disabled by default) + `EmailService`
  (System.Net.Mail) and the `send_email` tool (`email` / write / single_confirm), which can
  attach an export artifact by id.
- NetBox sync (Slice H): `InventorySource` entity (base url + `${secret:...}` token reference +
  allow-private flag), `NetBoxSyncService` (paginated `/api/dcim/devices`, SSRF-guarded, upserts
  into Device by name and preserves fields NetBox omits), `InventoryController` (source CRUD +
  `/sync`), and the `sync_netbox_inventory` tool (`inventory` / write / single_confirm).
- ClosedXML 0.105.0 dependency; EF migration `Phase4_ExportKnowledgeInventory`
  (`export_artifacts`, `knowledge_articles`, `inventory_sources`).
- Unit tests: CSV parser (quotes / commas / embedded newlines), CSV escaping, and NetBox
  device-field mapping (ip-prefix stripping, role/device_role + status string/object).

### Notes
- Export bytes live in the row (`bytea`); move to blob storage if exports grow large.
- The SMTP password is supplied via environment/secret (`Smtp__Password`), never committed config.
- NetBox is the only inventory `Kind` in v1; the token resolves through the existing Secret store.
  Sync is single-process (no distributed locking).
- Phase 4 (Integrations) is complete: device/credential inventory, SSH command execution, git,
  export, file parsing, knowledge base, email, and NetBox inventory sync. Governance refinement
  (per-command SSH policy, per-operation autonomy tiers) remains Phase 5.5.

## Phase 4 — Integrations (Slice C: git repositories)

### Added
- `GitRepository` entity (name, https url, default branch, optional `AuthCredentialId`,
  local working-copy path, last-fetched timestamp) + EF migration `Phase4_GitRepositories`
  (`git_repositories`, unique `(CompanyId, Name)`). Git auth reuses the Slice A `Credential`
  store (a `Credential` with the PAT in `EncryptedPassword`) — no new secret type.
- `Services/Git`: `IGitService` / `GitService` (LibGit2Sharp 0.31.0), adapted from flow-weaver.
  Per-repo working copy under `{Git:Root}/{CompanyId}/{GitRepositoryId}`, per-repo
  `SemaphoreSlim` serialization, repo-relative path guard, 5 MiB file cap. Operations:
  clone-on-demand, pull (fast-forward), push, branches, checkout, list files, read file
  (base64 for binary), write file + commit, commit + push, diff. HTTPS+PAT transport only.
- `GitController` (`api/git`): repository CRUD (`[Admin]`), read ops (`[Viewer]`), and
  working-copy operations (`[Operator]`).
- Agent tools under `Services/Ai/Tools/Handlers/Git`: `git_list_repositories`, `git_list_files`,
  `git_read_file`, `git_diff` (`git` / read / autonomous); `git_pull`, `git_write_file`,
  `git_commit_push` (`git` / write / single_confirm). Registered in `PermissionClassifier`,
  `ToolRegistry`, and `Program.cs`.
- Config `Git:Root` / `Git:MaxFileBytes` / `Git:DefaultAuthorName` / `Git:DefaultAuthorEmail`;
  `data/` (working copies) added to `.gitignore` / `.dockerignore`.
- Git URL allow-list + path-traversal guard unit tests.

### Notes
- Deferred to a later slice/phase: SSH-key git transport (HTTPS+PAT only for now), inbound
  webhooks (`GitWebhook` + receiver + HMAC signature verification — needs the workflow engine),
  and provider REST calls (`git_create_remote_repository`).
- `LibGit2Sharp.NotFoundException` collides with `nashira_backend.Exceptions.NotFoundException`;
  a `using` alias in `GitService` binds the bare name to ours.
- Single-process concurrency only (per-repo semaphore); multi-replica deployments must pin git
  work to one node until a queued worker exists.

## Phase 4 — Integrations (Slice B: SSH command execution)

### Added
- Python netmiko runner lifted from flow-weaver and renamed to `nashira_ssh_runner.py`
  (+ `nashira_ssh_parsers.py`, `nashira_ssh_clean.py`, `ntc_templates_extra/` bundle, and the
  pytest suite) under `deploy/python/`. Two transports (netmiko PTY + Paramiko direct-exec),
  SHA256 host-key pinning, password/key auth, ANSI sanitation, TextFSM structured output.
  Nashira and flow-weaver share the runner at the contract level only, not as a dependency.
- `Services/Ssh`: `SshRunRequest` / `SshRunResult` / `SshCommandResult` / `SshRunOutcome`
  (the runner's stdin/stdout JSON contract) and `ISshCommandRunner` / `SshCommandRunner`,
  which spawns `python3 nashira_ssh_runner.py`, writes the payload to stdin, enforces a
  `timeout + 30s` process deadline (kills the tree on timeout), and classifies the runner's
  exit-code taxonomy (auth_failed / connect_timeout / host_key_mismatch / ...).
- Agent tool `device_connect` (`device` / execute / single_confirm): inventory-only — resolves
  the device (by name or IP), decrypts its stored credential via `ISecretProtector`, runs the
  commands, and returns per-command output (+ TextFSM `parsed` when structured). Captures the
  host-key fingerprint on first use (trust-on-first-use pinning) and never accepts ad-hoc
  hosts/credentials from the agent.
- SSH JSON-contract unit tests (request serialization, result parsing, exit-code taxonomy);
  runner + parser tests run under pytest (`deploy/python/tests`, 38 tests).

### Notes
- Config: `Python:Executable` (`python3`) and `Python:SshRunnerPath`
  (`/usr/local/lib/nashira_python/nashira_ssh_runner.py`); the Dockerfile already installs
  netmiko/paramiko/textfsm/ntc-templates and copies the runner to `PYTHONPATH`.
- No schema change (reuses the Slice A `devices`/`credentials` tables); no migration.
- Per-command show-vs-config risk classification and destructive-command policy (flow-weaver's
  PolicyEvaluator equivalent) are deferred to governance in Phase 5.5. Until then the tool-level
  single_confirm gate is the safety net (a human confirms each execution).

## Phase 4 — Integrations (Slice A: device + credential inventory)

### Added
- `Device` entity (netmiko `platform`/`device_type`, vendor, os_version, site, role,
  status, optional `CredentialId`, optional `ExpectedSshHostKeyFingerprint`) and
  `Credential` entity (type, username, `AuthMethod` password|key, encrypted
  password/private-key/passphrase as `bytea`). Both tenant-scoped via `BaseModel`.
- `CredentialController` (`[Admin]`, `api/credential`): CRUD; secrets encrypted at rest
  through `ISecretProtector`; `CredentialResponse` exposes only `has_password` /
  `has_private_key` flags, never the bytes. `auth_method=key` requires a private key.
- `DeviceController` (`[Viewer]` read / `[Operator]` write, `api/device`): CRUD;
  `EnsureCredentialAsync` validates that a referenced `credential_id` exists in-tenant.
- Agent tools (all `read` / autonomous): `query_devices` (inventory search by
  keyword/site/role, metadata only), `list_credentials` (names + auth method only,
  never secrets), `device_ping` (TCP reachability on port 22 by device name or IP,
  5s timeout). Registered in `PermissionClassifier`, `ToolRegistry`, and `Program.cs`.
- EF migration `Phase4_DevicesCredentials` (`devices`, `credentials`) with unique
  `(CompanyId, DeviceName)` / `(CompanyId, Name)` indexes and a `devices.IpAddress` index.

### Notes
- Controller-direct pattern (AppDbContext + `ICurrentTenant`), consistent with earlier
  slices; the `Services/Device` and `Services/Credential` scaffold folders stay empty.
- SSH command execution (netmiko runner + `device_connect`) lands in Slice B.

## Phase 3 — Dynamic API engine (Slice B: execute_operation)

### Added
- `IUrlGuard` / `UrlGuard` (SSRF guard): blocks loopback and link-local/metadata
  (169.254.169.254); RFC-1918 private ranges pass only with `allowPrivate` (on-prem
  integrations). Dev bypass via `Security:AllowInternalUrls`.
- `ISecretResolver` / `SecretResolver`: resolves nashira's `${secret:provider:key}`
  refs from the Secret store (Data Protection decrypt) and substitutes them into URLs,
  auth, and bodies right before the wire; unresolved markers are left literal.
- `IRestOperationExecutor` / `RestOperationExecutor`: builds the URL (path/query
  params) + auth (token/bearer/basic/header from `AiApiSpec.auth_config`,
  secret-resolved) + optional JSON body; 15s timeout; SSRF-guarded (`allowPrivate`);
  `verify_ssl` toggle via named clients `rest_call` / `rest_call_insecure`.
- `execute_operation` tool (`api` / execute / single_confirm) wired to the executor.

### Tested
- SSRF guard: metadata/loopback blocked; RFC-1918 allowed only when opted in;
  link-local never bypassed (IP literals, no network).

### Notes
- Phase 3 complete. The dynamic engine (list_apis / discover_operations /
  operation_detail / execute_operation) is fully wired; importing the real integration
  specs + skills is content work for a later step.

## Phase 3 — Dynamic API engine (Slice A: spec index + read tools)

### Added
- `AiApiSpec` entity: per-tenant OpenAPI spec (api name, raw YAML, cached operation
  count, plus base URL + auth config for `execute_operation` in Slice B).
- `YamlSpecIndex` (`IApiSpecIndex`): singleton, per-tenant in-memory index of parsed
  `ApiOperation` metadata (lenient YAML parser; lazy warm + reload on mutation).
  `OperationYamlSlicer` condenses parameter/request-body/response schema from raw YAML.
- Read tools: `list_apis`, `discover_operations` (compact, with optional inline details
  for narrow searches), `operation_detail`. Registered in `PermissionClassifier`
  (`api` / read / autonomous).
- Admin CRUD `AiApiSpecController` (`api/ai/specs`): create/update reparse the YAML to
  refresh the operation count and reload the index; delete is soft + reload.
- EF migration `Phase3_AiApiSpecs`.

### Tested
- OpenAPI YAML parser (operations, methods, tags) and operation slicer (parameters,
  request body, response preview) against a sample spec — no DB, no network.

### Notes
- `execute_operation` + SSRF guard (`IUrlGuard`) + `${secret:...}` resolver land in
  Phase 3 Slice B. The real integration specs (NetBox/ServiceNow/Infoblox/AWX/email)
  are content to port later.

## Phase 2 — Agent core (Slices B + C: tool system + conversation loop + chat SSE)

### Added
- Tool system: `IToolHandler`, `ToolRegistry` (singleton metadata, populated at boot),
  `ToolDispatcher` (scoped, per-turn) with three gates — RBAC (`PermissionClassifier`),
  autonomy tier (`human_only` refused), and a mutation budget. `ToolCallOutput`,
  `ToolSchemas`. Example handlers: `whoami`, `list_ai_providers`.
- Conversation persistence: `AIConversation` entity (per-user chat thread; messages
  stored as a JSON array of user/assistant turns).
- Agent tool-calling loop: `AgentConversationRunner` — stream the LLM, emit text +
  collect tool calls, dispatch each, feed results back, repeat until the model answers
  with no tool calls (cap: 10 iterations, 120s deadline). System prompt built by
  `SkillPromptLoader` (concatenates `Skills/*.md` with a built-in fallback + tool-list
  injection).
- Chat streaming: `AiChatController` `POST /api/ai/chat` streams the turn as Server-Sent
  Events (`conversation`, `token`, `tool_start`, `tool_result`, `done`, `error`) via
  `IAgentEventSink` / `SseAgentEventSink`.
- Conversation management: `AiConversationsController` (`api/ai/conversations`) —
  list / get (with messages) / delete the caller's own threads.
- EF migration `Phase2_Conversations` (`ai_conversations`).

### Notes
- Loop trimmed vs flow-weaver: no per-agent config (`AIAgent`), no `AgentRun`/trace, no
  tool-execution context yet — those arrive with the observability/agents phases.
  Anthropic/Ollama providers are still follow-ups (factory throws until implemented).

## Phase 2 — Agent core (Slice A: LLM providers + AIProvider config)

### Added
- Provider-agnostic LLM abstraction (`ILlmProvider` / `IToolCallingLlmProvider` /
  `IStreamingToolCallingLlmProvider`) with shared types (`LlmMessage`,
  `ToolDefinition`, `ToolCallResult`, `ChatResult`, `ChatStreamEvent`).
- `OpenAiProvider`: HTTP against `{baseUrl}/v1/chat/completions`, **SSE streaming**
  (text deltas, incrementally assembled tool calls, usage) and non-streaming, with
  transient-error retry + jitter. Lifted from flow-weaver.
- `LlmProviderFactory`: resolves a streaming provider from a tenant's `AIProvider`
  row (by id, or the default enabled one), decrypting the API key. OpenAI wired;
  Anthropic/Ollama are follow-ups.
- `AIProvider` entity + admin CRUD (`AIProviderController`, `api/ai/providers`). The
  API key is encrypted at rest (Data Protection) and never returned (`has_api_key`).
- Named `HttpClient` "llm" registered in `Program.cs`.
- EF migration `Phase2_AIProvider` (`ai_providers`).

### Tested
- SSE streaming parser exercised against a canned OpenAI-style event stream (text
  deltas + assembled tool call + done) via a stub `HttpMessageHandler` — the riskiest
  part of the agent loop (§6.1), no network.

## Phase 1 — Platform (Slices B + C: users, profiles, permissions, secrets, settings)

### Added
- Users CRUD (`UsersController`, admin-only, tenant-scoped): paginated list, get,
  create (password policy + hashing), update (email/role/active/password reset), and
  soft-delete (self-delete guarded).
- Profiles (`ProfilesController`): agent-persona CRUD (Admin writes, Viewer reads) plus
  per-user assignment (`users/me/profile`, `users/{id}/profile`). New `Profile` entity;
  `User` gains `ProfileId` + `CustomProfileText`.
- Permissions (`PermissionsController`, admin-only): tool-domain catalog and per-user
  read/write/execute grants (`UserToolPermission`).
- Secrets (`SecretsController`, admin-only): tenant-scoped secret store encrypted at
  rest via Data Protection (`ISecretProtector` / `SecretProtector`, purpose
  `nashira.secrets.v1`). Values are never returned — only metadata + an `is_set` flag;
  set/clear/status keyed by provider + key.
- Settings (`SettingsController`): tenant-scoped non-secret config (Viewer read,
  Operator write); new `SystemSetting` entity.
- `Pagination` helper (clamps limit to 1..200); Data Protection keyring wired in
  `Program.cs` (filesystem, `DataProtection:KeyRingPath`).
- EF migration `Sprint1_ProfilesPermsSecretsSettings` (`profiles`,
  `user_tool_permissions`, `secrets`, `system_settings`, and the new `users` columns).

### Notes
- Controllers use `AppDbContext` + `ICurrentTenant` directly and throw
  `DomainException`s (mapped to problem+json). Entity aliases (e.g. `SecretEntity`)
  disambiguate model types from same-named DTO namespaces.

## Phase 1 — Platform (Slice A: multi-tenancy + auth)

### Added
- Multi-tenancy: `Company`, `User`, `RefreshToken` entities; `ICurrentTenant` /
  `CurrentTenant` (reads JWT claims) / `MutableCurrentTenant` (background contexts);
  automatic `CompanyId` indexing for every `BaseModel`-derived entity in `AppDbContext`.
- JWT authentication: `JwtOptions` (with boot-time key validation) + `AuthOptions`
  (password policy + lockout); `JwtTokenService`; `IPasswordPolicy` / `PasswordPolicy`;
  `RefreshTokenService` (SHA256-hashed tokens, rotation + replay detection);
  `AuthService` (login / refresh / logout / change-password / me / bootstrap);
  `AuthController`. Password hashing via `PasswordHasher<User>`.
- Authorization policies `Admin` / `Operator` / `Viewer` and a secure-by-default
  `FallbackPolicy`; JWT bearer wired in `Program.cs`; infra endpoints (health, OpenAPI,
  root) marked `AllowAnonymous`.
- CRUD foundation: `IBaseService<TResponse, TCreate, TUpdate>` and `ListResponse<T>`.
- Error handling: `DomainException` hierarchy (Validation/NotFound/Conflict/
  Unauthorized/Forbidden) + `DomainExceptionHandler` (RFC 7807 problem+json).
- EF migration `InitialAuth` (`companies`, `users`, `refresh_tokens`); migrations
  auto-applied on boot with retry; Development-only dev seed (default company + admin).
- Auth unit tests: password policy and JWT issuance.

### Notes
- Services talk to `AppDbContext` directly (no repository/UoW layer). Audit/trace,
  rate limiting, and the DTOs' repository seams are deferred to later phases.
- Namespaces follow folder paths (e.g. `Data/Models/` -> `nashira_backend.Data.Models`).

## [Unreleased]

### Added
- Initial monorepo folder structure: `.NET` backend (`nashira_backend/`), test project
  (`nashira_backend.Tests/`), `frontend/`, `deploy/python/`, and `docs/`, mirroring
  flow-weaver conventions. Included the canonical-engine folders
  (`Engine`, `Compiler`, `Workflow`, `WorkflowRun`, `StepRun`, `Job`, `Snippet`,
  `Worker`); omitted FlowWeaver-only promotion-gate folders (`Promotion`,
  `WorkflowPlan`, `WorkflowVersion`, `Slo`, `QaLab`, `Import`); added Nashira-specific
  folders (`Learning`, `Loader`, `Profile`).
- `README.md` documenting the monorepo layout, design principles, and conventions.
- `.gitignore` covering .NET, Node, Python, and secrets/runtime artifacts.
- `deploy/python/README.md` describing the SSH netmiko runner to lift from flow-weaver.
- `.gitkeep` files so empty folders persist in version control (pruned from folders
  that later received real files).
- .NET 10 solution (`nashira.slnx`) with `nashira_backend` (ASP.NET Core Web) and
  `nashira_backend.Tests` (xUnit) projects.
- Minimal bootable `Program.cs`: Serilog structured logging, EF Core + Npgsql
  `AppDbContext` registration, OpenAPI + Scalar (Development), `/health/live` and
  `/health/ready` health checks, and a root status endpoint.
- `BaseModel` (multi-tenant base: `CompanyId`/`IsActive`/`CreatedAt`/`UpdatedAt`) and an
  empty `AppDbContext`.
- App configuration: `appsettings.json`, `appsettings.Development.json`,
  `Properties/launchSettings.json`.
- Docker: `deploy/Dockerfile` (hybrid .NET + Python image), root `.dockerignore`, and
  `deploy/docker-compose.yml` (Postgres + backend).
- GitLab CI (`.gitlab-ci.yml`) with build and test stages.
- Smoke test (`SmokeTests`) validating the project reference and EF Core wiring (passing).

### Security
- Pinned `Microsoft.OpenApi` to `2.9.0` to clear NU1903 (GHSA-v5pm-xwqc-g5wc) from the
  transitive `2.0.0` pulled by `Microsoft.AspNetCore.OpenApi` 10.0.6.
