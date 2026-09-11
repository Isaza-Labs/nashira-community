# Skill: exports and reports — handing results out

Use an export for a downloadable file and a report for a persisted document. Pick by
what the user actually wants, not by what is closest to hand.

Tools: `export_table`, `export_document`, `list_reports`, `read_report`, `save_report`,
`bulk_delete_reports`, `parse_file`. Report operations live in `na_notifications`;
exports are listed in `na_admin_readonly`.

**A file is always possible.** Between these two tools every format the product offers is
reachable — pdf, xlsx, csv, html, markdown. Never answer that a format is unavailable and
substitute another one: pick the tool that produces it and call it.

## `export_table` — rows the user can keep

Takes an array of **flat** objects and returns a download link plus an
`export_artifact_id`. Formats: `csv` (default), `xlsx`, `pdf`, `html` (self-contained,
safe to attach) and `markdown`.

| Format | Use when |
|---|---|
| `csv` | It is going into another tool. Smallest, most portable. |
| `xlsx` | More than ~50 rows, or the user says "filter" / "spreadsheet". |
| `pdf` | A table meant to be read or circulated as-is, not edited. |
| `html` | A rich view they will open in a browser or receive by mail. |
| `markdown` | It is going into a document, ticket or PR. |

Export when the user asks to **export**, **download**, or wants a file to forward. Do not
export just because you happen to have tabular data — for "show me", a markdown table in
the reply is better than a file they have to open.

Nested objects do not survive: flatten before calling, or the columns come out unusable.

## `export_document` — a report, not a table

Takes markdown in `content` and renders it as a real document: `pdf` (default, paginated
A4), `html` or `markdown`. Supported markdown: `#`/`##`/`###` headings, paragraphs, `-`
and `1.` lists, `|` pipe tables, ``` fenced code, `>` quotes, `**bold**`, `` `code` ``
and http(s) links. Pass `title` for the heading on page one.

This is the tool for anything the user calls a **report**, a **summary**, a **document**
or "the answer as a file" — an incident write-up, the state of a workflow, a runbook, a
handover note. Prose with a table inside it belongs here, not in `export_table`: a report
squeezed through the rows-only tool comes out as a one-column table of field names.

Write the report the way you would write the answer — the sections, the numbers and the
context that make it useful on its own, since whoever opens the file will not have the
conversation in front of them. A `pdf` is what to produce unless they ask otherwise.

## Handing the file over

Both export tools return the same thing, and both are handed over the same way: a markdown
link whose text is the `file_name` and whose target is the `download_url` exactly as
returned — `[interfaces.csv](/api/export/…/download)`. The UI turns that link into a real
download button; a path you retype, shorten or invent does not become one, and the user is
left with a file they cannot reach. The same holds for a report's `download_url`
(`/api/reports/…/download`), which the tools below return — hand it over as a link too.

## `parse_file` — data coming in

`csv` / `xlsx` return columns plus row objects; `pdf` returns text per page (pass `page`
to fetch one page of a long document); `docx` returns the body text plus every table as
a columns-and-rows grid; `json` returns the parsed value; `text` returns lines. Legacy
binary `.doc` (Word 97–2003) is not supported — ask for `.docx` or a PDF. Parse before
reasoning about a file's contents — do not eyeball a pasted CSV and hope.

**A file the user attached to this conversation is already yours to read.** Pass its
filename as `attachment` — `{"format": "xlsx", "attachment": "matrix.xlsx"}`. This works
for the current message's files and for files attached to earlier messages in the same
conversation: they are kept for the conversation's lifetime, so never ask the user to
re-attach, paste base64 or re-type a file they already sent. If a name misses, the error
lists what IS attached — only ask for a re-attach when that list truly lacks the file
(very old threads can evict past a per-conversation cap). Inline `content` (text) or
`content_base64` remain for data produced inside the conversation.

## Reports — the documents that stay

A report is a persisted, attributable document with a title, an optional
`workflow_run_id` and an optional expiry; it is visible in `/reports`. An export —
including one made with `export_document` — is a file handed to whoever asked and
forgotten. Three tools cover the difference:

- **`list_reports`** — the catalogue. `source` is `reports` (default), `exports`, or
  `all`; filter by `workflow_run_id`, by `search` over the title and file name, and
  `include_expired` to see past retention. Each item carries `readable`.
- **`read_report`** — the text, back in the conversation. It takes a
  `report_artifact_id` **or** the `export_artifact_id` that `export_document` /
  `export_table` returned, so a document generated earlier in the conversation can be
  quoted, summarised or extended instead of described from memory. Long documents page
  with `offset` / `next_offset` — read the next page before summarising, never treat a
  truncated body as the whole document.
- **`save_report`** — persist one. Markdown body, optional `retain_days`, optional
  `workflow_run_id` (it must be a run you actually read, not one you inferred).
- **`bulk_delete_reports`** — remove many saved reports in one confirmed call (one
  mutation-budget slot). Select by `report_ids` and/or filters (`search`,
  `workflow_run_id`, `expired_only`, `older_than_days`); filters restrict the id list.
  Exports are never touched. Preview with `list_reports`, show the user the titles and
  the count, pass the count as `expected_count`.

**A pdf cannot be read back.** Only text formats survive the round trip — markdown,
html, csv, json, plain text. A pdf or an xlsx comes back `readable: false` with its
download link, because the stored bytes are a rendering, not the prose. So when a
document will be needed again — the agent's own notes, an incident write-up, anything a
later turn or a later conversation has to quote — `save_report` the markdown, and hand
over the pdf separately with `export_document` if the user wants one.

Do not tell the user a report is unreachable before calling `list_reports`: a document
this platform produced is one call away, whichever tool made it.

`/api/reports` remains available through `na_notifications` (Viewer to read, Operator to
create) for anything the tools do not cover, such as deleting one.


## What never leaves

Secrets, tokens, credential bodies and private keys do not go into an export or report —
regardless of format and regardless of who asks. Reads return safe presence flags precisely
so sensitive material never has to enter an artifact.
