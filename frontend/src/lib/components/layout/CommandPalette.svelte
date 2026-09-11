<script lang="ts">
	import { goto } from '$app/navigation';
	import { authStore } from '$lib/stores/auth.svelte';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';
	import Kbd from '$lib/components/ui/Kbd.svelte';
	import { paletteEntries, type PaletteEntry } from '$lib/nav/registry';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { Search, CornerDownLeft, Plus } from 'lucide-svelte';

	// Cmd/Ctrl+K jump-to-anywhere, plus the create actions. A console with ~30
	// destinations is faster to drive from the keyboard than from the sidebar, and
	// someone who opens the palette to *do* something should not scroll past every
	// destination to find it — so actions sort first.
	//
	// The destination list comes from $lib/nav/registry: a page that exists in the
	// sidebar is in the palette by construction, and neither can be forgotten.
	const isAdmin = $derived(authStore.session?.role === 'admin');
	const available = $derived(
		paletteEntries(
			authStore.session?.role,
			moduleStore.availability,
			navigationVisibilityStore.visibility
		)
	);

	let open = $state(false);
	let query = $state('');
	let active = $state(0);
	let input = $state<HTMLInputElement | undefined>(undefined);

	// `g` then a letter jumps without opening the palette. Only armed outside text
	// entry, and only for a moment — a stray `g` must not swallow the next key.
	const SHORTCUTS: Record<string, string> = {
		c: '/chat',
		o: '/overview',
		d: '/devices',
		w: '/workflows',
		s: '/admin/snippets',
		i: '/admin/integrations',
		k: '/knowledge',
		r: '/reports',
		h: '/docs'
	};
	const CHORD_MS = 1200;
	let chordArmed = $state(false);
	let chordTimer: ReturnType<typeof setTimeout> | undefined;

	const results = $derived.by(() => {
		const q = query.trim().toLowerCase();
		if (!q) return available;
		return available.filter((e) => `${e.label} ${e.group} ${e.keywords}`.toLowerCase().includes(q));
	});

	// Group headings are rendered by comparing against the previous row, so the
	// list stays a single flat sequence for arrow-key navigation.
	function isFirstOfGroup(i: number): boolean {
		return i === 0 || results[i - 1].group !== results[i].group;
	}

	function show() {
		query = '';
		active = 0;
		open = true;
		queueMicrotask(() => input?.focus());
	}

	function disarmChord() {
		chordArmed = false;
		if (chordTimer) clearTimeout(chordTimer);
	}

	function isTyping(target: EventTarget | null): boolean {
		const el = target as HTMLElement | null;
		if (!el) return false;
		const tag = el.tagName;
		return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
	}

	function onWindowKeydown(e: KeyboardEvent) {
		if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
			e.preventDefault();
			open ? (open = false) : show();
			return;
		}
		if (e.key === 'Escape' && open) {
			open = false;
			return;
		}
		if (open || e.metaKey || e.ctrlKey || e.altKey || isTyping(e.target)) return;

		if (chordArmed) {
			const href = SHORTCUTS[e.key.toLowerCase()];
			disarmChord();
			if (href && available.some((entry) => entry.href.split('?')[0] === href)) {
				e.preventDefault();
				goto(href);
			}
			return;
		}
		if (e.key.toLowerCase() === 'g') {
			chordArmed = true;
			chordTimer = setTimeout(disarmChord, CHORD_MS);
		}
	}

	function onListKeydown(e: KeyboardEvent) {
		if (results.length === 0) return;
		if (e.key === 'ArrowDown') {
			e.preventDefault();
			active = (active + 1) % results.length;
		} else if (e.key === 'ArrowUp') {
			e.preventDefault();
			active = (active - 1 + results.length) % results.length;
		} else if (e.key === 'Enter') {
			e.preventDefault();
			select(results[active]);
		}
	}

	function select(entry: PaletteEntry | undefined) {
		if (!entry) return;
		open = false;
		goto(entry.href);
	}

	// Keep the highlight in range as the result set shrinks under typing.
	$effect(() => {
		if (active >= results.length) active = 0;
	});
