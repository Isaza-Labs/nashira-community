<script lang="ts">
	import { untrack } from 'svelte';
	import { Input, Checkbox, Alert, Select } from '$lib/components/ui';
	import type { InventorySource, SourcePayload } from '$lib/api/inventory.api';
	import {
		listSecrets,
		createSecret,
		updateSecret,
		secretRef,
		type Secret
	} from '$lib/api/secrets.api';
	import { listCredentials, type Credential } from '$lib/api/credentials.api';

	// Create/edit form for a NetBox inventory source. Submitted from the modal
	// footer via form="source-form".
	//
	// Authentication never handles a raw token beyond this form: a pasted token is
	// written to the secret store first and the source only stores the reference.
	// The API enforces the same rule and masks legacy literal tokens as '***'.
	let {
		initial = null,
		onsave
	}: { initial?: InventorySource | null; onsave: (payload: SourcePayload) => void } = $props();

	type AuthMode = 'none' | 'token' | 'secret' | 'credential' | 'ref';

	const SECRET_REF_RE = /^\$\{secret:secret:([^:}]+):value\}$/;
	const CREDENTIAL_REF_RE = /^\$\{secret:credential:([^:}]+):token\}$/;
	const MASKED = '***';

	function parseInitialAuth(ref: string | null): {
		mode: AuthMode;
		secretName: string;
		credentialName: string;
		rawRef: string;
	} {
		const none = { mode: 'none' as AuthMode, secretName: '', credentialName: '', rawRef: '' };
		if (!ref) return none;
		// A legacy literal token: the API masks it and treats an echoed '***' as
		// "keep what is stored".
		if (ref === MASKED) return { ...none, mode: 'token' };
		const s = SECRET_REF_RE.exec(ref);
		if (s) return { ...none, mode: 'secret', secretName: s[1] };
		const c = CREDENTIAL_REF_RE.exec(ref);
		if (c) return { ...none, mode: 'credential', credentialName: c[1] };
		// A bare secret name (accepted by the API) or an advanced reference.
		if (!ref.includes('${secret:')) return { ...none, mode: 'secret', secretName: ref };
		return { ...none, mode: 'ref', rawRef: ref };
	}

	const seed = untrack(() => initial);
	const initialAuth = parseInitialAuth(seed?.tokenSecretRef ?? null);
	// True when editing a source whose stored token is a masked literal — leaving
	// the token field blank then means "keep it".
	const hasStoredToken = (seed?.tokenSecretRef ?? '') === MASKED;

	let name = $state(seed?.name ?? '');
	let baseUrl = $state(seed?.baseUrl ?? '');
	let kind = $state(seed?.kind ?? 'netbox');
	let siteFilter = $state(seed?.siteFilter ?? '');
	let allowPrivateNetwork = $state(seed?.allowPrivateNetwork ?? false);

	// Typed string (not AuthMode) so it can bind to Select's string value prop.
	let authMode = $state<string>(initialAuth.mode);
	let secretName = $state(initialAuth.secretName);
	let credentialName = $state(initialAuth.credentialName);
	let rawRef = $state(initialAuth.rawRef);
	let newToken = $state('');

	let secrets = $state<Secret[]>([]);
	let credentials = $state<Credential[]>([]);
	let error = $state('');
	let storing = $state(false);

	// The form is only reachable by admins (RoleGate on the page) and both listing
	// endpoints are admin-only, so a failure here is connectivity, not permissions.
	$effect(() => {
		listSecrets().then(
			(s) => (secrets = s),
			() => {}
		);
		listCredentials().then(
			(r) => (credentials = r.items.filter((c) => c.hasToken)),
			() => {}
		);
	});

	const AUTH_OPTIONS: { value: AuthMode; label: string }[] = [
		{ value: 'none', label: 'No authentication' },
		{ value: 'token', label: 'API token (stored as a secret)' },
		{ value: 'secret', label: 'Existing secret' },
		{ value: 'credential', label: 'Existing credential (token)' },
		{ value: 'ref', label: 'Custom secret reference (advanced)' }
	];

	// The secret a pasted token is stored under, derived from the source name so
	// rotation on re-edit overwrites the same secret instead of accumulating copies.
	function tokenSecretName(): string {
		const slug = name
			.trim()
			.toLowerCase()
			.replace(/[^a-z0-9]+/g, '-')
			.replace(/^-+|-+$/g, '');
		return `netbox-${slug || 'source'}-token`.slice(0, 64).replace(/[_-]+$/, '');
	}

	async function resolveTokenRef(): Promise<string | null> {
		switch (authMode) {
			case 'none':
				return '';
			case 'secret': {
				if (!secretName) throw new Error('Choose the secret that holds the API token.');
				return secretRef(secretName);
			}
			case 'credential': {
				if (!credentialName) throw new Error('Choose the credential that holds the API token.');
				return `\${secret:credential:${credentialName}:token}`;
			}
			case 'ref': {
				const v = rawRef.trim();
				if (!v.startsWith('${secret:'))
					throw new Error('The reference must look like ${secret:secret:<name>:value}.');
				return v;
			}
			case 'token': {
				const token = newToken.trim();
				if (!token) {
					if (hasStoredToken) return null; // keep the stored token
					throw new Error('Paste the API token, or pick another authentication option.');
				}
				// Store the token first so only the reference travels with the source.
				const sName = tokenSecretName();
				const existing = secrets.find((s) => s.name === sName);
				if (existing) await updateSecret(existing.id, { value: token });
				else
					await createSecret({
						name: sName,
						value: token,
						description: `NetBox API token for inventory source "${name.trim()}"`
					});
				return secretRef(sName);
			}
			default:
				return '';
		}
	}

	async function submit(e: SubmitEvent) {
		e.preventDefault();
		if (!name.trim()) {
			error = 'Name is required.';
			return;
		}
		if (!baseUrl.trim()) {
			error = 'Base URL is required.';
			return;
		}
		error = '';
		storing = true;
		try {
			const tokenSecretRefValue = await resolveTokenRef();
			onsave({
				name: name.trim(),
				kind: kind.trim() || 'netbox',
				baseUrl: baseUrl.trim(),
				tokenSecretRef: tokenSecretRefValue,
				siteFilter,
				allowPrivateNetwork
			});
		} catch (err) {
			error = err instanceof Error ? err.message : "Couldn't store the API token.";
		} finally {
			storing = false;
		}
	}
