<script lang="ts">
	// The identity chip at the foot of the sidebar, and the menu it opens.
	//
	// It used to be a plain link to /account. That made the word under your name — your
	// role, "Admin" — look like a way into the admin panel, which it never was: people
	// clicked it expecting the dashboard and landed on their own profile. A menu says
	// what each destination is instead of making the label carry two meanings.
	import { tick } from 'svelte';
	import { goto } from '$app/navigation';
	import { authStore } from '$lib/stores/auth.svelte';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';
	import { canViewPath } from '$lib/nav/registry';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { logout } from '$lib/api/auth.api';
	import { Gauge, CircleUser, LogOut } from 'lucide-svelte';

	let { onnavigate }: { onnavigate?: () => void } = $props();

	let open = $state(false);
	let wrapper = $state<HTMLDivElement | null>(null);
	let trigger = $state<HTMLButtonElement | null>(null);
	let menu = $state<HTMLDivElement | null>(null);

	const session = $derived(authStore.session);
	const isAdmin = $derived(session?.role === 'admin');
	const canViewAccount = $derived(
		canViewPath(
			'/account',
			session?.role,
			moduleStore.availability,
			navigationVisibilityStore.visibility
		)
	);

	async function openMenu() {
		open = true;
		await tick();
		menu?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
	}

	function closeMenu(refocus = true) {
		open = false;
		if (refocus) trigger?.focus();
	}

	function choose() {
		closeMenu(false);
		onnavigate?.();
	}

	async function onLogout() {
		closeMenu(false);
		await logout();
		await goto('/login');
	}

	// Pointerdown rather than click: the menu must be gone before whatever was clicked
	// gets it. The wrapper contains the trigger, so pressing the trigger while open
	// counts as inside and is left to the button's own handler to toggle — otherwise the
	// two would fight and the menu would never close on a second press.
	$effect(() => {
		if (!open) return;
		const onPointerDown = (e: PointerEvent) => {
			if (!wrapper?.contains(e.target as Node)) closeMenu(false);
		};
		document.addEventListener('pointerdown', onPointerDown, true);
		return () => document.removeEventListener('pointerdown', onPointerDown, true);
	});

	// Escape closes and hands focus back; the arrows rove the items and wrap around.
	function onMenuKeydown(e: KeyboardEvent) {
		if (e.key === 'Escape') {
			e.preventDefault();
			closeMenu();
			return;
		}
		if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
		e.preventDefault();
		const items = Array.from(menu?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
		if (items.length === 0) return;
		const i = items.indexOf(document.activeElement as HTMLElement);
		const next = e.key === 'ArrowDown' ? (i + 1) % items.length : (i - 1 + items.length) % items.length;
		items[next].focus();
	}

	const ITEM =
		'flex w-full items-center gap-2.5 px-3 py-2 text-left text-sm text-surface-700-300 transition hover:bg-surface-200-800/60 hover:text-surface-950-50 focus:bg-surface-200-800/60 focus:outline-none';
</script>

<div class="relative" bind:this={wrapper}>
	<button
		bind:this={trigger}
		type="button"
		onclick={() => (open ? closeMenu(false) : openMenu())}
		aria-haspopup="menu"
		aria-expanded={open}
		aria-label="Open user menu"
		class="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left transition hover:bg-surface-100-900 {open
			? 'bg-surface-100-900'
			: ''}"
	>
		<div
			class="grid h-8 w-8 shrink-0 place-items-center rounded-full bg-primary-500/15 text-xs font-semibold uppercase text-primary-700-300"
		>
			{(session?.username ?? '?').slice(0, 2)}
		</div>
		<div class="min-w-0">
			<div class="truncate text-sm font-medium text-surface-900-100">{session?.username}</div>
			<div class="truncate text-[11px] capitalize text-surface-600-400">{session?.role ?? ''}</div>
		</div>
	</button>

	{#if open && session}
		<div
			bind:this={menu}
			role="menu"
			tabindex="-1"
			onkeydown={onMenuKeydown}
			class="animate-in slide-up absolute bottom-full left-0 z-50 mb-2 w-56 overflow-hidden rounded-lg border border-surface-300-700 bg-surface-50-950 py-1 shadow-2xl shadow-black/30"
		>
			{#if isAdmin}
				<a href="/admin" role="menuitem" onclick={choose} class={ITEM}>
					<Gauge size={14} class="shrink-0 text-primary-600-400" />
					<span>Admin dashboard</span>
				</a>
			{/if}

			{#if canViewAccount}
				<a href="/account" role="menuitem" onclick={choose} class={ITEM}>
					<CircleUser size={14} class="shrink-0 text-surface-600-400" />
					<span>Account and password</span>
				</a>
			{/if}

			<div class="my-1 border-t border-surface-200-800/60"></div>

			<button type="button" role="menuitem" onclick={onLogout} class={ITEM}>
				<LogOut size={14} class="shrink-0 text-surface-600-400" />
				<span>Sign out</span>
			</button>
		</div>
	{/if}
</div>
