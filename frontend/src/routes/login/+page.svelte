<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { login } from '$lib/api/auth.api';
	import { errorMessage } from '$lib/api/client';
	import { Card, Input, Button, Alert, Logo, ModeToggle } from '$lib/components/ui';

	let username = $state('');
	let password = $state('');
	let submitting = $state(false);
	let error = $state<string | null>(null);

	async function onSubmit(e: SubmitEvent) {
		e.preventDefault();
		if (submitting) return;
		error = null;
		submitting = true;
		try {
			await login(username.trim(), password);
			const redirect = page.url.searchParams.get('redirect');
			await goto(redirect && redirect.startsWith('/') ? redirect : '/');
		} catch (err) {
			error = errorMessage(err);
		} finally {
			submitting = false;
		}
	}
</script>

<svelte:head><title>Sign in · Nashira</title></svelte:head>

<div class="fixed right-4 top-4"><ModeToggle /></div>

<div class="w-full max-w-sm">
	<div class="mb-6 flex flex-col items-center text-center">
		<h1><Logo size={32} /></h1>
		<p class="mt-2 text-sm text-surface-600-400">Sign in to continue</p>
	</div>

	<Card>
		<form class="space-y-4" onsubmit={onSubmit} aria-label="Sign in">
			{#if error}<Alert tone="error">{error}</Alert>{/if}
			<Input id="username" label="Username" bind:value={username} autocomplete="username" required />
			<Input
				id="password"
				label="Password"
				type="password"
				bind:value={password}
				autocomplete="current-password"
				required
			/>
			<Button type="submit" variant="primary" full loading={submitting}>
				{submitting ? 'Signing in…' : 'Sign in'}
			</Button>
		</form>
	</Card>
</div>
