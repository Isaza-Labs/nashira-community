<script lang="ts">
	import { page } from '$app/state';
	import {
		listCredentials,
		createCredential,
		updateCredential,
		deleteCredential,
		AUTH_METHODS,
		type Credential
	} from '$lib/api/credentials.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Select,
		Textarea,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import { Plus, Pencil, Trash2 } from 'lucide-svelte';

	const authOptions = AUTH_METHODS;

	let items = $state<Credential[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<Credential | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fType = $state('ssh');
	let fUsername = $state('');
	let fAuth = $state('password');
	let fPassword = $state('');
	let fKey = $state('');
	let fPassphrase = $state('');
	let fToken = $state('');
	let fApiKeyHeader = $state('');
	let fClientId = $state('');
	let fClientSecret = $state('');
	let fTokenUrl = $state('');
	let fScopes = $state('');

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'type', header: 'Type' },
		{ key: 'username', header: 'Username' },
		{ key: 'auth', header: 'Auth' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listCredentials()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function openCreate() {
		editing = null;
		formError = '';
		fName = '';
		fType = 'ssh';
		fUsername = '';
		fAuth = 'password';
		fPassword = '';
		fKey = '';
		fPassphrase = '';
		fToken = '';
		fApiKeyHeader = '';
		fClientId = '';
		fClientSecret = '';
		fTokenUrl = '';
		fScopes = '';
		modalOpen = true;
	}

	function openEdit(c: Credential) {
		editing = c;
		formError = '';
		fName = c.name;
		fType = c.type;
		fUsername = c.username ?? '';
		fAuth = c.authMethod || 'password';
		fPassword = '';
		fKey = '';
		fPassphrase = '';
		fToken = '';
		fApiKeyHeader = c.apiKeyHeader ?? '';
		fClientId = c.clientId ?? '';
		fClientSecret = '';
		fTokenUrl = c.tokenUrl ?? '';
		fScopes = c.scopes ?? '';
		modalOpen = true;
	}

	// Client-side mirror of the server's per-method material rules — same
	// messages, but without a round-trip. On edit, already-stored material
	// (has_* flags) satisfies the requirement.
	function missingMaterial(): string | null {
		const has = {
			key: !!fKey || !!editing?.hasPrivateKey,
			token: !!fToken || !!editing?.hasToken,
			secret: !!fClientSecret || !!editing?.hasClientSecret
		};
		if (fAuth === 'key' && !has.key) return 'Auth method "SSH private key" requires a private key.';
		if ((fAuth === 'token' || fAuth === 'api_key') && !has.token)
			return fAuth === 'token' ? 'A token is required.' : 'The API key value is required.';
		if (fAuth === 'oauth2') {
			if (!fClientId.trim()) return 'OAuth2 requires a client ID.';
			if (!has.secret) return 'OAuth2 requires a client secret.';
			if (!fTokenUrl.trim()) return 'OAuth2 requires a token URL.';
		}
		return null;
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		const missing = missingMaterial();
		if (missing) {
			formError = missing;
			return;
		}
		formError = '';
		saving = true;
		try {
			const payload = {
				name: fName.trim(),
				type: fType.trim() || 'ssh',
				username: fUsername.trim(),
				authMethod: fAuth,
				password: fPassword,
				privateKey: fKey,
				keyPassphrase: fPassphrase,
				token: fToken,
				apiKeyHeader: fApiKeyHeader.trim(),
				clientId: fClientId.trim(),
				clientSecret: fClientSecret,
				tokenUrl: fTokenUrl.trim(),
				scopes: fScopes.trim()
			};
			if (editing) await updateCredential(editing.id, payload);
			else await createCredential(payload);
			modalOpen = false;
			toast.success(editing ? 'Credential updated' : 'Credential created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the credential');
		} finally {
			saving = false;
		}
	}

	async function remove(c: Credential) {
		const ok = await confirm({
			title: 'Delete credential?',
			message: `"${c.name}" will be removed. Devices referencing it lose their credential.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteCredential(c.id);
			items = items.filter((x) => x.id !== c.id);
			toast.success('Credential deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the credential");
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Credentials · Nashira</title></svelte:head>

<PageHeader title="Credentials" description="Device and service credentials: password, SSH key, token, API key, OAuth2.">
	{#snippet actions()}
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New credential</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(c) => c.id} empty="No credentials — devices, repositories and integrations each pick one from here to authenticate.">
		{#snippet cell(row, col)}
			{#if col.key === 'name'}
				<span class="font-medium">{row.name}</span>
			{:else if col.key === 'type'}
				<Badge>{row.type}</Badge>
			{:else if col.key === 'username'}
				<span class="text-surface-600-400">{row.username || '—'}</span>
			{:else if col.key === 'auth'}
				<Badge tone={row.authMethod === 'oauth2' || row.authMethod === 'token' || row.authMethod === 'api_key' ? 'primary' : 'neutral'}>
					{row.authMethod || 'password'}
				</Badge>
			{:else if col.key === 'actions'}
				<div class="flex justify-end gap-1">
					<IconButton label="Edit credential" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
					<IconButton label="Delete credential" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
				</div>
			{/if}
		{/snippet}
	</DataTable>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit credential' : 'New credential'}>
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Name" bind:value={fName} required />
			<Input label="Type" bind:value={fType} hint="Context tag, e.g. ssh, git_token, netbox" />
		</div>
		<Select label="Auth method" bind:value={fAuth} options={authOptions} />

		{#if fAuth === 'password'}
			<div class="grid gap-3 sm:grid-cols-2">
				<Input label="Username" bind:value={fUsername} />
				<Input
					label="Password"
					bind:value={fPassword}
					type="password"
					hint={editing ? 'Leave blank to keep' : ''}
				/>
			</div>
		{:else if fAuth === 'key'}
			<Input label="Username" bind:value={fUsername} />
			<Textarea
				label="Private key"
				bind:value={fKey}
				rows={4}
				hint={editing ? 'Leave blank to keep' : 'PEM contents'}
			/>
			<Input
				label="Key passphrase"
				bind:value={fPassphrase}
				type="password"
				hint={editing ? 'Leave blank to keep' : 'Optional'}
			/>
		{:else if fAuth === 'token'}
			<div class="grid gap-3 sm:grid-cols-2">
				<Input label="Username" bind:value={fUsername} hint="Optional; e.g. the PAT owner (git uses it)" />
				<Input
					label="Token"
					bind:value={fToken}
					type="password"
					hint={editing ? 'Leave blank to keep' : 'Bearer token or personal access token'}
				/>
			</div>
		{:else if fAuth === 'api_key'}
			<div class="grid gap-3 sm:grid-cols-2">
				<Input
					label="API key value"
					bind:value={fToken}
					type="password"
					hint={editing ? 'Leave blank to keep' : ''}
				/>
				<Input label="Header name" bind:value={fApiKeyHeader} placeholder="X-API-Key" hint="Blank → Authorization header" />
			</div>
		{:else if fAuth === 'oauth2'}
			<div class="grid gap-3 sm:grid-cols-2">
				<Input label="Client ID" bind:value={fClientId} required />
				<Input
					label="Client secret"
					bind:value={fClientSecret}
					type="password"
					hint={editing ? 'Leave blank to keep' : ''}
				/>
			</div>
			<Input label="Token URL" bind:value={fTokenUrl} placeholder="https://idp.example.com/oauth2/token" required />
			<Input label="Scopes" bind:value={fScopes} hint="Space-separated, e.g. read:devices write:configs" />
		{/if}
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>
