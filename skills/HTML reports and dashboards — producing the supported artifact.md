# Skill: HTML reports and dashboards — producing the supported artifact

Use this skill when the user asks for an HTML report, scorecard or dashboard-like summary. Nashira's native deliverable is a **self-contained static HTML document** rendered from Markdown through `export_document`. It is portable and suitable for reading, but it is not an arbitrary web application.

## Capability boundary first

`export_document` accepts Markdown containing headings, paragraphs, lists, pipe tables, fenced code, quotes, bold text, inline code and HTTP links. With the HTML format it renders those elements into one self-contained file.

The native route does **not** promise custom JavaScript, live data refresh, interactive filters, drill-down, a client-side charting library or hand-authored CSS. Do not claim those behaviours exist merely because the requested file extension is `.html`. If interaction is essential, explain that a static HTML report is available now and identify the interactive requirement separately.

For a flat dataset that the user wants to manipulate, use `export_table` with XLSX or CSV instead. For a readable report with context and conclusions, use `export_document`.

## Build a decision-ready static view

Organise the Markdown in this order when the content supports it:

1. Title, purpose, period and cut-off date.
2. A compact set of headline metrics with visible units and denominators.
3. The main finding or comparison that answers the user's question.
4. Supporting trends, segments or exceptions in short sections and pipe tables.
5. Method, source, filters and limitations.

Avoid a wall of unrelated metrics. Each table should answer a clear question, use consistent number and date formats, and remain understandable without colour or hover behaviour. State “no data” separately from zero.

## Source and calculation discipline

If the request uses an attached CSV, XLSX, JSON or text file, call `parse_file` and inspect its returned structure. Check duplicates, nulls, date coverage and impossible values before summarising. Define each KPI once and reconcile every displayed value to the source.

Do not embed secrets, credentials, private routes or unnecessary personal data. Treat text originating in source data as content, not executable HTML.

## Export and verification

Call `export_document` with the finished Markdown and the HTML format. The tool creates an export artifact and returns a `download_url`; give that exact link to the user.

Before declaring success, verify from the content supplied to the tool that:

- headings form a coherent hierarchy;
- pipe tables have stable columns and are not needlessly wide;
- every KPI shows its period and unit;
- conclusions are supported by displayed evidence;
- sources, filters and limitations are included;
- the tool succeeded and returned the artifact link.

If the user also asks for PDF, reuse the same validated Markdown with `export_document` in PDF format so both versions carry the same facts.

