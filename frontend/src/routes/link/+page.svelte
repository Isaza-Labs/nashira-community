<script lang="ts">
	// Self-service account linking. The bot posts a one-time URL into the chat;
	// following it binds that external identity to whoever is signed in here.
	//
	// The route is in the layout's PUBLIC_PATHS so the shell does not redirect and
	// strip `?token=` on the way to /login. Auth is handled below instead, carrying
	// the FULL url (path + query) through the login round trip.
	import { page } from '$app/state';
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { previewLink, confirmLink, type LinkPreview } from '$lib/api/messaging.api';
	import { errorMessage } from '$lib/api/client';
	import { authStore } from '$lib/stores/auth.svelte';
	import { Button, Alert, Spinner, Card } from '$lib/components/ui';
	import { MessageSquare, CheckCircle2 } from 'lucide-svelte';

	const token = $derived(page.url.searchParams.get('token'));

	let preview = $state<LinkPreview | null>(null);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let confirming = $state(false);
	let done = $state(false);

	onMount(async () => {
		if (!authStore.isAuthenticated) {
			goto(`/login?redirect=${encodeURIComponent(page.url.pathname + page.url.search)}`);
			return;
		}
		if (!token) {
			error = 'No link token provided.';
			loading = false;
			return;
		}
		try {
			preview = await previewLink(token);
		} catch (e) {
			error = errorMessage(e);
		} finally {
			loading = false;
		}
	});

	async function confirm() {
		if (!token || confirming) return;
		error = null;
		confirming = true;
		try {
			await confirmLink(token);
			done = true;
		} catch (e) {
			error = errorMessage(e);
		} finally {
			confirming = false;
		}
	}
</script>

<svelte:head><title>Link account · Nashira</title></svelte:head>

<div class="w-full max-w-md">
	<Card>
		<div class="space-y-5">
			<div class="flex items-center gap-3">
				<span
					class="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-primary-500/10 text-primary-600-400"
				>
					<MessageSquare size={20} />
				</span>
				<h1 class="text-xl font-semibold">Link your messaging account</h1>
			</div>

			{#if loading}
				<div class="flex items-center gap-2 text-sm text-surface-600-400">
					<Spinner size="sm" />Loading…
				</div>
			{:else if done}
				<div class="flex items-start gap-3">
					<span class="mt-0.5 text-success-600-400"><CheckCircle2 size={20} /></span>
					<div>
						<p class="font-medium">Account linked.</p>
						<p class="text-sm text-surface-600-400">
							You can return to your chat and message the assistant.
						</p>
					</div>
				</div>
				<Button variant="primary" onclick={() => goto('/')}>Go to Nashira</Button>
			{:else if error}
				<Alert tone="error">{error}</Alert>
				<Button variant="ghost" onclick={() => goto('/')}>Back to the app</Button>
			{:else if preview}
				<p class="text-sm text-surface-600-400">
					You're signed in as
					<span class="font-medium text-surface-950-50">{authStore.session?.username}</span>. Confirm
					to bind this external identity to your account — the assistant will then act with your
					role.
				</p>
				<dl class="space-y-1 rounded-lg border border-surface-100-900 p-3 text-sm">
					<div class="flex justify-between gap-3">
						<dt class="text-surface-600-400">Provider</dt>
						<dd class="font-medium capitalize">{preview.provider}</dd>
					</div>
					<div class="flex justify-between gap-3">
						<dt class="text-surface-600-400">Channel</dt>
						<dd class="font-medium">{preview.channelName}</dd>
					</div>
					<div class="flex justify-between gap-3">
						<dt class="text-surface-600-400">External user</dt>
						<dd class="truncate font-mono text-xs">{preview.externalUserId}</dd>
					</div>
					<div class="flex justify-between gap-3">
						<dt class="text-surface-600-400">Expires</dt>
						<dd class="text-xs">{new Date(preview.expiresAt).toLocaleString()}</dd>
					</div>
				</dl>
				<div class="flex gap-2">
					<Button variant="ghost" onclick={() => goto('/')}>Cancel</Button>
					<Button variant="primary" loading={confirming} onclick={confirm}>Confirm link</Button>
				</div>
			{/if}
		</div>
	</Card>
</div>
