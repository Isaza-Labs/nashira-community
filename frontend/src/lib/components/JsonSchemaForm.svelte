<script lang="ts">
	// JSON-schema driven form. Svelte 5 runes with $bindable.
	//
	// Renders one labelled control per declared property so an integration's
	// "External config" is a form ("Microsoft App ID", with help text) instead of
	// a JSON blob the operator has to know the key names for.
	//
	// Defaults are shown as placeholders, never written into `value`: a default
	// the backend already applies should not be persisted as if the user had
	// typed it, and writing it back would fight the user's own keystrokes.
	//
	// Blank is absence. Clearing a field deletes the key rather than storing "",
	// so an untouched optional key never reaches the API as an empty string.
	import { Checkbox, FieldHint, Input, Select, Textarea } from '$lib/components/ui';
	import JsonSchemaForm from './JsonSchemaForm.svelte';

	interface Schema {
		type?: string;
		title?: string;
		description?: string;
		properties?: Record<string, Schema>;
		required?: string[];
		enum?: unknown[];
		default?: unknown;
		placeholder?: string;
		minimum?: number;
		maximum?: number;
		format?: string;
		maxLength?: number;
		'x-order'?: string[];
	}

	interface Props {
		schema?: Schema;
		value?: Record<string, unknown>;
		readonly?: boolean;
		depth?: number;
	}

	let {
		schema = {},
		value = $bindable<Record<string, unknown>>({}),
		readonly = false,
		depth = 0
	}: Props = $props();

	const properties = $derived((schema.properties || {}) as Record<string, Schema>);
	const requiredFields = $derived(new Set(schema.required ?? []));
	// Render in the schema's optional `x-order` when present. Postgres jsonb does
	// NOT preserve object key order (it normalises keys by length then bytewise),
	// so a stored schema can't rely on property declaration order — an `x-order`
	// ARRAY survives jsonb intact. Listed keys lead in that order; any not listed
	// follow in their natural order.
	const propertyKeys = $derived(orderKeys(Object.keys(properties), schema['x-order']));

	function orderKeys(keys: string[], order?: string[]): string[] {
		if (!order || order.length === 0) return keys;
		const inOrder = order.filter((k) => keys.includes(k));
		const rest = keys.filter((k) => !inOrder.includes(k));
		return [...inOrder, ...rest];
	}

	function getFieldType(prop: Schema): string {
		if (prop.enum) return 'enum';
		if (prop.type === 'boolean') return 'boolean';
		if (prop.type === 'integer' || prop.type === 'number') return 'number';
		if (prop.type === 'object' && prop.properties) return 'object';
		if (prop.type === 'array') return 'array';
		return 'string';
	}

	// The one write path. `undefined`, "" and NaN mean "not set" — the key goes
	// away instead of persisting a blank. `false` and `0` are values, and stay.
	function updateField(key: string, val: unknown) {
		const next = { ...value };
		if (val === undefined || val === '' || (typeof val === 'number' && Number.isNaN(val))) {
			delete next[key];
		} else {
			next[key] = val;
		}
		value = next;
	}

	function placeholderFor(prop: Schema): string {
		if (prop.placeholder) return prop.placeholder;
		return prop.default === undefined || prop.default === null ? '' : String(prop.default);
	}

	// Shown in the ⓘ only when there is something to say beyond what the label,
	// the type badge and the required marker already show.
	function constraintDetail(prop: Schema, isRequired: boolean): string {
		const bits: string[] = [];
		if (prop.enum?.length) bits.push(`Allowed: ${prop.enum.map(String).join(', ')}.`);
		if (prop.minimum !== undefined) bits.push(`Minimum ${prop.minimum}.`);
		if (prop.maximum !== undefined) bits.push(`Maximum ${prop.maximum}.`);
		if (prop.maxLength !== undefined) bits.push(`Up to ${prop.maxLength} characters.`);
		if (prop.format && prop.format !== 'textarea') bits.push(`Format: ${prop.format}.`);
		if (prop.default !== undefined) bits.push(`Default: ${JSON.stringify(prop.default)}.`);
		if (bits.length === 0) return '';
		return `${isRequired ? 'Required.' : 'Optional.'} ${bits.join(' ')}`;
	}

	function enumOptions(prop: Schema): { value: string; label: string }[] {
		return (prop.enum ?? []).map((o) => ({ value: String(o), label: String(o) }));
	}

	// ─── Bindable wrappers ────────────────────────────────────────────────
	// `bind:` needs a writable expression and `value[key]` isn't one, so each
	// control binds to a getter/setter pair that reads and writes through
	// updateField.

	function textBinding(key: string) {
		return {
			get current(): string {
				const v = value[key];
				return v === undefined || v === null ? '' : String(v);
			},
			set current(v: string) {
				updateField(key, v);
			}
		};
	}

	// Numbers keep a text draft: "1." and "-" are legal things to be typing and
	// would be erased under the caret if every keystroke round-tripped through
	// Number().
	let numberDrafts = $state<Record<string, string>>({});

	function numberBinding(key: string) {
		return {
			get current(): string {
				if (key in numberDrafts) return numberDrafts[key];
				const v = value[key];
				return v === undefined || v === null ? '' : String(v);
			},
			set current(raw: string) {
				numberDrafts = { ...numberDrafts, [key]: raw };
				const trimmed = raw.trim();
				if (!trimmed) {
					updateField(key, undefined);
					return;
				}
				const n = Number(trimmed);
				if (!Number.isNaN(n)) updateField(key, n);
			}
		};
	}

	function boolBinding(key: string, prop: Schema) {
		return {
			get current(): boolean {
				const v = value[key];
				if (typeof v === 'boolean') return v;
				return prop.default === true;
			},
			set current(v: boolean) {
				updateField(key, v);
			}
		};
	}

	function enumBinding(key: string, prop: Schema) {
		return {
			get current(): string {
				const v = value[key] !== undefined ? value[key] : prop.default;
				return v === undefined || v === null ? '' : String(v);
			},
			set current(raw: string) {
				// Map back to the declared literal so a numeric enum stays numeric.
				const match = (prop.enum ?? []).find((o) => String(o) === raw);
				updateField(key, match !== undefined ? match : raw);
			}
		};
	}

	function objectBinding(key: string) {
		return {
			get current(): Record<string, unknown> {
				const v = value[key];
				return v && typeof v === 'object' && !Array.isArray(v)
					? (v as Record<string, unknown>)
					: {};
			},
			set current(v: Record<string, unknown>) {
				value = { ...value, [key]: v };
			}
		};
	}

	// ─── Raw-JSON escape hatches ──────────────────────────────────────────
	// Arrays, property-less objects and a schema with no properties at all are
	// edited as text. The draft buffer is what keeps a half-typed (invalid) JSON
	// on screen instead of snapping back to the last good value.
	let jsonDrafts = $state<Record<string, string>>({});
	let jsonErrors = $state<Record<string, string>>({});

	function jsonDisplay(slot: string, current: unknown, fallback: unknown): string {
		if (slot in jsonDrafts) return jsonDrafts[slot];
		return JSON.stringify(current ?? fallback, null, 2);
	}

	function onJsonInput(slot: string, raw: string, commit: (parsed: unknown) => void) {
		jsonDrafts = { ...jsonDrafts, [slot]: raw };
		if (!raw.trim()) {
			jsonErrors = { ...jsonErrors, [slot]: '' };
			commit(undefined);
			return;
		}
		try {
			const parsed = JSON.parse(raw);
			jsonErrors = { ...jsonErrors, [slot]: '' };
			commit(parsed);
		} catch (e) {
			// Invalid while typing — keep the last good value, no commit.
			jsonErrors = { ...jsonErrors, [slot]: (e as Error).message };
		}
	}

	const jsonClass =
		'ui-control w-full resize-y px-3 py-2 font-mono text-xs outline-none disabled:opacity-60';
