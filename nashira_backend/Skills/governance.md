# Skill: governance — roles, autonomy, permissions, policies, audit

Four independent gates sit between a request and an effect. All four have to allow it,
and each refuses for a different reason. When something is blocked, say **which** gate
did it — "permission denied" is unactionable.

Spec: `na_governance`; this skill covers its permissions and policy operations.
It is where everything below that has no dedicated tool lives, policies above all
(`/api/policies`, `/api/policies/evaluate`). The audit trail is read through
`na_admin_readonly` instead, which is admin-read-only by construction.

## 1. Role

`admin` | `operator` | `viewer`, on the user. Every tool declares the level it needs:
`read`, `write`, `execute` or `dangerous` (the admin-only gate). `whoami` returns the
current identity; `list_users` / `create_user` / `update_user` / `delete_user` manage
them and are admin-only. You cannot delete your own account.

## 2. Autonomy tier

Independent of role: it decides how much human agreement an allowed call needs.

| Tier | Behaviour | Examples |
|---|---|---|
| `autonomous` | Runs without asking. | every read, `simulate_workflow`, `export_table` |
| `single_confirm` | One confirmation. | `device_connect`, `execute_operation`, `mcp_call`, `send_email`, all config writes |
| `elevated_confirm` | Explicit, deliberate approval. | `run_workflow`, `promote_workflow`, `github_merge_pr`, `delete_user`, `delete_email`, `set_user_permissions` |
| `human_only` | You cannot perform it at all. | — |

An unknown tool falls back to the most restrictive classification and is refused.

**The tier is per tool, but two tools are refined per call.** `execute_operation` and
`mcp_call` each cover reads and writes alike, so a blanket confirmation would make
asking NetBox how many devices it has cost the same approval as deleting one — and
people who confirm constantly stop reading what they confirm. So:

- `execute_operation` runs autonomously when the operation's method is `GET` or `HEAD`.
  That comes from an OpenAPI document an admin uploaded here, so it is evidence.
- `mcp_call` runs autonomously only when the tool declares `readOnlyHint` **and** an
  admin has set `trust_tool_hints` on that server. Untrusted, a server describing itself
  as harmless proves nothing.

The confirmation is only meaningful if the user knows what they are agreeing to. State
what will change, where, and what the effect is **before** asking — not after.

## 3. Per-user resource permissions

`list_permission_domains`, `get_user_permissions`, `set_user_permissions` (admin, elevated).

Grants are keyed by capability domain (`device`, `mcp`, `api`, `workflow`, `git`,
`knowledge`, `secret`, …) or by a specific system: `integration:<slug>`, `mcp:<server>`,
`api:<api>`. `list_permission_domains` enumerates every valid key — use it, do not invent
one.

**The posture is default-allow, opt-in-deny.** A permission row is a *restriction*: if no
row names a resource, the role gate alone decides. A user with no rows at all is
unrestricted. Both the domain and the specific resource are checked, so "no NetBox for
this user" and "no MCP at all" are both expressible and the narrow one cannot be bypassed
through the broad one.

Say this plainly when an admin asks you to "give someone access to X" — usually they
already have it, and what they want is to restrict everything else.

## 4. Policies

Corporate guardrails, admin-only, evaluated server-side. Two actions, and both **fail
closed**: a rule that does not parse blocks rather than allows.

**`deny`** — checked before a run. `when` clauses AND together, values within a clause
OR, and an absent clause places no restriction, so adding a clause always narrows.
Clauses: `environment`, `device_role`, `device_pool`, `snippet_type`,
`description_contains`. A present clause against an empty context does **not** match — a
device-role policy cannot fire on a run that targets no devices. A `deny` with no `when`
denies everything, which is a legitimate way to express a change freeze.

**`gate`** — checked before a promotion, against the workflow's run history. Optional
`on` / `from` / `to` narrow which transition it applies to; `require` lists conditions
such as `successful_runs` or `last_successful_run_within`, scoped `this_workflow` or
`any_workflow`. A gate reports **every** unmet requirement at once, not the first.

An unrecognised `action` (someone typed `warn`) is not treated as a deny. `/api/policies/evaluate`
dry-runs a rule that is not saved yet — offer it before an admin enables one.

## Audit

`list_audit_events`, `get_audit_event`, `verify_audit` (all admin). Append-only and
hash-chained: every mutation is recorded with who, when, entity, action and the
before/after state. `verify_audit` recomputes the chain and returns `valid` plus the
broken sequence if it does not hold.

A broken chain is not a bug to work around. Report the sequence number and stop.
