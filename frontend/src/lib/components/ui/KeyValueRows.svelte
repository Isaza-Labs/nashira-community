<script lang="ts">
	// A JSON object of strings, edited as rows.
	//
	// The stored format is still JSON — it is what the API takes — but nobody should have
	// to type braces and quotes to add one header, and a stray comma should not be the
	// difference between a saved integration and a 400. The JSON is produced here, so a
	// malformed one is not expressible.
	import { Plus, X } from 'lucide-svelte';
	import IconButton from './IconButton.svelte';
	import Input from './Input.svelte';

	let {
		value = $bindable(''),
		label = '',
		hint = '',
		keyPlaceholder = 'Name',
		valuePlaceholder = 'Value',
		addLabel = 'Add row'
	}: {
		// The JSON object as text. Bound both ways: the parent stores what the API stores.
		value?: string;
		label?: string;
		hint?: string;
		keyPlaceholder?: string;
		valuePlaceholder?: string;
		addLabel?: string;
	} = $props();

	type Row = { key: string; value: string };

	function parse(json: string): Row[] {
		const text = (json ?? '').trim();
		if (!text) return [];
		try {
			const parsed = JSON.parse(text);
			if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return [];
			return Object.entries(parsed).map(([key, v]) => ({ key, value: String(v ?? '') }));
		} catch {
			// A hand-written blob that will not parse is left alone rather than silently
			// emptied; the parent keeps its text until the user actually edits a row.
			return [];
		}
	}

	function serialize(current: Row[]): string {
		const obj: Record<string, string> = {};
		for (const r of current) {
			const k = r.key.trim();
			if (k) obj[k] = r.value;
		}
		return Object.keys(obj).length > 0 ? JSON.stringify(obj) : '';
	}

	let rows = $state<Row[]>(parse(value));

	// Deliberately NOT $state: this is the bookkeeping that tells our own echo apart from
	// a value arriving from outside (a different record loaded into the form). Making it
	// reactive would make the two effects below depend on each other.
	let lastEmitted = value;

	// Outside -> rows.
	$effect(() => {
		if (value !== lastEmitted) {
			rows = parse(value);
			lastEmitted = value;
		}
	});

	// Rows -> outside. Reads every row field, so it re-runs on any keystroke.
	$effect(() => {
		const json = serialize(rows);
		if (json !== lastEmitted) {
			lastEmitted = json;
			value = json;
		}
	});

	function add() {
		rows = [...rows, { key: '', value: '' }];
	}

	function remove(i: number) {
		rows = rows.filter((_, idx) => idx !== i);
	}
</script>

<div class="space-y-1.5">
	{#if label}
		<div class="text-sm font-medium text-surface-800-200">{label}</div>
	{/if}

	{#each rows as row, i (i)}
		<div class="flex items-center gap-2">
			<Input bind:value={row.key} placeholder={keyPlaceholder} />
			<Input bind:value={row.value} placeholder={valuePlaceholder} />
			<IconButton label="Remove row" onclick={() => remove(i)}><X size={14} /></IconButton>
		</div>
	{/each}

	<button
		type="button"
		onclick={add}
		class="inline-flex items-center gap-1 text-xs text-primary-600-400 hover:underline"
	>
		<Plus size={13} />{addLabel}
	</button>

	{#if hint}
		<div class="text-xs text-surface-600-400">{hint}</div>
	{/if}
</div>