</script>

{#snippet typeBadge(t: string)}
	<span class="rounded bg-surface-200-800 px-1.5 py-0.5 font-mono text-[10px] text-surface-600-400">
		{t}
	</span>
{/snippet}

{#if propertyKeys.length === 0}
	<!-- The schema declares no properties (shapes that vary per action carry
	     their keys in the value, not in the schema). Editing the whole object as
	     raw JSON beats telling the user there is nothing to configure. -->
	<textarea
		value={jsonDisplay('__root__', value, {})}
		oninput={(e) =>
			onJsonInput('__root__', e.currentTarget.value, (parsed) => {
				if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
					value = parsed as Record<string, unknown>;
				} else if (parsed === undefined) {
					value = {};
				}
			})}
		disabled={readonly}
		spellcheck="false"
		rows="10"
		placeholder={'{}'}
		class={jsonClass}
	></textarea>
	{#if jsonErrors['__root__']}
		<p class="mt-1 font-mono text-xs text-error-600-400">{jsonErrors['__root__']}</p>
	{:else}
		<p class="mt-1 text-xs text-surface-600-400">
			This schema declares no fields — editing raw JSON.
		</p>
	{/if}
{:else}
	<div
		class="space-y-3"
		class:pl-3={depth > 0}
		class:border-l={depth > 0}
		class:border-surface-200-800={depth > 0}
	>
		{#each propertyKeys as key (key)}
			{@const prop = properties[key]}
			{@const fieldType = getFieldType(prop)}
			{@const isRequired = requiredFields.has(key)}
			{@const fieldId = `jsf-${depth}-${key}`}
			{@const title = prop.title || key}
			{@const detail = constraintDetail(prop, isRequired)}

			<div class="space-y-1">
				{#if fieldType === 'boolean'}
					{@const b = boolBinding(key, prop)}
					<div class="flex items-center gap-1.5">
						<Checkbox id={fieldId} bind:checked={b.current} label={title} disabled={readonly} />
						{#if isRequired}<span class="text-error-500">*</span>{/if}
						{@render typeBadge(fieldType)}
						{#if detail}
							<FieldHint label={title} help={prop.description ?? ''} {detail} />
						{/if}
					</div>
					{#if prop.description}
						<p class="text-xs text-surface-600-400">{prop.description}</p>
					{/if}
				{:else}
					<div class="flex items-center gap-1.5">
						<label for={fieldId} class="text-sm font-medium">
							{title}{#if isRequired}<span class="text-error-500"> *</span>{/if}
						</label>
						{@render typeBadge(fieldType)}
						{#if detail}
							<FieldHint label={title} help={prop.description ?? ''} {detail} />
						{/if}
					</div>

					{#if prop.description}
						<p class="text-xs text-surface-600-400">{prop.description}</p>
					{/if}

					{#if fieldType === 'string'}
						{@const b = textBinding(key)}
						{#if prop.format === 'textarea' || (prop.maxLength ?? 0) > 200}
							<Textarea
								id={fieldId}
								bind:value={b.current}
								rows={3}
								placeholder={placeholderFor(prop)}
								disabled={readonly}
							/>
						{:else}
							<Input
								id={fieldId}
								bind:value={b.current}
								placeholder={placeholderFor(prop)}
								disabled={readonly}
							/>
						{/if}
					{:else if fieldType === 'number'}
						{@const b = numberBinding(key)}
						<Input
							id={fieldId}
							type="number"
							bind:value={b.current}
							placeholder={placeholderFor(prop)}
							disabled={readonly}
							min={prop.minimum}
							max={prop.maximum}
							step={prop.type === 'integer' ? 1 : 'any'}
						/>
					{:else if fieldType === 'enum'}
						{@const b = enumBinding(key, prop)}
						<Select
							id={fieldId}
							bind:value={b.current}
							options={enumOptions(prop)}
							placeholder="Select…"
							disabled={readonly}
						/>
					{:else if fieldType === 'object'}
						{@const objProps = prop.properties ?? {}}
						{#if Object.keys(objProps).length > 0}
							{@const ob = objectBinding(key)}
							<div class="mt-1">
								<JsonSchemaForm
									schema={prop}
									bind:value={ob.current}
									{readonly}
									depth={depth + 1}
								/>
							</div>
						{:else}
							<!-- Object with no declared properties: raw JSON, same as an array. -->
							<textarea
								id={fieldId}
								value={jsonDisplay(key, value[key], {})}
								oninput={(e) =>
									onJsonInput(key, e.currentTarget.value, (parsed) => updateField(key, parsed))}
								disabled={readonly}
								spellcheck="false"
								rows="6"
								placeholder={'{}'}
								class={jsonClass}
							></textarea>
							{#if jsonErrors[key]}
								<p class="font-mono text-xs text-error-600-400">{jsonErrors[key]}</p>
							{/if}
						{/if}
					{:else if fieldType === 'array'}
						<textarea
							id={fieldId}
							value={jsonDisplay(key, value[key], [])}
							oninput={(e) =>
								onJsonInput(key, e.currentTarget.value, (parsed) => updateField(key, parsed))}
							disabled={readonly}
							spellcheck="false"
							rows="3"
							placeholder="[]"
							class={jsonClass}
						></textarea>
						{#if jsonErrors[key]}
							<p class="font-mono text-xs text-error-600-400">{jsonErrors[key]}</p>
						{/if}
					{:else}
						{@const b = textBinding(key)}
						<Input id={fieldId} bind:value={b.current} disabled={readonly} />
					{/if}
				{/if}
			</div>
		{/each}
	</div>
{/if}
