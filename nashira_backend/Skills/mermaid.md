# Skill: authoring the `logic_diagram_mermaid` field

> Copied from FlowWeaver on 2026-08-30, when this product adopted the same rule. The two are
> deliberately identical: the validator's refusal message points readers here, and it is the
> SAME message in both products, so a reader who follows it must find the same conventions
> whichever one they came from. Keep them in step — `LogicDiagramTests
> .The_required_set_is_the_one_FlowWeaver_uses` guards the type list, and nothing guards this
> file.

Every `python_snippet` and `transform` snippet MUST carry a Mermaid
source string in `Snippet.LogicDiagramMermaid`. The UI renders it:

- In the snippet's edit dialog, drawn live beside the source — which is
  also the dialog the workflow confirmation card opens when a reader
  clicks Edit on a step, so it is how a node's logic gets read.
- In chat, wherever you put the source in a ```mermaid fence. Do that
  when you are proposing a snippet: the diagram is the part of the
  proposal a human can actually check before approving it.

The point is that a user opening the workflow six months later can
read the diagram and understand *what the task does* without reading a
line of Python. If the diagram can't stand alone, it's not serving its
purpose.

## Backend validation (what the 400 catches)

`SnippetCatalog.ValidateLogicDiagram` rejects a create/update with
`logic_diagram_invalid` when:

- The snippet type is `python_snippet`, `python`, `transform`, or
  `jmespath` AND the field is missing / empty.
- The field is non-empty but doesn't start with a Mermaid directive
  (`graph`, `flowchart`, `sequenceDiagram`, `stateDiagram`,
  `classDiagram`, `erDiagram`, `gantt`, `journey`, `gitGraph`, `pie`,
  `mindmap`, `timeline`, …).

The backend does NOT parse the body deeply — that's the renderer's
job. If the diagram is syntactically valid Mermaid but semantically
wrong (missing arrows, orphan nodes), it lands in the UI and the
reviewer catches it visually.

## Preferred shapes

### `flowchart TD` for most tasks

A top-down flowchart reads left-to-right in time and maps naturally
onto a Python function: input node at the top, decision diamonds in
the middle, output at the bottom.

```mermaid
flowchart TD
    in([Input: device list]) --> loop{For each device}
    loop -->|has primary IP| fetch[GET /record:host?name=X]
    loop -->|no primary IP| skip[Skip]
    fetch --> classify{Found?}
    classify -->|yes| insync[status=In Sync]
    classify -->|no| missing[status=Missing DNS]
    insync --> tag[PATCH device tag=dns-verified]
    missing --> tag2[PATCH device tag=missing-dns]
    tag --> row[Append row]
    tag2 --> row
    skip --> row
    row --> loop
    loop -->|done| out([Output: rows, counts])
```

Rules of thumb:

- **Input node as `([...])`** (stadium shape) so it's visually distinct.
- **Output node also `([...])`**.
- **Actions as `[...]`** (rectangles).
- **Decisions as `{...}`** (diamonds).
- **Labels on arrows** when there's branching: `-->|yes|`, `-->|no|`.
- 5-15 nodes is the sweet spot. Fewer → not earning its keep; more →
  the task is too big, split it.

### `sequenceDiagram` when the task coordinates multiple actors

Use a sequence diagram ONLY when the task genuinely has multiple
external actors doing a back-and-forth (e.g., a two-phase commit
across two APIs). Most `python_snippet` tasks are flowcharts.

```mermaid
sequenceDiagram
    participant Task
    participant NetBox
    participant Infoblox
    Task->>NetBox: GET /dcim/devices?status=active
    NetBox-->>Task: devices[]
    loop per device
        Task->>Infoblox: GET /record:host?name=<device>
        Infoblox-->>Task: records[]
        Task->>NetBox: PATCH /dcim/devices/<id> tags=[...]
        NetBox-->>Task: 200
    end
    Task->>Task: build CSV
```

## Level of detail

The diagram documents **the task**, not the whole workflow. Don't
redraw the DAG inside each snippet's diagram.

**Include**:
- The inputs the task consumes (keys of `get_input()` payload).
- The external calls the task makes (integration + endpoint).
- The decision branches the task takes.
- The output keys the task emits (what appears under `set_output(...)`).

**Exclude**:
- Which upstream node produced the inputs (that's in the DAG).
- Which downstream node consumes the outputs (same).
- Error handling that's identical across all tasks (timeouts, retries).

Keep it at the level a human reviewer needs to spot "wait, shouldn't
this also handle X?" — not every branch of every `try/except`.

## Examples for common task shapes

### `fetch-from-api`

```mermaid
flowchart TD
    in([netbox_integration_id, active_status]) --> get[GET /dcim/devices?status=active]
    get --> filter[Filter: has primary_ip4]
    filter --> out([rows: devices with IP])
```

### `reconcile-two-systems`

```mermaid
flowchart TD
    in([rows: devices, both integration_ids]) --> loop{For each device}
    loop -->|next| query[GET Infoblox /record:host?name=X]
    query --> decide{Host record exists?}
    decide -->|yes| insync[In Sync + tag dns-verified]
    decide -->|no| missing[Missing DNS + tag missing-dns]
    insync --> patch[PATCH NetBox device.tags]
    missing --> patch
    patch --> loop
    loop -->|done| out([rows with status + tag])
```

### `build-csv-attachment`

```mermaid
flowchart TD
    in([rows: list]) --> header[Write header row]
    header --> body[csv.writer rows]
    body --> encode[base64 encode UTF-8 bytes]
    encode --> out([filename, content_base64, row_count])
```

## When the code changes, the diagram changes

Whenever you send a new `code` — `create_snippet` here, or
`snippets_update` through `execute_operation` against the `na_snippets`
spec — include an updated `logic_diagram_mermaid` in the SAME payload.
(FlowWeaver spells the update `fw_snippets:update_snippet`; the rule is
the same in both.) A stale diagram is worse than no diagram — it
misleads reviewers. The backend doesn't enforce freshness (it can't),
so this is on the agent.

Note that `snippets_update` merges: omitting `logic_diagram_mermaid`
keeps the OLD one rather than clearing it, so a code change that leaves
the field out is exactly how a diagram goes stale without anything
failing.

If the only change is cosmetic (rename a variable, tweak a log line),
the diagram doesn't need to change — but the `updated_at` on the
snippet still reflects it.

## What NOT to do

- ❌ Paste Python code into the Mermaid field. It fails validation
  (first line is not a directive), and even if it passed, it would
  render as garbage.
- ❌ Copy the workflow DAG into the diagram. The workflow canvas
  already shows the DAG — the task-level diagram is *inside* one node.
- ❌ Emit a stub like `flowchart TD\n    A --> B` that doesn't
  describe anything. That's the scaffold anti-pattern with extra
  steps.
- ❌ Use ASCII-art or PlantUML or sequence diagrams written in prose.
  Only Mermaid (or nothing — for built-in types).
