<script lang="ts">
	import { Select } from '$lib/components/ui';
	import { AUTH_METHODS, listCredentials, type Credential } from '$lib/api/credentials.api';

	// Picks a credential stored in /admin/credentials. Extracted from DeviceForm and
	// RepoForm, which carried the same block verbatim; `allowedMethods` is what the
	// HTTP-auth callers add, since an SSH private key cannot authenticate a request.
	//
	// Listing credentials is Admin-gated, so a caller without the role gets nothing
	// rendered rather than a broken select.
	let {
		value = $bindable(''),
		selected = $bindable<Credential | null>(null),
		label = 'Credential',
		hint = '',
		emptyLabel = '— none —',
		allowedMethods = null,
		showMethod = false
	}: {
		value?: string;
		selected?: Credential | null;
		label?: string;
		hint?: string;
		emptyLabel?: string;
		allowedMethods?: string[] | null;
		showMethod?: boolean;
	} = $props();

	let credentials = $state<Credential[]>([]);
	let available = $state(false);
	let hidden = $state(0); // credentials filtered out as unusable here

	$effect(() => {
		listCredentials()
			.then((r) => {
				credentials = r.items;
				available = true;
			})
			.catch(() => {
				available = false; // needs Admin; hide the picker rather than show it broken
			});
	});

	const usable = $derived(
		allowedMethods ? credentials.filter((c) => allowedMethods.includes(c.authMethod)) : credentials
	);

	$effect(() => {
		hidden = credentials.length - usable.length;
	});

	const options = $derived([
		{ value: '', label: emptyLabel },
		...usable.map((c) => ({
			value: c.id,
			label: showMethod ? `${c.name} — ${c.authMethod}` : c.name
		}))
	]);

	// Keep the resolved row in sync so the parent can preselect a scheme from it.
	$effect(() => {
		selected = usable.find((c) => c.id === value) ?? null;
	});

	// Name the methods this caller actually accepts. The note used to state the HTTP-auth
	// set verbatim, which read as a lie to anyone picking a credential for git — where
	// only a token (or a legacy password) works.
	const allowedLabels = $derived(
		(allowedMethods ?? [])
			.map((m) => AUTH_METHODS.find((a) => a.value === m)?.label ?? m)
			.join(', ')
	);

	const note = $derived(
		hidden > 0
			? `${hidden} stored credential(s) are hidden: only ${allowedLabels} can be used here.`
			: ''
	);
</script>

{#if available}
	<Select {label} bind:value {options} hint={hint || note} />
	{#if hint && note}
		<p class="mt-1 text-xs text-surface-600-300">{note}</p>
	{/if}
{/if}
