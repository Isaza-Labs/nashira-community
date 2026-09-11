# Skill: integrations and API specs — calling an external system

Two registries, one call path. An **integration** is a registered external HTTP system
(`base_url`, auth, health probe) plus the catalogue of **actions** projected from its
specs. An **API spec** is an OpenAPI document whose operations `execute_operation` can
call. A spec may link to an integration to inherit its URL and credentials.

Tools: `list_integrations`, `list_apis`, `discover_operations`, `operation_detail`,
`execute_operation`, `list_specs`, `get_spec`, `create_spec`, `update_spec`,
`delete_spec`. Spec: `na_integrations`.

## The call sequence, in order

1. `list_apis` — what is registered, with an operation count and tags.
2. `discover_operations(api=…, keyword=…)` — compact summaries. Filter by `api` when you
   know the target; `keyword` is OR-matched, so a two-word query widens rather than
   narrows.
3. `operation_detail(operation_id)` — **always**, before calling an operation you have not
   called before. Parameter shapes shift between APIs and a guessed body is a 400 at best.
4. `execute_operation` — path template slots like `{id}` go in the top-level
   `path_params`, **not** inside `body`. Query strings go in `query_params`.

`get_spec` returns the whole YAML and can be very large. Use `list_specs` when you only
need metadata.

## Wiring a system up

When the specs are already in hand, do it in one call: `POST /api/integrations/bundle`
takes the integration plus any number of skills and specs, creates them in a single
transaction and materialises the actions. If anything fails validation, nothing
persists — no half-configured integration left behind for someone to find later.

Step by step, when they are not:

1. **Credential** with the material (`create_credential`).
2. **Integration**: `base_url`, `type`, a **relative** `health_check_path` (`/status/`),
   and `auth_credential_id`.
3. **Health check** (`/api/integrations/{id}/check`) — see the status table below.
4. **Spec** — `POST /api/integrations/{id}/specs` with `api` and `content`. It upserts
   the spec, links it, and re-materialises the action catalogue in the same call,
   reporting `actions_created` / `actions_updated` / `actions_disappeared`. There is no
   auth to supply: the spec inherits the integration's, which is the point of the link.

Prefer that over `create_spec` + `sync-actions`. Registering a spec globally and linking
it afterwards works, but forgetting the sync leaves an integration whose spec is
registered and whose action list never moved, with nothing on screen saying so.
`create_spec` with `integration_id` and `auth_type: "none"` is still the right tool when
the spec is not being attached to anything, or when it needs its own base URL — a spec
that declares its own `auth_type` uses its own `auth_config` instead, and an empty one
sends the call out unauthenticated.

A prompt skill scoped to one integration attaches the same way:
`POST /api/integrations/{id}/skills` with `name` and `content`. It joins the agent
prompt on the next turn. `GET /api/integrations/{id}/bundle` lists both.

## Auth shape

`auth_config.method` decides the header:

| method                      | Header sent                                                                              |
| --------------------------- | ---------------------------------------------------------------------------------------- |
| `bearer`                    | `Authorization: Bearer <token>`                                                          |
| `token`                     | `Authorization: <prefix> <token>` — `prefix` defaults to `Token`. **NetBox needs this.** |
| `basic`                     | `Authorization: Basic <base64 user:pass>`                                                |
| `api_key`                   | `<header>: <key>` — `header` defaults to `X-API-Key`                                     |
| `oauth2_client_credentials` | Nashira fetches the token and sends it as a bearer                                       |

**Inline material in `auth_config` wins over the linked credential.** An integration
created with a token baked in ignores every credential it is later pointed at. The
response flags this as `has_inline_credentials: true`; the fix is to clear those fields,
not to re-link the credential.

## Status

| Status        | Means                                                           | First thing to check                                                                      |
| ------------- | --------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `healthy`     | Answered 2xx/3xx.                                               | —                                                                                         |
| `degraded`    | Answered, but rejected the credentials or the probe path 404'd. | The credential, then `health_check_path`.                                                 |
| `unreachable` | Nothing answered.                                               | URL, port, network, and the SSRF guard — a private address needs `allow_private_network`. |

`last_check_error` and `last_checked_at` carry the detail. A stale `last_checked_at`
means nobody has probed it since the last change; re-run the check before diagnosing.

## Actions

Each action row is `operation_id`, `method`, `path`, `category`, `read_only`, `enabled`.
`read_only` is what lets a workflow author mark an `integration_action` node idempotent
honestly. Disabling an action removes it from what workflows and permissions can bind —
it does not delete anything upstream.

## Registries are independent

`list_apis` covers **only** registered OpenAPI specs. A system can be reachable through
MCP or configured as an integration and be completely invisible to it. Before telling a
user that something is not registered, check all three (`list_integrations`,
`list_mcp_tools`, `list_apis`) — this is Rule 0 in the base prompt and it is the single
most common wrong answer.
