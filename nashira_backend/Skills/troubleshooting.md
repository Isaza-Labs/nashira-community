# Skill: troubleshooting — the failures that actually happen

A diagnostic map. Each entry is a symptom, what it really means, and the cheapest next
call. Read the error text before choosing: several of these look alike and have opposite
fixes.

## Authentication and reachability

| Symptom | Means | Next |
|---|---|---|
| `403 "Authentication credentials were not provided"` | **Nothing was sent.** The spec declares an auth type with no material, or the integration has no credential. | Check the spec's `auth_type` and the integration's `auth_credential_id`. |
| `403 "Invalid token"` | Something **was** sent and was wrong. A different problem entirely. | Check the credential itself, and the auth `method` — NetBox needs `token` with prefix `Token`, not `bearer`. |
| Integration `degraded` | It answered, but rejected the credentials or the probe path 404'd. | The credential, then `health_check_path`. |
| Integration `unreachable` | Nothing answered. | URL, port, network, and `allow_private_network` for a private address. |
| Credentials look correct and are still ignored | Inline material in `auth_config` overrides the linked credential. | `has_inline_credentials: true` in the response. Clear those fields. |

## "The system isn't registered"

`list_apis` covers **only** OpenAPI specs. Check `list_integrations` and
`list_mcp_tools` before saying a system is absent — this is the single most common wrong
answer and it is cheap to avoid (Rule 0, base prompt).

## MCP

An MCP tool returning "session is not logged in" / "unauthorized" is that server's own
upstream session expiring. It is not a Nashira credential and you cannot fix it from
here. Name the server and say so. Its health probe is a `tools/list`, which is why it can
report healthy and fail every call.

A tool that is missing may be marked `disappeared_at` from a previous sync. Re-sync
before concluding it was removed.

## Workflows

| Symptom | Means |
|---|---|
| `simulation_missing` / `simulation_failed` / `simulation_stale` | The draft→qa gate. Stale means the definition changed after the simulation — re-simulate; the gate compares a hash. |
| `approval_required` | qa→production needs `approved_by`, and it must differ from the promoter. |
| `invalid_transition` | A step was skipped, or the workflow is not in the state you think. |
| Update refused | Only **draft** workflows are editable. |
| A run refuses on a device by name | That device's `allow_draft` / `allow_qa` / `allow_production` does not include the run's environment. |
| Denied before the first node | A `deny` policy. The refusal names the policy and its reason — relay both. |

## Steps

- **`python_snippet` fails on an import** — the module is not on the allowlist. Only an
  admin can add it, and only root-module names. If the module is network-using, the
  snippet also has to opt into `network_enabled`.
- **`python_snippet` refused before running** — it uses dynamic import machinery, which
  defeats import checking. That is a refusal, not a sandbox failure.
- **An SSH command is blocked** — it classified as destructive, and
  `Ssh:AllowDestructiveCommands` is off. Unrecognised commands classify as mutations, not
  reads. Do not rephrase to get past the classifier.
- **A step failed with `retryable: false`** — re-running changes nothing. Say what has to
  change instead of offering another attempt.

## Reading a failed run

`runs_get` → `final_state`. `rolled_back` means every state change was reversed;
`failed` means at least one was not and something is still changed out there. Lead with
that distinction. Then the one failed step's `error_code`, and only then the graph — and
check `schema_hash` still matches the current definition before blaming a node.

## When the platform corrected you

- A failed result carrying `_correction_hint` has already been diagnosed. Follow the hint
  before anything else, or relay it when it needs action outside your tools.
- Some failures are retried automatically with corrected parameters. If the result shows
  success, do not re-run the call.
- Two identical failed attempts is the limit. Explain the error and what is needed.
