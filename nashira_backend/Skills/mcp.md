# Skill: MCP servers — someone else's tools, someone else's credentials

An **MCP server** is an external endpoint that exposes its own tools with their own
argument schemas and its own upstream credentials. It is a third registry alongside
integrations and API specs, and it is invisible to `list_apis`.

Tools: `list_mcp_tools`, `mcp_call`. Spec: `na_mcp` (`/api/mcp/servers`, plus
`/{id}/sync`, `/{id}/check`, `/{id}/tools`, `/{id}/call`).

## Calling a tool

`list_mcp_tools` first, always. It returns the server name, the tool name and the
`input_schema` — none of which you can guess, because they are defined by the remote
server and change when it is re-synced. Then `mcp_call` with the exact tool name and
arguments matching that schema.

When a system is registered both as an integration and as an MCP server, **prefer the
MCP tools**: they are purpose-built for it and carry their own credentials, so they
cannot be broken by a mis-set `auth_config` on this side.

## Nothing here distinguishes a read from a write

The protocol has no notion of it. `read_only_hint` on a tool is exactly that — a hint
from the remote server, and it is only trusted when the server row has
`trust_tool_hints` set. So:

- `mcp_call` is confirmed before it runs. The one exception is a tool that declares
  `readOnlyHint` on a server whose `trust_tool_hints` an admin has set — that combination
  runs autonomously. Absent it, do not argue that a particular tool "just reads": the
  claim is the server's, and an untrusted server's self-description is not evidence.
- The `mcp_call` snippet type defaults to `non_reversible` and cannot be declared
  idempotent. The rollback planner will not promise a reversal for it.

Before calling, say which server, which tool, and what it will do — the tool name alone
tells the user nothing about what is about to happen on their infrastructure.

## Registration and sync

`POST /api/mcp/servers` — `name`, `url`, `auth_type` / `auth_config` or
`auth_credential_id`, optional `headers`, `tls_skip_verify`, `allow_private_network`,
`trust_tool_hints`. Then `/{id}/sync` to discover the tools; it reports `discovered`,
`created`, `updated`, `disappeared`, `reappeared`.

A tool that stops being advertised is marked `disappeared_at` rather than deleted — so a
server that comes back does not lose its permission grants. A tool with a
`disappeared_at` is not callable; re-sync before concluding it was removed on purpose.

`tool_count` and `tools_synced_at` on the server row tell you whether the catalogue is
current. A server registered but never synced has zero tools and is not broken.

## The failure that is not yours to fix

The health probe is a `tools/list`. That call succeeds using **this** side's credentials
— it says nothing about the server's own upstream session. So a server can list its tools
happily and fail every actual call.

When a tool returns "session is not logged in", "unauthorized" or similar, that is the
remote server's upstream session, not a Nashira credential. The server is marked
`degraded` and the error says so. Name the server, say the session expired on its side,
and stop — there is no credential here that fixes it, and re-syncing will not help.

Two OAuth cases have a precise answer, so give it instead of a generic one:

- A server whose `status` is `needs_authorization` uses the authorization-code grant
  and its consent lapsed (or never ran). Only an admin at a browser can fix it — the
  Authorize action on that server in `/admin/mcp`. Say exactly that.
- A tool error naming the **Google Workspace Developer Preview Program** means the
  credentials are fine; the Google Cloud project behind the server's OAuth client is
  not enrolled in that preview program. Point the admin at `docs/mcp-oauth.md`.

## SSRF and TLS

`allow_private_network` is required for an endpoint on a private address; without it the
guard refuses before any request goes out. `tls_skip_verify` disables certificate
validation — if a user asks for it, say plainly that it removes the guarantee that the
endpoint is who it claims to be, then do what they decide.
