<script lang="ts">
	// Secret manager. Secrets here are referenced as ${secret:secret:<name>:value}
	// from spec auth configs, integration auth configs and inventory sources. The
	// backend pulls them at execute time through SecretResolver — plaintext never
	// leaves it, so this page only ever shows metadata and a "value set" indicator.

	import { page } from '$app/state';
	import {
		listSecrets,
		createSecret,
		updateSecret,
		deleteSecret,
		secretRef,
		type Secret
	} from '$lib/api/secrets.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Textarea,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import { absolute, timeAgo } from '$lib/utils/time';
	import { copyText } from '$lib/utils/clipboard';
	import { errorMessage } from '$lib/api/client';
	import { Plus, Pencil, Trash2, RefreshCw, Copy, Info } from 'lucide-svelte';

	let items = $state<Secret[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	// `editing === null` means we are creating; otherwise it is an update where the
	// value stays empty unless the admin explicitly rotates.
	let modalOpen = $state(false);
	let editing = $state<Secret | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fDescription = $state('');
	let fValue = $state('');

	// Mirrors the backend regex so bad input is flagged before a round-trip.
	const NAME_RE = /^[a-z0-9](?:[a-z0-9_-]{1,62}[a-z0-9])?$/;

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'description', header: 'Description' },
		{ key: 'value', header: 'Value', sortValue: (s: Secret) => s.hasValue },
		{ key: 'updated', header: 'Updated', sortValue: (s: Secret) => s.updatedAt },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = await listSecrets();
		} catch (e) {
			error = e;
			toast.fromError(e, "Couldn't load secrets");
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function openCreate() {
		editing = null;
		fName = '';
		fDescription = '';
		fValue = '';
		formError = '';
		modalOpen = true;
	}

	function openEdit(s: Secret) {
		editing = s;
		fName = s.name;
		fDescription = s.description ?? '';
		fValue = '';
		formError = '';
		modalOpen = true;
	}

	function validate(): string {
		if (!NAME_RE.test(fName.trim()))
			return 'Name must be 2–64 chars, lowercase letters/digits/hyphens/underscores.';
		if (!editing && !fValue) return 'Value is required when creating a secret.';
		return '';
	}

	async function save() {
		if (saving) return;
		formError = validate();
		if (formError) return;

		// Rotating an existing secret (editing + a new non-empty value) swaps the
		// ciphertext the moment we save, so confirm before replacing it. The
		// value-send check below uses the same truthiness test.
		if (editing && fValue) {
			const ok = await confirm({
				tone: 'danger',
				title: `Rotate secret "${editing.name}"?`,
				message:
					'Rotating replaces the credential immediately; in-flight requests will use the new value.',
				confirmLabel: 'Rotate'
			});
			if (!ok) return;
		}

		saving = true;
		try {
			if (editing) {
				// Only send the value when the admin filled it in — editing the
				// description alone must not touch the ciphertext.
				await updateSecret(editing.id, {
					description: fDescription.trim() || undefined,
					...(fValue ? { value: fValue } : {})
				});
				toast.success('Secret updated');
			} else {
				await createSecret({
					name: fName.trim(),
					description: fDescription.trim() || undefined,
					value: fValue
				});
				toast.success('Secret created');
			}
			modalOpen = false;
			await load();
		} catch (e) {
			formError = errorMessage(e);
		} finally {
			saving = false;
		}
	}

	async function remove(s: Secret) {
		const ok = await confirm({
			title: `Delete secret "${s.name}"?`,
			message: 'Anything referencing it will break until a replacement is created.',
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteSecret(s.id);
			toast.success('Secret deleted');
			await load();
		} catch (e) {
			toast.fromError(e, 'Delete failed');
		}
	}

	// Copies the ${secret:…} template for a row so the admin can paste it into an
	// auth config without remembering the syntax. copyText falls back to the
	// legacy execCommand path on insecure origins (http://<ip>), where
	// navigator.clipboard does not exist; only when both mechanisms fail is the
	// reference shown instead of a success message that did not happen.
	async function copyRef(s: Secret) {
		const ref = secretRef(s.name);
		if (await copyText(ref)) toast.success('Reference copied', { description: ref });
		else toast.info('Copy failed — reference:', { description: ref });
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Secrets · Nashira</title></svelte:head>

<PageHeader
	title="Secrets"
	description={'Named credentials the platform consumes when calling external APIs. Reference them as ${secret:secret:<name>:value}.'}
>
	{#snippet actions()}
		<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New secret</Button>
	{/snippet}
</PageHeader>

<Alert tone="primary">
	<div class="flex items-start gap-2 text-xs">
		<Info size={13} class="mt-0.5 shrink-0" />
		<div>
			Plaintext values are only readable by the backend; this page shows metadata only.
			Rotate by editing the secret and pasting a new value — references stay stable under
			the same name.
		</div>
	</div>
</Alert>

<div class="mt-4">
	{#if error}
		<ErrorState {error} onRetry={load} />
	{:else}
		<DataTable
			{loading}
			{columns}
			rows={items}
			rowKey={(s) => s.id}
			empty="No secrets yet — add the API keys, bearer tokens and basic-auth passwords the platform needs. Specs and integrations reference them by name."
		>
			{#snippet cell(row, col)}
				{#if col.key === 'name'}
					<span class="font-mono text-sm">{row.name}</span>
				{:else if col.key === 'description'}
					<span class="text-surface-600-400">{row.description || '—'}</span>
				{:else if col.key === 'value'}
					{#if row.hasValue}<Badge tone="success">set</Badge>{:else}<Badge tone="error">empty</Badge>{/if}
				{:else if col.key === 'updated'}
					<span class="whitespace-nowrap tabular-nums text-surface-600-400" title={absolute(row.updatedAt)}>
						{timeAgo(row.updatedAt)}
					</span>
				{:else if col.key === 'actions'}
					<div class="flex justify-end gap-1">
						<IconButton label="Copy reference" onclick={() => copyRef(row)}><Copy size={14} /></IconButton>
						<IconButton label="Edit secret" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
						<IconButton label="Delete secret" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
					</div>
				{/if}
			{/snippet}
		</DataTable>
	{/if}
</div>

<Modal bind:open={modalOpen} title={editing ? 'Edit secret' : 'New secret'}>
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}
		<Input
			label="Name"
			placeholder="netbox-token"
			bind:value={fName}
			disabled={!!editing || saving}
			required
			hint={editing ? 'Names are immutable — references stay stable.' : ''}
		/>
		<Textarea
			label="Description"
			placeholder="What this credential authorizes — shown in /admin/secrets only."
			rows={2}
			bind:value={fDescription}
			disabled={saving}
		/>
		<Textarea
			label={editing ? 'New value (leave empty to keep current)' : 'Value'}
			placeholder="Paste the secret value here."
			rows={3}
			bind:value={fValue}
			disabled={saving}
			hint={editing
				? 'Leave blank to update only the description. Submitting a value rotates the ciphertext.'
				: 'Stored encrypted at rest. The value cannot be retrieved after submission — rotate to replace it.'}
		/>
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)} disabled={saving}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>
			{editing ? 'Save changes' : 'Create'}
		</Button>
	{/snippet}
</Modal>
