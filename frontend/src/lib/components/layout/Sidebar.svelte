<script lang="ts">
	import { page } from '$app/state';
	import { onMount } from 'svelte';
	import { slide } from 'svelte/transition';
	import { animate, stagger, prefersReducedMotion } from '$lib/anim';
	import { authStore } from '$lib/stores/auth.svelte';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';
	import ModeToggle from '$lib/components/ui/ModeToggle.svelte';
	import Logo from '$lib/components/ui/Logo.svelte';
	import { sidebarFor, footerPages, isPathActive } from '$lib/nav/registry';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import UserMenu from './UserMenu.svelte';
	import { ChevronDown } from 'lucide-svelte';

	// Left navigation. The tree, the roles and the labels all come from
	// $lib/nav/registry — this file is presentation. Groups are collapsible
	// because seven groups of two beats one list of twenty, but the group holding
	// the current page always opens regardless of stored state: navigation must
	// never hide where you are.
	let { onnavigate }: { onnavigate?: () => void } = $props();

	const COLLAPSE_KEY = 'nashira:nav-collapsed';

	const role = $derived(authStore.session?.role ?? '');
	const groups = $derived(
		sidebarFor(role, moduleStore.availability, navigationVisibilityStore.visibility)
	);
	const footer = $derived(
		footerPages(role, moduleStore.availability, navigationVisibilityStore.visibility)
	);
	const homeHref = $derived(groups[0]?.items[0]?.href ?? footer[0]?.href ?? '#');

	let collapsed = $state<Record<string, boolean>>(readCollapsed());

	function readCollapsed(): Record<string, boolean> {
		if (typeof localStorage === 'undefined') return {};
		try {
			const raw = localStorage.getItem(COLLAPSE_KEY);
			const parsed = raw ? JSON.parse(raw) : {};
			return typeof parsed === 'object' && parsed !== null ? parsed : {};
		} catch {
			return {};
		}
	}

	function toggle(id: string) {
		collapsed = { ...collapsed, [id]: !collapsed[id] };
		try {
			localStorage.setItem(COLLAPSE_KEY, JSON.stringify(collapsed));
		} catch {
			/* storage unavailable — the collapse still works for this session */
		}
	}

	function itemActive(owns: string[]): boolean {
		return owns.some((href) => isPathActive(page.url.pathname, href));
	}

	function groupHasActive(items: { owns: string[] }[]): boolean {
		return items.some((i) => itemActive(i.owns));
	}

	// One orchestrated entrance on mount — the same cascade Flow Weaver's nav
	// does. The sidebar survives navigation, so this plays once per full load,
	// not on every page change.
	let navEl = $state<HTMLElement | null>(null);
	onMount(() => {
		if (!navEl || prefersReducedMotion()) return;
		const items = navEl.querySelectorAll<HTMLElement>('.nav-item');
		animate(items, {
			opacity: [0, 1],
			translateX: [-8, 0],
			duration: 320,
			ease: 'outCubic',
			delay: stagger(28)
		});
	});
</script>

<div class="flex h-full flex-col">
	<a
		href={homeHref}
		onclick={onnavigate}
		aria-label="Nashira — go to first available area"
		class="flex h-14 shrink-0 items-center justify-center border-b border-surface-200-800/60 px-4"
	>
		<Logo size={19} />
	</a>

	<nav bind:this={navEl} class="min-h-0 flex-1 space-y-0.5 overflow-y-auto px-2 py-3" aria-label="Primary">
		{#each groups as group (group.id)}
			{@const hasActive = groupHasActive(group.items)}
			{@const open = hasActive || !collapsed[group.id]}

			{#if group.label}
				<button
					type="button"
					onclick={() => toggle(group.id)}
					aria-expanded={open}
					class="flex w-full items-center gap-1 rounded px-2 pb-1 pt-3 text-[10px] font-semibold uppercase tracking-wider text-surface-600-400/80 transition hover:text-surface-800-200"
				>
					<ChevronDown
						size={11}
						class="shrink-0 transition-transform duration-150 {open ? '' : '-rotate-90'}"
					/>
					{group.label}
				</button>
			{/if}

			{#if open}
				<!-- The wrapper exists so a collapsing group folds shut instead of
				     vanishing — it re-declares the nav's item rhythm inside itself. -->
				<div class="space-y-0.5" transition:slide={{ duration: 150 }}>
					{#each group.items as item (item.href)}
						{@const active = itemActive(item.owns)}
						<a
							href={item.href}
							onclick={onnavigate}
							aria-current={active ? 'page' : undefined}
							class="nav-item group relative flex h-9 items-center gap-2.5 rounded-md px-2.5 text-sm transition {active
								? 'bg-primary-500/12 font-medium text-primary-700-300 ring-1 ring-inset ring-primary-500/25'
								: 'text-surface-600-400 hover:bg-surface-100-900 hover:text-surface-950-50'}"
						>
							{#if active}
								<span class="absolute bottom-2 left-0 top-2 w-0.5 rounded-r bg-primary-400"></span>
							{/if}
							<item.icon
								size={16}
								class={active
									? 'text-primary-700-300'
									: 'text-surface-600-400/70 group-hover:text-surface-700-300'}
							/>
							{item.label}
						</a>
					{/each}
				</div>
			{/if}
		{/each}
	</nav>

	<div class="shrink-0 border-t border-surface-200-800/60 p-2">
		<div class="mb-1 flex items-center gap-0.5 px-1">
			<ModeToggle />
			{#each footer as item (item.href)}
				{@const active = isPathActive(page.url.pathname, item.href)}
				<a
					href={item.href}
					onclick={onnavigate}
					aria-label={item.label}
					title={item.label}
					aria-current={active ? 'page' : undefined}
					class="inline-flex h-9 w-9 items-center justify-center rounded-lg transition {active
						? 'bg-primary-500/12 text-primary-700-300'
						: 'text-surface-600-400 hover:bg-surface-100-900 hover:text-surface-950-50'}"
				>
					<item.icon size={17} />
				</a>
			{/each}
		</div>
		<UserMenu {onnavigate} />
	</div>
</div>
