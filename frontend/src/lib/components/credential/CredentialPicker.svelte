<script lang="ts">
	import { Select } from '$lib/components/ui';
	import {
		AUTH_METHODS,
		listAssignableCredentials,
		listCredentials,
		type AssignableCredential,
		type Credential
	} from '$lib/api/credentials.api';

	// Picks a credential stored in the Vault (/admin/vault). Extracted from DeviceForm and
	// RepoForm, which carried the same block verbatim; `allowedMethods` is what the
	// HTTP-auth callers add, since an SSH private key cannot authenticate a request.
	//
	// Listing credentials is Admin-gated, so a caller without the role gets nothing
	// rendered rather than a broken select.
	//
	// source="assignable" lists instead only the credentials an Admin made assignable to
	// devices (`/api/credential/assignable`, open to Operators): the device form uses it
	// for non-Admins. Those rows carry no username or secret flags, so `selected` stays
	// null in that mode; `assignable` hands the list back to the caller.
	let {
		value = $bindable(''),
		selected = $bindable<Credential | null>(null),
		assignable = $bindable<AssignableCredential[]>([]),
		source = 'all',
		label = 'Credential',
		hint = '',
		emptyLabel = '— none —',
		allowedMethods = null,
		showMethod = false
	}: {
		value?: string;
		selected?: Credential | null;
		assignable?: AssignableCredential[];
		source?: 'all' | 'assignable';
		label?: string;
		hint?: string;
		emptyLabel?: string;
		allowedMethods?: string[] | null;
		showMethod?: boolean;
	} = $props();

	type Row = { id: string; name: string; authMethod: string };

	let credentials = $state<Row[]>([]);
	let full = $state<Credential[]>([]);
	let available = $state(false);
	let hidden = $state(0); // credentials filtered out as unusable here

	$effect(() => {
		if (source === 'assignable') {
			listAssignableCredentials()
				.then((rows) => {
					assignable = rows;
					credentials = rows;
					available = true;
				})
				.catch(() => {
					assignable = [];
					available = false; // needs Operator; hide the picker rather than show it broken
				});
			return;
		}
		listCredentials()
			.then((r) => {
				full = r.items;
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

	// In assignable mode the current link may be a credential this caller cannot list.
	// It stays selected (and is resent unchanged) rather than showing as "none".
	const unlisted = $derived(
		source === 'assignable' && !!value && !usable.some((c) => c.id === value)
	);

	const options = $derived([
		{ value: '', label: emptyLabel },
		...(unlisted ? [{ value, label: 'Current credential (managed by an Admin)' }] : []),
		...usable.map((c) => ({
			value: c.id,
			label: showMethod ? `${c.name} — ${c.authMethod}` : c.name
		}))
	]);

	// Keep the resolved row in sync so the parent can preselect a scheme from it.
	$effect(() => {
		selected =
			source === 'assignable'
				? null
				: (full.find((c) => c.id === value && usable.some((u) => u.id === c.id)) ?? null);
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