</script>

<form id="source-form" onsubmit={submit} class="space-y-3">
	{#if error}<Alert tone="error">{error}</Alert>{/if}
	<Input label="Name" bind:value={name} required />
	<Input label="Base URL" bind:value={baseUrl} required hint="e.g. https://netbox.example.com" />
	<div class="grid gap-3 sm:grid-cols-2">
		<Input label="Kind" bind:value={kind} hint="netbox" />
		<Input label="Site filter" bind:value={siteFilter} hint="Optional" />
	</div>

	<Select
		label="Authentication"
		bind:value={authMode}
		options={AUTH_OPTIONS}
		hint={authMode === 'none' ? 'Only for a NetBox that allows anonymous API reads.' : ''}
	/>

	{#if authMode === 'token'}
		<Input
			label="API token"
			type="password"
			autocomplete="off"
			bind:value={newToken}
			hint={hasStoredToken
				? 'A token is already stored. Leave blank to keep it, or paste a new one to rotate it into the secret store.'
				: `Stored encrypted in the secret store as "${tokenSecretName()}" — the source only keeps the reference.`}
		/>
	{:else if authMode === 'secret'}
		<Select
			label="Secret"
			bind:value={secretName}
			placeholder="Choose a secret…"
			options={secrets.map((s) => ({ value: s.name, label: s.name }))}
			hint="Managed in Administration → Secrets. The value never leaves the server."
		/>
	{:else if authMode === 'credential'}
		<Select
			label="Credential"
			bind:value={credentialName}
			placeholder="Choose a credential…"
			options={credentials.map((c) => ({ value: c.name, label: c.name }))}
			hint="Only credentials that hold a token are listed. Managed in Administration → Credentials."
		/>
	{:else if authMode === 'ref'}
		<Input
			label="Secret reference"
			bind:value={rawRef}
			hint={'e.g. ${secret:secret:<name>:value} or ${secret:integration:<name>:token}'}
		/>
	{/if}

	<Checkbox bind:checked={allowPrivateNetwork} label="Allow private-network base URLs" />
	{#if storing}
		<p class="text-xs text-surface-600-400">Storing the token…</p>
	{/if}
</form>
