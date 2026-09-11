# Skill: Excel reports — exporting an auditable table

Use this skill when the user wants data in an editable Excel file. Nashira's native Excel path is `export_table` with the XLSX format: it turns an array of **flat row objects** into a downloadable workbook artifact.

## What the native XLSX export is — and is not

It is appropriate for a clean rectangular dataset with stable columns and values that can be opened, filtered and reused in Excel. It is not a promise of a bespoke multi-sheet model with formulas, pivot tables, macros, charts, protected input areas or recalculation logic.

Do not describe a native `export_table` result as containing those features unless the tool contract and returned artifact explicitly support them. If the user requires a complex workbook, state that the native route can deliver the underlying table and clarify the additional workbook design that remains outside this export.

`export_table` also supports CSV for maximum interoperability and PDF for a table intended to be read rather than edited. Use `export_document`, not `export_table`, when the content is mainly prose with headings and conclusions.

## Prepare the rows

When the source is an attached CSV, XLSX, JSON or text file, call `parse_file` first. Inspect the parsed columns and preserve meaningful data types:

- numbers and percentages remain numeric values rather than decorated strings;
- dates use a consistent unambiguous representation;
- identifiers remain text when leading zeroes are significant;
- missing values remain missing and are not silently converted to zero;
- each row has the same logical fields, with clear and stable column names.

Before export, check row count, key uniqueness where applicable, duplicates, totals and any exclusions. Do not silently overwrite source values. If calculated columns are included, explain their definitions and compute them consistently before constructing the row objects.

## Export workflow

1. Confirm the requested granularity: one row per device, event, site, period or other unit.
2. Define the columns and their meaning before assembling the rows.
3. Reconcile record counts and critical totals with the source.
4. Call `export_table` with the flat rows and XLSX format.
5. Confirm that the tool succeeded and return its `download_url`.

For multiple unrelated tables, prefer separate clearly named artifacts rather than pretending that one native call created a designed multi-sheet workbook. Include the source, cut-off date, filters and KPI definitions in the chat response or in a companion report when they are necessary to interpret the table.

## Failure modes to avoid

- Do not put nested objects or arrays into cells and call the result a clean workbook; flatten intentionally or export separate tables.
- Do not export display-formatted numbers that Excel will treat as text.
- Do not promise formulas or charts that were never generated.
- Do not invent missing rows or values to make totals close.
- Do not claim delivery before a valid artifact and `download_url` exist.

