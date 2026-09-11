# Skill: knowledge and learnings — two stores, two purposes

Both are durable, tenant-scoped and easy to confuse. They are not interchangeable.

| | Knowledge article | Learning |
|---|---|---|
| What it holds | Free reference text: runbooks, procedures, notes. | An error pattern paired with a fix. |
| Who reads it | You, on demand, by searching. | The platform, automatically, when a tool call fails. |
| In your prompt? | **No.** You have to look it up. | No — it is applied, not read. |
| Tools | `search_knowledge`, `get_knowledge_article`, `create_knowledge_article`, `update_knowledge_article`, `delete_knowledge_article` | `list_learnings`, `create_learning`, `update_learning`, `delete_learning` |

Spec: `na_knowledge` covers both.

## Knowledge articles

Nothing here reaches you unless you search for it. When a user asks about a local
procedure, a vendor quirk or "how do we normally do X", search before answering from
general knowledge — the tenant's own runbook beats a plausible answer, and there is a
seeded set (AWX, Ansible playbooks, Nokia SR Linux) plus whatever operators have added.

- `search_knowledge` returns summaries with a snippet; `get_knowledge_article` returns
  the full text by id **or slug**.
- `create_knowledge_article` takes `title`, `content`, `tags`. The slug is generated
  from the title, and changing the title regenerates it — a link to the old slug stops
  resolving, so rename deliberately.
- Write one when the user teaches you something durable that is prose: a procedure, a
  site convention, a decision and its reason. Do not write one for something that
  belongs in a learning, and do not store secrets in either.

## Learnings

A learning is not free text. It matches a recurring tool error and carries the fix:

- `error_pattern` (required) — a substring or regex matched against the error text.
- `tool_name`, `error_category`, `service_type` — narrow where it applies.
- `fix_strategy` — `parameter_adjust` (a parameter delta in `fix_params`) or `escalate`
  (a hint message for a human).

The store tracks `confidence`, `success_count` and `failure_count`, and `list_learnings`
returns the most confident first. System-wide learnings (`is_system: true`) are read-only:
they cannot be updated or deleted, only shadowed by a more specific tenant one.

Curating them:

- Check `list_learnings` for an equivalent before creating a duplicate; update instead.
- Keep the pattern specific to the error text. A pattern that matches everything will
  fire on unrelated failures and its confidence will decay taking real fixes with it.
- If a learning keeps misfiring, deactivate it with `update_learning` (`is_active:
  false`) and tell the user which one and why.
- Never put a secret, token or customer datum in `fix_params`.

## Choosing between them

"When the NetBox sync 403s, the token needs the `Token` prefix" → a **learning**: it is
an error and a parameter fix.

"Our maintenance window is Tuesdays 02:00–04:00 and changes need a CAB ticket" → a
**knowledge article**: it is prose a human wrote for other humans, and no error pattern
matches it.
