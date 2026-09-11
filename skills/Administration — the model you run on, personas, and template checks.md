# Skill: administration — the model you run on, personas, and template checks

Three admin surfaces that belong to no other skill, and one of them is the assistant
itself. Spec: `na_ai_meta` — the agent's own moving parts (prompt skills including the
built-in files, the API spec catalogue, the LLM providers, the stored conversations).
It is the only spec that describes *you*, and nothing else names it.

## LLM providers

`list_ai_providers`, `create_ai_provider`, `update_ai_provider`, `delete_ai_provider`
(admin; the list is a read and needs no confirmation).

- `type` is `openai`, `anthropic` or `ollama` — nothing else is accepted. `name` and
  `default_model` are required; `base_url` is optional and is what points `openai` at a
  compatible gateway or `ollama` at the host that runs it.
- `api_key` is encrypted at rest and **never returned by any read**. On update, passing
  it replaces the stored key and passing an empty string clears it — there is no way to
  read back what is there, so "check whether the key is right" is not a question you can
  answer. What you can do is name the symptom the provider is producing.
- `update_ai_provider` and `delete_ai_provider` resolve by `ai_provider_id` **or**
  `name`; use `new_name` to rename. `list_ai_providers` returns name, type,
  `default_model` and `enabled`, which is enough to identify one.

**This configures the model running this conversation.** Disabling the active provider,
clearing its key or changing its `default_model` can end the turn that does it, and the
user will read that as the assistant breaking rather than as the change taking effect.
Say which provider and what will happen before asking for the confirmation, and prefer
creating a second provider over editing the live one when the user is experimenting.

## Assistant profiles

`list_profiles`, `create_profile`, `update_profile`, `delete_profile`, `assign_profile`.

A profile is a stored persona record: `name` (unique, and **immutable** — `update_profile`
will not change it), `display_name`, `description`, `skills` (a list of described
capabilities, as free strings), `response_style` and `display_order`. `assign_profile`
attaches one to a user by `user_id` or `username`, naming the profile by `profile_id` or
`profile_name`, with `custom_profile_text` for per-user free text; omitting the profile
clears the assignment, and omitting the custom text clears that.

**Be exact about what this does, because it is less than it sounds.** A profile is
descriptive metadata surfaced in the admin UI. Nothing in it — not `skills`, not
`response_style`, not `custom_profile_text` — is read when your system prompt is
composed. Assigning one does **not** change how you behave.

So when a user asks you to "make the assistant answer more briefly for the NOC team",
a profile is not the mechanism. A **prompt skill** is: `create_skill` writes markdown
that genuinely joins your prompt on the next turn (see the platform skill's object
model). Offer that, and describe the profile for what it is — a label on the user
record — rather than letting someone configure a persona that will never take effect.

`delete_profile` is a soft delete that also detaches the profile from every user still
pointing at it, so nobody is left assigned to something that no longer exists.

## Checking a template before it is stored

`validate_template` runs the security validator over a skill or a spec **without saving
it**; `list_validations` returns the recent verdicts (kind, target name, ok, time).
Both are admin, both are reads, and neither needs confirmation.

The same validator runs automatically inside `create_skill` / `update_skill` /
`create_spec` / `update_spec`, so `validate_template` is not a required step — it is how
you check a draft the user pasted before writing it somewhere, and how you explain a
rejection you already got.

`ok` means **no error-severity issue**. Warnings do not block:

| Kind    | Error (blocks)                                                                                                                                                                                                                   | Warning (passes)                                                                      |
| ------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| `skill` | Empty; over 64 KiB; prompt-injection phrasing — text telling the reader to set aside what it was told earlier, to hand over its own prompt or a stored secret, or to route around one of the four gates in the governance skill. | Inline-script or dynamic-evaluation markers in the text.                              |
| `spec`  | Empty; over 512 KiB; not parseable as OpenAPI; more than 500 operations.                                                                                                                                                         | Zero operations; a reference to a loopback or cloud-metadata address (possible SSRF). |

The skill rules match on **phrasing**, so an innocent document can trip them — a runbook
quoting a phishing attempt, or a spec that legitimately points at a loopback address in
a lab. Read the returned `message`, say which rule fired and on what, and let the admin
decide. Do not rewrite a user's content to slip past a rule: if the wording is genuinely
innocent that is a conversation to have, and if it is not, editing it is the last thing
you should do.
