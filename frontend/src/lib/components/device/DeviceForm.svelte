<script lang="ts">
	import { untrack } from 'svelte';
	import { Input, Alert, Checkbox } from '$lib/components/ui';
	import CredentialPicker from '$lib/components/credential/CredentialPicker.svelte';
	import type { Device, DevicePayload } from '$lib/api/devices.api';

	// Create/edit form for a device. Submitted from the modal footer via
	// form="device-form". Remounts per open, so `initial` seeds once. The credential
	// picker degrades gracefully when the caller can't list credentials (Admin-gated).
	let {
		initial = null,
		onsave
	}: { initial?: Device | null; onsave: (payload: DevicePayload) => void } = $props();

	const seed = untrack(() => initial);
	let name = $state(seed?.name ?? '');
	let ipAddress = $state(seed?.ipAddress ?? '');
	let platform = $state(seed?.platform ?? '');
	let vendor = $state(seed?.vendor ?? '');
	let osVersion = $state(seed?.osVersion ?? '');
	let site = $state(seed?.site ?? '');
	let role = $state(seed?.role ?? '');
	let status = $state(seed?.status ?? '');
	let credentialId = $state(seed?.credentialId ?? '');
	// New devices mirror the backend defaults: draft and production on, qa opt-in.
	let allowDraft = $state(seed?.allowDraft ?? true);
	let allowQa = $state(seed?.allowQa ?? false);
	let allowProduction = $state(seed?.allowProduction ?? true);
	let fingerprint = $state('');
	let error = $state('');

	// A device with all three off is parked — no run can reach it. That is a valid
	// state (it is how you take a box out of rotation without deleting it), so warn
	// rather than block.
	const parked = $derived(!allowDraft && !allowQa && !allowProduction);

	function submit(e: SubmitEvent) {
		e.preventDefault();
		if (!name.trim()) {
			error = 'Name is required.';
			return;
		}
		if (!ipAddress.trim()) {
			error = 'IP address is required.';
			return;
		}
		error = '';
		onsave({
			name: name.trim(),
			ipAddress: ipAddress.trim(),
			platform,
			vendor,
			osVersion,
			site,
			role,
			status,
			credentialId,
			allowDraft,
			allowQa,
			allowProduction,
			fingerprint: fingerprint.trim()
		});
	}
</script>

<form id="device-form" onsubmit={submit} class="space-y-3">
	{#if error}<Alert tone="error">{error}</Alert>{/if}
	<div class="grid gap-3 sm:grid-cols-2">
		<Input label="Name" bind:value={name} required />
		<Input label="IP address" bind:value={ipAddress} required />
		<Input label="Platform" bind:value={platform} />
		<Input label="Vendor" bind:value={vendor} />
		<Input label="OS version" bind:value={osVersion} />
		<Input label="Site" bind:value={site} />
		<Input label="Role" bind:value={role} />
		<Input label="Status" bind:value={status} hint="e.g. active, maintenance" />
	</div>
	<CredentialPicker label="Credential" bind:value={credentialId} />

	<fieldset class="space-y-2 rounded-lg border border-surface-200-800 p-3">
		<legend class="px-1 text-sm font-medium">Environments</legend>
		<p class="text-xs text-surface-600-400">
			Which promotion stage may run a workflow against this device.
		</p>
		<div class="flex flex-wrap gap-4 pt-1">
			<Checkbox bind:checked={allowDraft} label="Draft" />
			<Checkbox bind:checked={allowQa} label="QA" />
			<Checkbox bind:checked={allowProduction} label="Production" />
		</div>
		{#if parked}
			<Alert tone="warning">
				No environment is enabled — no workflow run will be able to target this device.
			</Alert>
		{/if}
	</fieldset>

	{#if initial?.sourceId}
		<p class="text-xs text-surface-600-400">
			Synced from an inventory source{initial.lastSyncAt
				? ` · last sync ${new Date(initial.lastSyncAt).toLocaleString()}`
				: ''}. Fields the source provides are overwritten on the next sync.
		</p>
	{/if}

	<Input
		label="Expected SSH host-key fingerprint"
		bind:value={fingerprint}
		hint={initial ? 'Leave blank to keep the current value' : 'Optional; pins the device host key'}
	/>
</form>
