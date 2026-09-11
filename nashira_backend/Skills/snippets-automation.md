# Skill: snippets — automation-owned step types

A snippet is the reusable unit referenced by a workflow node's `snippet_id`.
Always call `list_snippets` before authoring a workflow and use an existing UUID,
or call `create_snippet` first. Never invent placeholders such as `__ping__`;
only `__start__`, `__end__` and `subflow` are non-UUID references.

This deployment's Automation-owned handlers are:

| `type` | Behaviour |
|---|---|
| `transform` | Maps values from step input without external I/O. |
| `rest_call` | Executes a raw URL or an operation from an available API spec. |
| `python_snippet` | Runs approved Python in the sandbox. |
| `ping` | Tests TCP reachability for a host or literal IP. |

Treat `list_snippets.known_types` as authoritative. Other capabilities may add
types in a larger deployment; a stored snippet whose handler is unavailable is
intentionally absent from this deployment's catalogue.

Code-carrying snippets require a `logic_diagram_mermaid`. For
`python_snippet`, imported root modules must be approved and ready in the Python
module allowlist. The node's resolved `config_overrides` arrive as `input`, and
the value assigned to `result` becomes the step output.

Declare whether author-supplied code changes state. A missing declaration fails
the step rather than guessing. Never mark an action read-only merely to obtain a
clean rollback plan.
