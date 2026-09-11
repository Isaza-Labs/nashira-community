// What a run form should ask for.
//
// A workflow *may* declare an `input_schema`, but nothing in the product forces
// one — the agent authors most workflows and there is no UI to write a schema by
// hand. So the schema is derived from the DAG itself when none was declared: every
// `{{ input.X }}` the nodes reference is a value the run cannot supply on its own,
// which is exactly the set a human has to be asked for.

import type { Workflow } from '$lib/api/workflows.api';

// Mirrors the engine's own matcher (backend VariableResolver.cs): `{{ input<path> }}`.
// The `\b` after `input` is what stops `{{ inputs }}` and `{{ input_date }}` from
// matching — the same guard the resolver uses, for the same reason. Group 1 is the
// path: ".email", ".devices[0].id", "['pass']", "" (the whole object) or " | filter".
const INPUT_REF = /\{\{\s*input\b([^}]*?)\s*\}\}/g;

/**
 * Reduce a captured path to its top-level key, plus whether it was indexed (a hint
 * that the value is an array). Returns null for `{{ input }}`, `{{ input | to_json }}`
 * and `{{ input[0] }}` — none of those name a single field, so none can become one.
 */
function topLevelInputKey(rawPath: string): { key: string; isArray: boolean } | null {
	let p = rawPath.trim();
	if (p.startsWith('.')) p = p.slice(1);
	p = p.trimStart();
	if (!p || p.startsWith('|')) return null;
	if (p.startsWith('[')) {
		const bracket = p.match(/^\[\s*(['"])(.*?)\1\s*\](\s*\[)?/);
		return bracket ? { key: bracket[2], isArray: !!bracket[3] } : null;
	}
	// The key ends at the first `.`, `[`, whitespace or `|` (filter pipe).
	const dotted = p.match(/^([^.[\s|]+)(\[)?/);
	return dotted ? { key: dotted[1], isArray: !!dotted[2] } : null;
}

/**
 * Walk any config value — string, array, or object, at any depth — recording every
 * top-level `{{ input.X }}` key it references. A node's config is arbitrarily nested
 * (a REST body inside a headers map inside a list), so a shallow scan would miss the
 * references that matter most. `acc` maps key → isArray.
 */
function collect(value: unknown, acc: Map<string, boolean>): void {
	if (typeof value === 'string') {
		INPUT_REF.lastIndex = 0;
		let m: RegExpExecArray | null;
		while ((m = INPUT_REF.exec(value)) !== null) {
			const hit = topLevelInputKey(m[1]);
			if (hit) acc.set(hit.key, (acc.get(hit.key) ?? false) || hit.isArray);
		}
	} else if (Array.isArray(value)) {
		for (const item of value) collect(item, acc);
	} else if (value && typeof value === 'object') {
		for (const v of Object.values(value)) collect(v, acc);
	}
}

/**
 * Synthesize a minimal JSON Schema — one property per `{{ input.X }}` the workflow's
 * nodes actually reference — so a run form can render a labelled field per input.
 * A template reference carries no type, so each field is a string unless it was
 * indexed (`input.foo[0]` → array). Returns `{}` when nothing is referenced.
 */
export function deriveInputSchemaFromWorkflow(
	workflow: Pick<Workflow, 'nodes'> | null | undefined
): Record<string, unknown> {
	const found = new Map<string, boolean>();
	for (const node of workflow?.nodes ?? []) {
		collect(node?.configOverrides ?? {}, found);
	}
	if (found.size === 0) return {};

	const properties: Record<string, { type: string; title: string }> = {};
	const order: string[] = [];
	for (const [key, isArray] of found) {
		properties[key] = { type: isArray ? 'array' : 'string', title: key };
		order.push(key);
	}
	// `x-order` because jsonb does not preserve key order and JsonSchemaForm reads it.
	return { type: 'object', properties, 'x-order': order };
}

/**
 * The schema that should drive an input form for a workflow: the declared
 * `input_schema` when it has properties, otherwise one derived from the DAG.
 * An authored schema always wins — it carries types, descriptions and defaults
 * that no derivation can recover.
 */
export function effectiveInputSchema(
	workflow: Workflow | null | undefined
): Record<string, unknown> {
	const declared = workflow?.inputSchema;
	const props =
		declared && typeof declared === 'object'
			? ((declared as Record<string, unknown>).properties as Record<string, unknown> | undefined)
			: undefined;
	if (props && Object.keys(props).length > 0) return declared as Record<string, unknown>;
	return deriveInputSchemaFromWorkflow(workflow);
}

/** True when the schema has at least one field to render. */
export function hasInputFields(schema: Record<string, unknown> | null | undefined): boolean {
	const props = schema?.properties as Record<string, unknown> | undefined;
	return !!props && Object.keys(props).length > 0;
}
