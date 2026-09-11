<script lang="ts">
	import { untrack } from 'svelte';
	import { Input, Alert } from '$lib/components/ui';
	import CredentialPicker from '$lib/components/credential/CredentialPicker.svelte';
	import type { GitRepo, RepoPayload } from '$lib/api/git.api';

	// Create/edit form for a git repository (Admin). Submitted from the modal footer
	// via form="repo-form". The auth-credential picker degrades if listing fails.
	//
	// The transport is HTTPS + PAT only — the server rejects ssh:// and git:// and
	// authenticates with the credential's token (falling back to its password for rows
	// predating auth_method 'token'). Both facts are enforced here rather than left to
	// a 400 on save or a clone that fails hours later on the first pull.
	let {
		initial = null,
		onsave
	}: { initial?: GitRepo | null; onsave: (payload: RepoPayload) => void } = $props();

	const seed = untrack(() => initial);
	let name = $state(seed?.name ?? '');
	let url = $state(seed?.url ?? '');
	let defaultBranch = $state(seed?.defaultBranch ?? 'main');
	let description = $state(seed?.description ?? '');
	let authCredentialId = $state(seed?.authCredentialId ?? '');
	let error = $state('');

	// Only these can authenticate an HTTPS remote: 'token' carries the PAT, 'password'
	// is where PATs lived before the token field existed. An SSH key cannot be used at
	// all while the SSH transport is deferred.
	const GIT_AUTH_METHODS = ['token', 'password'];

	const trimmedUrl = $derived(url.trim());
	const urlLooksSsh = $derived(
		trimmedUrl.startsWith('git@') || trimmedUrl.startsWith('ssh://') || trimmedUrl.startsWith('git://')
	);

	function submit(e: SubmitEvent) {
		e.preventDefault();
		if (!name.trim()) {
			error = 'Name is required.';
			return;
		}
		if (!trimmedUrl) {
			error = 'URL is required.';
			return;
		}
		if (!/^https:\/\//i.test(trimmedUrl)) {
			error = urlLooksSsh
				? 'SSH remotes are not supported. Use the https:// clone URL and a token credential.'
				: 'The URL must start with https://.';
			return;
		}
		error = '';
		onsave({
			name: name.trim(),
			url: trimmedUrl,
			defaultBranch: defaultBranch.trim() || 'main',
			description,
			authCredentialId
		});
	}
</script>

<form id="repo-form" onsubmit={submit} class="space-y-3">
	{#if error}<Alert tone="error">{error}</Alert>{/if}
	<Input label="Name" bind:value={name} required />
	<Input label="URL" bind:value={url} required hint="https:// clone URL — SSH remotes are not supported" />
	{#if urlLooksSsh}
		<Alert tone="warning">
			That looks like an SSH remote. Copy the <code>https://</code> clone URL instead and
			authenticate with a token credential.
		</Alert>
	{/if}
	<div class="grid gap-3 sm:grid-cols-2">
		<Input label="Default branch" bind:value={defaultBranch} />
		<CredentialPicker
			label="Auth credential"
			bind:value={authCredentialId}
			allowedMethods={GIT_AUTH_METHODS}
			showMethod
			hint="A credential holding a personal access token. Leave empty for a public repository."
		/>
	</div>
	<Input label="Description" bind:value={description} />
</form>
