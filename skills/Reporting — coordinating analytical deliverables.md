# Skill: reporting — coordinating analytical deliverables

Use this skill when a request combines analysis, narrative, tables, or more than one output format. Its job is to keep the answer and every exported artifact consistent. For a single, clearly tabular export or a single statistical question, follow the corresponding specialised skill directly.

## What Nashira can produce

There are two native export paths and they are not interchangeable:

| Need | Tool | Supported result |
|---|---|---|
| A report with headings, paragraphs, lists, pipe tables, quotes or code | `export_document` | Paginated A4 PDF, self-contained HTML, or Markdown |
| A flat set of row objects intended for reading or reuse | `export_table` | CSV, XLSX, or PDF |

Use `parse_file` to turn an attached CSV, XLSX, JSON or text file into structured rows before analysing it. Do not bend narrative content into artificial key/value rows: reports belong in `export_document`. Do not place a large editable dataset inside a document: rows belong in `export_table`.

Both export tools create an artifact and return a `download_url`. Always give that returned link to the user. Do not claim that a file exists until the tool succeeds.

## Establish one source of truth

Before writing any output, determine:

- the audience and the decision the report should support;
- the reporting period, cut-off date and unit of analysis;
- the source files, tables or tool results actually used;
- the filters, exclusions and treatment of missing values;
- the exact definition, formula, unit and denominator of every KPI.

Calculate each result once and reuse it. When both a document and a table are requested, the narrative, headline values and exported rows must share the same definitions and cut-off. Reconcile totals and key metrics before exporting; explain any intentional difference.

## Ordered workflow

1. Inspect the available sources. If a file is involved, call `parse_file` rather than guessing its structure from the name.
2. Resolve only ambiguities that materially change the analysis or requested artifact.
3. Profile coverage, duplicates, missing values and impossible values before calculating KPIs.
4. Separate observed facts, interpretation and recommendations. Do not present an association as a cause.
5. Build the smallest useful deliverable. A report needs a purpose, findings, evidence and limitations; a table needs stable columns and real data types.
6. Export with the native tool that matches the content and the user's requested format.
7. Check that dates, filters, units, denominators and totals agree across the chat answer and every artifact, then return each `download_url`.

## Quality and safety boundaries

- Never invent rows, definitions, source coverage or causal explanations.
- Preserve calculation precision and round only for presentation.
- Distinguish a true zero from a missing or unavailable value.
- Minimise sensitive detail; prefer aggregates when row-level data is unnecessary.
- Do not send or publish an artifact outside the authorised environment unless the user explicitly requests an available governed action.
- Do not substitute a different format and apologise. PDF, HTML, Markdown, CSV and XLSX all have a native route; choose the right one or explain a genuine structural limitation.

