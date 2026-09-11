<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { authStore } from '$lib/stores/auth.svelte';
	import { changePassword, logout } from '$lib/api/auth.api';
	import {
		listProfiles,
		getMyProfile,
		setMyProfile,
		MAX_CUSTOM_PROFILE_TEXT,
		type Profile
	} from '$lib/api/profiles.api';
	import { errorMessage } from '$lib/api/client';
	import { PageHeader, Card, Input, Button, Alert, Select, Textarea } from '$lib/components/ui';

	let current = $state('');
	let next = $state('');
	let confirmPw = $state('');
	let submitting = $state(false);
	let error = $state<string | null>(null);
	let ok = $state(false);

	async function onSubmit(e: SubmitEvent) {
		e.preventDefault();
		error = null;
		ok = false;
		if (next !== confirmPw) {
			error = 'The new passwords do not match.';
			return;
		}
		submitting = true;
		try {
			await changePassword(current, next);
			ok = true;
			current = '';
			next = '';
			confirmPw = '';
		} catch (err) {
			error = errorMessage(err);
		} finally {
			submitting = false;
		}
	}

	async function onLogout() {
		await logout();
		await goto('/login');
	}

	// ── assistant profile ──────────────────────────────────────────────
	// What the agent reads about this user on every turn (see Skills/base.md,
	// "User profile context"): the profile's response style plus the free text.
	let profiles = $state<Profile[]>([]);
	let profileId = $state('');
	let customText = $state('');
	let profileLoading = $state(true);
	let profileSaving = $state(false);
	let profileError = $state<string | null>(null);
	let profileOk = $state(false);

	const profileOptions = $derived([
		{ value: '', label: 'No profile' },
		...profiles.map((p) => ({ value: p.id, label: p.displayName }))
	]);
	const selectedProfile = $derived(profiles.find((p) => p.id === profileId) ?? null);
	const customTextTooLong = $derived(customText.length > MAX_CUSTOM_PROFILE_TEXT);

	onMount(async () => {
		try {
			const [list, mine] = await Promise.all([listProfiles(), getMyProfile()]);
			profiles = [...list.items].sort((a, b) => a.displayOrder - b.displayOrder);
			profileId = mine.profileId ?? '';
			customText = mine.customProfileText ?? '';
		} catch (err) {
			profileError = errorMessage(err);
		} finally {
			profileLoading = false;
		}
	});

	async function onSaveProfile(e: SubmitEvent) {
		e.preventDefault();
		profileError = null;
		profileOk = false;
		if (customTextTooLong) {
			profileError = `Keep the text under ${MAX_CUSTOM_PROFILE_TEXT} characters.`;
			return;
		}
		profileSaving = true;
		try {
			const saved = await setMyProfile({ profileId: profileId || null, customProfileText: customText });
			profileId = saved.profileId ?? '';
			customText = saved.customProfileText ?? '';
			profileOk = true;
		} catch (err) {
			profileError = errorMessage(err);
		} finally {
			profileSaving = false;
		}
	}
</script>

<svelte:head><title>Account · Nashira</title></svelte:head>

<PageHeader
	title="Account"
	description={`Signed in as ${authStore.session?.username} (${authStore.session?.role}).`}
/>

<div class="grid max-w-5xl gap-4 lg:grid-cols-2 lg:items-start">
	<Card title="Assistant profile">
		<form class="space-y-4" onsubmit={onSaveProfile} aria-label="Assistant profile">
			<p class="text-sm text-surface-600-400">
				How the assistant talks to you. The profile sets the tone and depth of its answers;
				the text below is yours — your role, the tools you prefer, the language you want.
				It applies from your next message.
			</p>
			{#if profileError}<Alert tone="error">{profileError}</Alert>{/if}
			{#if profileOk}<Alert tone="success">Assistant profile saved.</Alert>{/if}
			<Select
				id="profile"
				label="Profile"
				options={profileOptions}
				bind:value={profileId}
				disabled={profileLoading}
			/>
			{#if selectedProfile}
				<div class="space-y-1 rounded-md border border-surface-200-800 px-3 py-2 text-sm">
					{#if selectedProfile.description}
						<div>{selectedProfile.description}</div>
					{/if}
					{#if selectedProfile.responseStyle}
						<div class="text-surface-600-400">
							<span class="font-medium">Response style:</span>
							{selectedProfile.responseStyle}
						</div>
					{/if}
					{#if selectedProfile.skills.length > 0}
						<div class="text-surface-600-400">
							<span class="font-medium">Skills:</span>
							{selectedProfile.skills.join(', ')}
						</div>
					{/if}
				</div>
			{/if}
			<Textarea
				id="custom-profile-text"
				label="About you"
				placeholder="Tell the assistant about yourself: your role, preferred tools, areas of expertise, the language you want answers in…"
				rows={4}
				bind:value={customText}
				disabled={profileLoading}
				maxlength={MAX_CUSTOM_PROFILE_TEXT}
				error={customTextTooLong ? `At most ${MAX_CUSTOM_PROFILE_TEXT} characters.` : ''}
				hint={`${customText.length}/${MAX_CUSTOM_PROFILE_TEXT}`}
			/>
			<div class="flex justify-end">
				<Button type="submit" variant="primary" loading={profileSaving} disabled={profileLoading}>
					Save profile
				</Button>
			</div>
		</form>
	</Card>

	<div class="space-y-4">
		<Card title="Change password">
			<form class="space-y-4" onsubmit={onSubmit} aria-label="Change password">
				{#if error}<Alert tone="error">{error}</Alert>{/if}
				{#if ok}<Alert tone="success">Password changed.</Alert>{/if}
				<Input
					id="current"
					label="Current password"
					type="password"
					autocomplete="current-password"
					bind:value={current}
					required
				/>
				<Input
					id="next"
					label="New password"
					type="password"
					autocomplete="new-password"
					bind:value={next}
					required
				/>
				<Input
					id="confirm"
					label="Confirm new password"
					type="password"
					autocomplete="new-password"
					bind:value={confirmPw}
					required
				/>
				<div class="flex justify-end">
					<Button type="submit" variant="primary" loading={submitting}>Change password</Button>
				</div>
			</form>
		</Card>

		<Card>
			<div class="flex items-center justify-between gap-3">
				<div class="text-sm text-surface-600-400">Sign out of this session.</div>
				<Button variant="secondary" onclick={onLogout}>Sign out</Button>
			</div>
		</Card>
	</div>
</div>
