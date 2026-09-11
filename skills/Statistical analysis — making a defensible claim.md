# Skill: statistical analysis — making a defensible claim

Use this skill for descriptive analysis, comparisons, relationships, segmentation, time series or predictive questions. The goal is a defensible answer with visible assumptions and uncertainty, not the most sophisticated method available.

## Start from the question and the data actually available

Translate the request into the population, unit of analysis, outcome, predictors or groups, period and decision criterion. When data arrives as CSV, XLSX, JSON or text, use `parse_file` to obtain structured rows. Profile schema, coverage, keys, duplicates, missingness, impossible values, distributions and potential leakage before calculating a result.

Keep missing values distinct from zero. Record material cleaning, exclusions, imputations and transformations. Investigate extreme values as possible errors or real events; do not remove them merely for being extreme.

## Choose the smallest method that answers the question

| Question | Minimum useful analysis |
|---|---|
| What is happening? | Counts, coverage, distribution, quantiles and missingness; not only an average |
| How do groups differ? | Absolute and relative difference, group sizes and uncertainty compatible with the design |
| Are variables related? | Shape and outliers first, then a suitable association or model with diagnostics |
| What changes over time? | Ordered trend, seasonality, level changes and gaps; never a random split that leaks the future |
| Can we predict? | A simple baseline, out-of-sample validation and a metric aligned with the cost of error |

Check the assumptions required by the chosen method: independence, functional form, variance, error distribution, sampling structure and time ordering as applicable. If assumptions fail, simplify the claim or use a robust alternative; say what changed and why.

## Nashira execution boundary

There is no general chat tool that silently turns a statistical request into arbitrary Python execution. The Python module allowlist governs `python_snippet` workflow steps; it does not make packages directly callable from the conversation. Never claim that a package, test or model ran unless there is an actual successful execution result.

For a recurring or computationally substantial analysis, first inspect reusable snippets with `list_snippets`. Prefer a suitable entry marked `proven`, which means a step using it has completed on this instance. If no suitable snippet exists, `create_snippet` can define a governed `python_snippet`, but it still must be bound to and run through a real workflow before its output is evidence. Creating code is not executing it. Network access and third-party imports remain subject to the snippet and Python-module policies.

If the available rows and tools do not support a reliable computation, explain the limitation and specify the data or governed execution needed instead of manufacturing precision.

## Communicate the result

Separate:

1. observed descriptive facts;
2. inference conditional on assumptions;
3. practical recommendations.

Report magnitude and direction, sample size, uncertainty and practical relevance. A p-value alone is not a conclusion; include an effect and interval when appropriate, and declare or adjust for multiple comparisons. Do not present association as causation without a defensible causal design. Label post-hoc exploration as exploratory.

Use `export_document` for a narrative PDF, self-contained HTML or Markdown report, and `export_table` for result rows in CSV, XLSX or PDF. Reconcile every exported number to the analysis result and return the tool's `download_url` only after a successful export.

