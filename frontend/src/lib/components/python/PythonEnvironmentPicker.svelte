<script lang="ts">
	import { Select } from '$lib/components/ui';
	import {
		listPythonEnvironments,
		environmentOptions,
		type PythonEnvironment
	} from '$lib/api/python-environments.api';

	// Picks a Python environment (Admin → Python environments) for a device or a snippet.
	// '' is "System default" — the image's python3, which is what everything used before
	// environments existed. The list is readable by Operators, so whoever can edit the
	// device or snippet can pick; if it cannot be loaded, the current choice is kept and
	// only "System default" is offered beside it, so a save never silently drops it.
	let {
		value = $bindable(''),
		label = 'Python environment',
		hint = ''
	}: { value?: string; label?: string; hint?: string } = $props();

	let items = $state<PythonEnvironment[]>([]);
	let failed = $state(false);

	$effect(() => {
		listPythonEnvironments()
			.then((res) => (items = res.items))
			.catch(() => (failed = true));
	});

	const options = $derived.by(() => {
		const opts = environmentOptions(items);
		if (value && !opts.some((o) => o.value === value))
			opts.push({ value, label: failed ? 'Current environment' : 'Deleted environment' });
		return opts;
	});

	const selected = $derived(items.find((e) => e.id === value) ?? null);
	const note = $derived(
		selected && selected.status !== 'ready'
			? `“${selected.name}” is ${selected.status} — runs that need it fail clearly until it is ready.`
			: hint
	);
</script>

<Select {label} bind:value {options} hint={note} />
