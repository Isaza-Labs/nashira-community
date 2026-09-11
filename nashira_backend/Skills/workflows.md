# Skill: workflows — authoring, simulating, promoting

A **workflow** is a `workflow.v1` graph. It is not a script: nodes bind to catalogued
**snippets** (see the snippets skill), edges carry the control flow, and the whole thing
moves through three environments before it touches production.

Tools: `list_workflows`, `get_workflow`, `create_workflow`, `update_workflow`,
`delete_workflow`, `bulk_delete_workflows`, `simulate_workflow`, `promote_workflow`,
`run_workflow`. For deleting more than a handful of workflows use
`bulk_delete_workflows` (by `workflow_ids`/`names`, by filters `environment` /
`name_contains`, or both — filters restrict the list): one call, one confirmation, one
mutation-budget slot. Preview with `list_workflows`, show the user the names and the
count, pass the count as `expected_count`. The same applies to snippets with
`bulk_delete_snippets` — snippets still referenced by an active workflow are skipped
and reported, not deleted.
Spec: `na_workflows` (also exposes `/yaml`, `/bundle` and `/plan`, which have no
dedicated tool).

**Before `create_workflow`, call `list_snippets`.** A node binds to a snippet UUID, and
the only way to have one is to read it off the catalogue or off `create_snippet`. This
is the single most common way an authoring turn goes wrong: the workflow gets built out
of `__start__` and `__end__` because no real step id was ever obtained, it validates, it
promotes, and it runs green having done nothing.

## The definition

`create_workflow` / `update_workflow` take two JSON arrays, validated against the schema
and checked for cycles before anything is stored.

**Node** — `id` (required, ≤128 chars), `snippet_id` (required), and optionally `type`,
`x`, `y`, `config_overrides`.

- `snippet_id` is a snippet UUID **that exists**, or one of the three literals
  `__start__`, `__end__`, `subflow`. Nothing else is accepted. A reference that is
  neither is rejected at write time with `snippet_reference_invalid`; a UUID with no
  snippet behind it is rejected with `snippet_not_found`.
  - Do **not** invent a placeholder in the style of the literals. `__ping__` is not a
    thing: `ping` is a snippet *type*, and a node runs it by referencing the UUID of a
    snippet of that type. If the snippet you need does not exist yet, create it first
    with `create_snippet` and use the id you get back.
  - A workflow whose only nodes are `__start__` and `__end__` is not a draft to refine
    later — it is a graph that will run, report every node as no-change, and read as a
    success. If you could not obtain a real snippet id, say why and stop; do not store
    the shell.
  - `subflow` is accepted by the schema and **not implemented** in this build. A node
    that uses it is reported `skipped` at run time, not executed.
- `type` is `task` (default), `decision` or `subflow`.
- `config_overrides` is a free object merged over the snippet's own configuration. It is
  where per-node input lives, and where a node declares `idempotency`.

**Edge** — `source`, `target`, `type` (all required), plus optional `condition`,
`source_handle`, `target_handle`. `type` is `success` | `failure` | `always` |
`conditional`. **A `conditional` edge MUST carry a non-empty `condition`**: a run whose graph
has one without it is REFUSED before any step executes, with a message naming the edge.

That changed on 2026-08-29 — such an edge used to be accepted and simply never fired. If you
authored one, the workflow ran, the branch was absent, and nothing in the run said why. An edge
type whose entire purpose is to gate cannot default to a silent no, so it is now a refusal.
`workflow_simulate` still reports it as `conditional_missing_condition` before you ever run,
which is the friendlier place to meet it.

A condition that is present and evaluates to FALSE is not an error — that is the feature
working, and the target is recorded `skipped`.

No other properties are accepted on either object — the schema is `additionalProperties:
false`, so a stray field is a 400, not a silent drop.

## draft → qa → production

Environments are a state, not a label. Only **draft** workflows are editable:
`update_workflow` on a qa or production copy is refused. Promotion creates an immutable
copy in the target environment.

`promote_workflow` walks exactly one step, and each step has its own gate:

| Transition | Gate |
|---|---|
| `draft` → `qa` | A simulation must exist, have passed, and match the current definition. |
| `qa` → `production` | `approved_by` must differ from the promoter. Two people, always. |

The four refusals you will actually see, with what each one means:

- `simulation_missing` — call `simulate_workflow` first.
- `simulation_failed` — the last simulation found structural issues; fix and re-simulate.
- `simulation_stale` — the definition changed after the simulation. Re-simulate; do not
  argue that nothing important changed, the gate compares a hash.
- `approval_required` — the qa → production gate: `approved_by` is missing, or it names
  the same person doing the promotion.
- `invalid_transition` — you tried to skip a step or promote from the wrong state.

`simulate_workflow` is structural only: it proves the graph is sound (reachability,
dangling edges, unbound nodes), not that the steps do the right thing. It runs nothing
and touches no device, which is why it is autonomous. Behaviour is proven by acceptance
tests, which run the workflow for real and assert on the result. They live in the
**`na_triggers`** spec, not `na_workflows` — `tests_list`, `tests_create`, `tests_run` —
so `discover_operations(api="na_triggers")` is where to find them.

## Running one

`run_workflow` executes against the workflow's own environment and performs real changes,
so it is confirmed before it starts and needs elevated approval. Before calling it:

1. `get_workflow` to confirm which environment and which definition you are about to run.
2. Check the target devices allow that environment — the `allow_draft` / `allow_qa` /
   `allow_production` trio on the device row. A device that does not allow it is reported
   by name as a refusal; the run never silently skips it.
3. A `deny` policy can stop the run before the first node. The refusal names the policy
   and its reason — relay both.

## Triggers

A workflow runs unattended through a trigger: `cron` (needs `cron_expression` and a
`timezone`, IANA name, default UTC) or `webhook` (gets a `route` and a signing secret).
There is no dedicated tool — use `execute_operation` against the `na_triggers` spec.

Two things that catch people out:

- The trigger secret is returned **exactly once**, on create and on rotate. There is no
  endpoint that reads it back. If the user did not record it, rotate.
- `allow_unsigned` turns off signature verification on a webhook trigger. Say plainly
  what that means before enabling it: anyone who learns the route can start the run.

## Editing safely

- Change the draft, re-simulate, re-promote. Never describe editing a production
  workflow as an option — the API will refuse it and the user will have wasted a turn.
- `update_workflow` replaces the definition wholesale when you pass `nodes`/`edges`.
  Read the current graph with `get_workflow` and send the full modified arrays; sending
  only the nodes you changed deletes the rest.

## Sharing a workflow with another instance

`/yaml` is the readable artifact and it does **not** survive crossing instances: it
carries this installation's snippet UUIDs, none of which resolve elsewhere, so the
import is refused by the same reference gate that protects `create_workflow`.

`GET /api/workflows/{id}/bundle` is the portable one. It carries the nodes verbatim plus
the full definition of every snippet they name, so the receiving instance recreates what
it lacks instead of rejecting the file. `POST /api/workflows/import` detects a bundle by
its `kind` marker and takes the deterministic path; anything else is treated as the
plain artifact exactly as before.

An imported bundle always lands in **draft** with no simulation link, whatever
environment it held on the source — so the promotion gates apply here in full. Read
`import_notes` on the response before promoting: it records what the translation cost
(a dropped network flag, a `target_mode` with no local equivalent, a snippet matched by
name rather than slug), and `created_snippets` names the rows that arrived unverified
and are worth opening first.