</script>

<svelte:window onkeydown={onWindowKeydown} />

{#if chordArmed && !open}
	<div
		class="ui-surface chord-hint fixed bottom-4 left-1/2 z-[72] px-3 py-1.5 text-xs text-surface-700-300"
		role="status"
	>
		<Kbd>g</Kbd> … press d · w · s · i · k · r · o · c · h
	</div>
{/if}

{#if open}
	<!-- Scrim. Clicking anywhere outside dismisses. -->
	<button
		type="button"
		aria-label="Close command palette"
		class="animate-in fade-in fixed inset-0 z-[70] cursor-default bg-black/50 backdrop-blur-sm"
		onclick={() => (open = false)}
	></button>

	<div
		class="ui-surface palette-panel fixed left-1/2 top-[15vh] z-[71] w-[min(560px,calc(100vw-2rem))] overflow-hidden p-0"
		role="dialog"
		aria-modal="true"
		aria-label="Command palette"
	>
		<div class="flex items-center gap-2 border-b border-surface-200-800 px-3">
			<Search size={15} class="shrink-0 text-surface-600-400" />
			<input
				bind:this={input}
				bind:value={query}
				onkeydown={onListKeydown}
				placeholder="Jump to, or create…"
				aria-label="Jump to, or create"
				class="h-11 flex-1 bg-transparent text-sm outline-none placeholder:text-surface-600-400"
			/>
			<Kbd>esc</Kbd>
		</div>

		{#if results.length === 0}
			<p class="px-3 py-6 text-center text-sm text-surface-600-400">No matches.</p>
		{:else}
			<ul class="max-h-[50vh] overflow-y-auto p-1.5">
				{#each results as r, i (r.href + r.label)}
					{#if isFirstOfGroup(i)}
						<li
							class="px-2 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-wider text-surface-600-400/80"
						>
							{r.group}
						</li>
					{/if}
					<li>
						<button
							type="button"
							onclick={() => select(r)}
							onmouseenter={() => (active = i)}
							class="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm transition {i ===
							active
								? 'bg-primary-500/12 text-primary-700-300'
								: 'text-surface-800-200 hover:bg-surface-100-900'}"
						>
							{#if r.isAction}
								<Plus size={12} class="shrink-0 text-surface-600-400" />
							{/if}
							<span class="flex-1 truncate">{r.label}</span>
							{#if !r.isAction}
								<code class="text-[11px] text-surface-600-400">{r.href}</code>
							{/if}
							{#if i === active}<CornerDownLeft size={12} class="shrink-0" />{/if}
						</button>
					</li>
				{/each}
			</ul>
		{/if}

		<div
			class="flex items-center gap-3 border-t border-surface-200-800 px-3 py-1.5 text-[11px] text-surface-600-400"
		>
			<span><Kbd>↑</Kbd><Kbd>↓</Kbd> move</span>
			<span><Kbd>↵</Kbd> open</span>
			<span class="ml-auto"><Kbd>g</Kbd> then a letter jumps directly</span>
		</div>
	</div>
{/if}

<style>
	/* Both boxes centre with translateX(-50%), so their entrances must carry
	   that translation through every frame — a bare `animate-in` would drop it
	   mid-flight and snap the box left. The app-wide reduced-motion rule zeroes
	   these like any other animation. */
	.palette-panel {
		animation: palette-in 180ms cubic-bezier(0.4, 0, 0.2, 1);
		transform: translateX(-50%);
	}
	@keyframes palette-in {
		from {
			opacity: 0;
			transform: translate(-50%, -6px) scale(0.98);
		}
		to {
			opacity: 1;
			transform: translate(-50%, 0) scale(1);
		}
	}
	.chord-hint {
		animation: chord-in 160ms cubic-bezier(0.4, 0, 0.2, 1);
		transform: translateX(-50%);
	}
	@keyframes chord-in {
		from {
			opacity: 0;
			transform: translate(-50%, 4px);
		}
		to {
			opacity: 1;
			transform: translate(-50%, 0);
		}
	}
</style>
