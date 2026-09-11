# Skill: runs — reading an execution after the fact

A **run** is one execution of one workflow definition. It holds the aggregate outcome;
the per-node detail lives in its **step** rows. Runs are the primary diagnostic surface:
"why did this fail?" always starts here.

There is no dedicated read tool — use `execute_operation` against the `na_runs` spec:
`runs_list` (a workflow's runs, newest first) and `runs_get` (one run **with its steps**).
`run_workflow` starts one.

## What the run tells you

| Field | Read it as |
|---|---|
| `status` | `running` \| `completed` \| `failed`. The coarse answer. |
| `final_state` | `completed` \| `rolled_back` \| `failed`. The one that matters. |
| `schema_hash` | The definition **actually executed** — not the workflow's current one. |
| `environment` | `draft` \| `qa` \| `production`. |
| `node_count` / `changed_count` / `failed_count` | Scope of what happened. |
| `rollback_plan` | Node ids to undo, in reverse execution order. Empty on success. |

`rolled_back` and `failed` are not synonyms. `rolled_back` means every state change made
before the failure was reversible and has been undone. `failed` as a final state means at
least one was **not** — something in the world is still changed. When you report a failed
run, say which of the two it was; the difference decides whether anyone has to go clean up.

The DAG stops at the first failing node. Nodes after it are `skipped`, which is not the
same as `no_change`.

## What a step tells you

`result` is `changed` | `no_change` | `failed` | `skipped`. Alongside it:

| field | what it answers |
|---|---|
| `error` | the sentence. `error_code` only names a category. |
| `input` | what the node was **actually handed**, templates resolved and secrets redacted |
| `logs` | what the handler says it did — the thing that explains a `no_change` |
| `attempts` | 0 = never reached its handler, 1 = normal, more = the retry policy fired |
| `duration_ms` | which step was slow, and which one hit its timeout |
| `output` | the payload downstream nodes address |

Start from the one failed step, read `error`, then `input`. Reading the whole DAG before
finding the failure wastes a turn.

One `error_code` is not a bug in the step and reads like one: **`change_undeclared`**.
The snippet's handler could not determine whether the step changed anything and nobody
declared it. The message names the snippet and the field to set. Fix it by declaring
`changes` in the node's `config_overrides`, or `changes_state` on the snippet where the
action lives in its code. Do not treat it as a handler failure and do not retry the run;
nothing about the target or the code is wrong.

**Read `input` before blaming the node.** The most common cause of a step doing something
unexpected is a `{{ … }}` reference resolving to something other than what the author
assumed — an unresolvable one is left literal, so the handler receives the template text
itself and fails on a type it never expected. The workflow definition cannot show you
this; the step's `input` is the only record of what the run actually produced.

`attempts: 0` means the step never ran at all: the snippet did not resolve, or the node
was per-device on a run with no devices. Nothing about the handler explains it.

`retryable: false` means re-running changes nothing — a wrong parameter, a denied module,
a blocked command. Do not offer "let's try again"; say what has to change first.

## `schema_hash` is the trap

A run records the definition it executed. If the workflow has been edited since, the
graph you get from `get_workflow` is **not** the graph that ran. Before explaining a
failure in terms of a node, check whether the hashes agree — otherwise you will confidently
explain a node that did not exist at the time.

## Diagnostic order

1. `runs_get` on the run id → status, `final_state`, `failed_count`, `rollback_plan`.
   **If the run carries a top-level `error` and `node_count` is 0, stop here**: nothing
   executed, and the reason is that field. A policy denied it, the graph would not
   parse, the targets resolved to nothing. Looking for a failed step will find none and
   you will report "the run failed but no step reported an error", which is a dead end
   for the person who has to fix it.
2. The failed step: `node_id`, `error`, `input`, then `output`.
3. Only if the error is about the graph rather than the step: `get_workflow`, after
   confirming `schema_hash` still matches.
4. Report: what failed, whether state was reversed, and what has to change. If the run
   left something changed and un-reversed, lead with that.

## Runs nobody started by hand

The run itself says: `trigger` is `manual`, `agent`, `schedule`, `webhook`,
`git_webhook` or `test`. Answer "why did this run at 3am" from that field rather than
inferring it from the absence of a user.

From there the source row carries the rest. A cron or an inbound webhook is a
`WorkflowTrigger` — `last_run_at`, `last_run_status`, `last_run_id`, `last_error`,
`fire_count`, and `next_run_at` for what is still coming. For a module-specific trigger,
use its source tools only when they are present in the current tool list. An unexpected
production run is a question about the trigger, not about the workflow.

`last_error` on a trigger means two different things depending on `enabled`: on an
enabled trigger it is why the last run failed, on a disabled one it is why the scheduler
switched it off. Say which one you are reading.
