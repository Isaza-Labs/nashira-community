<script lang="ts">
	import { untrack } from 'svelte';
	import { listUsers, type User } from '$lib/api/users.api';
	import {
		getRoleNavigationVisibility,
		getUserNavigationVisibility,
		updateRoleNavigationVisibility,
		updateUserNavigationVisibility,
		type NavigationVisibility
	} from '$lib/api/navigation-permissions.api';
	import { authStore } from '$lib/stores/auth.svelte';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';
	import { PAGES, canSee, type NavGroupId } from '$lib/nav/registry';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import {
		Alert,
		Badge,
		Button,
		ErrorState,
		PageHeader,
		Select,
		Spinner,
		toast
	} from '$lib/components/ui';
	import { Eye, EyeOff, RotateCcw } from 'lucide-svelte';

	type Setting = 'inherit' | 'visible' | 'hidden';
	type Mode = 'role' | 'user';

	const LOCKED_PAGE = '/admin/navigation-permissions';
	const GROUPS: { id: NavGroupId; label: string }[] = [
		{ id: 'main', label: 'Home' },
		{ id: 'agent', label: 'Intelligence' },
		{ id: 'build', label: 'Build' },
		{ id: 'connect', label: 'Integrate' },
		{ id: 'operate', label: 'Operate' },
		{ id: 'govern', label: 'Govern' },
		{ id: 'resources', label: 'Help' },
		{ id: 'personal', label: 'Personal' }
	];
	const SETTING_OPTIONS = [
		{ value: 'inherit', label: 'Use default' },
		{ value: 'visible', label: 'Visible' },
		{ value: 'hidden', label: 'Hidden' }
	];

	let mode = $state<Mode>('role');
	let role = $state('viewer');
	let users = $state<User[]>([]);
	let selectedUserId = $state('');
	let values = $state<Record<string, Setting>>({});
	let baseline = $state<Record<string, Setting>>({});
	let roleOverrides = $state<Record<string, boolean>>({});
	let loadingUsers = $state(true);
	let loading = $state(false);
	let saving = $state(false);
	let error = $state<unknown>(null);
	let request = 0;

	// Only destinations this deployment actually has. Administering visibility for a
	// page the deployment does not run would offer a switch that changes nothing: the
	// module decision wins over any stored permission, so the row would be a promise
	// the shell refuses to keep.
	const deployedPages = $derived(
		PAGES.filter((page) => moduleStore.availability.isEnabled(page.module))
	);

	const selectedUser = $derived(users.find((user) => user.id === selectedUserId) ?? null);
	const scopeRole = $derived(mode === 'role' ? role : (selectedUser?.role ?? 'viewer'));
	const userOptions = $derived(
		users.map((user) => ({ value: user.id, label: `${user.username} (${user.role})` }))
	);
	const dirtyKeys = $derived(
		deployedPages.filter((page) => values[page.href] !== baseline[page.href]).map((page) => page.href)
	);

	function toSettings(rows: NavigationVisibility[]): Record<string, Setting> {
		const stored = new Map(rows.map((row) => [row.pageKey, row.visible]));
		return Object.fromEntries(
			deployedPages.map((page) => [
				page.href,
				stored.has(page.href) ? (stored.get(page.href) ? 'visible' : 'hidden') : 'inherit'
			])
		);
	}

	function toVisibility(rows: NavigationVisibility[]): Record<string, boolean> {
		return Object.fromEntries(rows.map((row) => [row.pageKey, row.visible]));
	}

	async function loadUsers() {
		try {
			const result = await listUsers(200, 0);
			users = result.items;
			if (!selectedUserId && users.length > 0) selectedUserId = users[0].id;
		} catch (e) {
			error = e;
		} finally {
			loadingUsers = false;
		}
	}

	async function loadScope() {
		if (mode === 'user' && !selectedUser) return;
		const token = ++request;
		loading = true;
		error = null;
		try {
			if (mode === 'role') {
				const rows = await getRoleNavigationVisibility(role);
				if (token !== request) return;
				values = toSettings(rows);
				roleOverrides = {};
			} else {
				const [userRows, roleRows] = await Promise.all([
					getUserNavigationVisibility(selectedUser!.id),
					getRoleNavigationVisibility(selectedUser!.role)
				]);
				if (token !== request) return;
				values = toSettings(userRows);
				roleOverrides = toVisibility(roleRows);
			}
			baseline = { ...values };
		} catch (e) {
			if (token !== request) return;
			error = e;
		} finally {
			if (token === request) loading = false;
		}
	}

	$effect(() => {
		loadUsers();
	});

	$effect(() => {
		mode;
		role;
		selectedUserId;
		untrack(() => void loadScope());
	});

	function setGroup(group: NavGroupId, setting: Setting) {
		const next = { ...values };
		for (const page of deployedPages.filter((item) => item.group === group)) {
			if (page.href !== LOCKED_PAGE && canSee(scopeRole, page.minRole)) next[page.href] = setting;
		}
		values = next;
	}

	function isEffectivelyVisible(page: (typeof PAGES)[number]): boolean {
		if (page.href === LOCKED_PAGE) return true;
		if (!canSee(scopeRole, page.minRole)) return false;
		const setting = values[page.href];
		if (setting === 'visible') return true;
		if (setting === 'hidden') return false;
		return mode === 'user' ? roleOverrides[page.href] !== false : true;
	}

	async function save() {
		const targetMode = mode;
		const targetRole = role;
		const targetUserId = selectedUser?.id ?? '';
		const changes = dirtyKeys.map((pageKey) => ({
			pageKey,
			visible:
				values[pageKey] === 'inherit' ? null : values[pageKey] === 'visible'
		}));
		saving = true;
		try {
			const rows =
				targetMode === 'role'
					? await updateRoleNavigationVisibility(targetRole, changes)
					: await updateUserNavigationVisibility(targetUserId, changes);
			const stillOnSavedScope =
				mode === targetMode &&
				(targetMode === 'role' ? role === targetRole : selectedUserId === targetUserId);
			if (stillOnSavedScope) {
				values = toSettings(rows);
				baseline = { ...values };
			}
			if (authStore.session) {
				await navigationVisibilityStore.load(authStore.session.userId, true);
			}
			toast.success('Navigation permissions saved');
		} catch (e) {
			toast.fromError(e, "Couldn't save navigation permissions");
		} finally {
			saving = false;
		}
	}
</script>

<svelte:head><title>Navigation access · Nashira</title></svelte:head>

<PageHeader
	title="Navigation access"
	description="Choose which Nashira areas appear for each role, with optional exceptions for individual users."
/>

<div class="mb-5">
	<Alert tone="warning">
		This controls navigation and page visibility. API authorization still follows the user's system
		role, so making an item visible never grants additional backend privileges.
	</Alert>
</div>

<div class="mb-5 flex flex-wrap items-end gap-3">
	<div class="flex rounded-lg border border-surface-200-800 p-1" aria-label="Permission scope">
		<button
			type="button"
			onclick={() => (mode = 'role')}
			disabled={saving || dirtyKeys.length > 0}
			aria-pressed={mode === 'role'}
			class="rounded-md px-3 py-1.5 text-sm font-medium transition disabled:opacity-50 {mode === 'role'
				? 'bg-primary-500/15 text-primary-700-300'
				: 'text-surface-600-400 hover:text-surface-950-50'}"
		>
			By role
		</button>
		<button
			type="button"
			onclick={() => (mode = 'user')}
			disabled={saving || dirtyKeys.length > 0}
			aria-pressed={mode === 'user'}
			class="rounded-md px-3 py-1.5 text-sm font-medium transition disabled:opacity-50 {mode === 'user'
				? 'bg-primary-500/15 text-primary-700-300'
				: 'text-surface-600-400 hover:text-surface-950-50'}"
		>
			User exceptions
		</button>
	</div>

	<div class="min-w-56 flex-1 sm:max-w-sm">
		{#if mode === 'role'}
			<Select
				bind:value={role}
				label="Role"
				disabled={saving || dirtyKeys.length > 0}
				options={[
					{ value: 'viewer', label: 'Viewer' },
					{ value: 'operator', label: 'Operator' },
					{ value: 'admin', label: 'Administrator' }
				]}
			/>
		{:else}
			<Select
				bind:value={selectedUserId}
				label="User"
				options={userOptions}
				placeholder={loadingUsers ? 'Loading users…' : 'Select a user'}
				disabled={loadingUsers || saving || dirtyKeys.length > 0}
			/>
		{/if}
	</div>
</div>

{#if error}
	<ErrorState {error} onRetry={loadScope} />
{:else if loading || loadingUsers}
	<div class="ui-surface grid min-h-48 place-items-center" aria-label="Loading navigation permissions">
		<Spinner />
	</div>
{:else if mode === 'user' && !selectedUser}
	<div class="ui-surface px-4 py-12 text-center text-sm text-surface-600-400">
		There are no users to configure.
	</div>
{:else}
	<div class="space-y-4">
		{#each GROUPS as group (group.id)}
			{@const pages = deployedPages.filter((page) => page.group === group.id)}
			{#if pages.length > 0}
				<section class="ui-surface overflow-hidden">
					<div class="flex flex-wrap items-center justify-between gap-2 border-b border-surface-200-800 px-4 py-3">
						<div>
							<h2 class="text-sm font-semibold">{group.label}</h2>
							<p class="text-xs text-surface-600-400">{pages.length} navigation items</p>
						</div>
						<div class="flex gap-1">
							<Button size="sm" variant="ghost" onclick={() => setGroup(group.id, 'inherit')}>
								<RotateCcw size={14} /> Defaults
							</Button>
							<Button size="sm" variant="ghost" onclick={() => setGroup(group.id, 'visible')}>
								<Eye size={14} /> Show
							</Button>
							<Button size="sm" variant="ghost" onclick={() => setGroup(group.id, 'hidden')}>
								<EyeOff size={14} /> Hide
							</Button>
						</div>
					</div>

					<div class="divide-y divide-surface-200-800">
						{#each pages as item (item.href)}
							{@const locked = item.href === LOCKED_PAGE}
							{@const unavailable = !canSee(scopeRole, item.minRole)}
							<div class="grid gap-3 px-4 py-3 sm:grid-cols-[minmax(0,1fr)_8rem_10rem] sm:items-center">
								<div class="min-w-0">
									<div class="flex flex-wrap items-center gap-2">
										<span class="font-medium">{item.label}</span>
										<Badge tone={isEffectivelyVisible(item) ? 'success' : 'neutral'}>
											{isEffectivelyVisible(item) ? 'Visible' : 'Hidden'}
										</Badge>
									</div>
									<p class="mt-0.5 truncate text-xs text-surface-600-400" title={item.href}>
										{item.href} · {item.desc}
									</p>
								</div>
								<span class="text-xs text-surface-600-400">Minimum: {item.minRole}</span>
								<Select
									bind:value={values[item.href]}
									options={SETTING_OPTIONS}
									disabled={locked || unavailable}
									hint={locked ? 'Always available' : unavailable ? 'Blocked by role' : ''}
								/>
							</div>
						{/each}
					</div>
				</section>
			{/if}
		{/each}
	</div>

	<div class="sticky bottom-3 mt-5 flex items-center justify-between gap-3 rounded-lg border border-surface-200-800 bg-surface-50-950/95 px-4 py-3 shadow-lg backdrop-blur">
		<p class="text-sm text-surface-600-400">
			{dirtyKeys.length === 0 ? 'No unsaved changes' : `${dirtyKeys.length} unsaved change(s)`}
		</p>
		<div class="flex gap-2">
			<Button
				variant="secondary"
				onclick={() => (values = { ...baseline })}
				disabled={dirtyKeys.length === 0 || saving}
			>
				Discard
			</Button>
			<Button onclick={save} loading={saving} disabled={dirtyKeys.length === 0}>Save changes</Button>
		</div>
	</div>
{/if}
