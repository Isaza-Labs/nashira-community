<script lang="ts">
	import '../app.css';
	import { afterNavigate, goto } from '$app/navigation';
	import { page } from '$app/state';
	import { animate, prefersReducedMotion } from '$lib/anim';
	import { authStore } from '$lib/stores/auth.svelte';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { blockedReasonForPath } from '$lib/nav/registry';
	import { me } from '$lib/api/auth.api';
	// Imported for its side effect: the engine's constructor re-applies the
	// browser's saved theme before first paint settles, same pattern as prefs.
	import '$lib/stores/theme.svelte';
	import Sidebar from '$lib/components/layout/Sidebar.svelte';
	import CommandPalette from '$lib/components/layout/CommandPalette.svelte';
	import UnavailableNotice from '$lib/components/layout/UnavailableNotice.svelte';
	import { Alert, Button } from '$lib/components/ui';
	import Toaster from '$lib/components/ui/Toaster.svelte';
	import ConfirmHost from '$lib/components/ui/ConfirmHost.svelte';
	import { Menu } from 'lucide-svelte';

	let { children } = $props();

	// Routes that unauthenticated visitors may enter without the app shell. /link
	// carries a one-time `?token=` from a chat message: bouncing it through the
	// guard would drop the query on the way to /login, so the page preserves the
	// full URL across that round trip. Once signed in, it joins the module guard.
	const PUBLIC_PATHS = ['/login', '/link'];

	const isLogin = $derived(page.url.pathname === '/login');
	const isPublic = $derived(PUBLIC_PATHS.includes(page.url.pathname));
	// Chat is an app-like, full-height surface: it goes edge-to-edge against the
	// sidebar and manages its own scrolling. Every other page is a document: it
	// gets the centered, measured reading container.
	const isChat = $derived(
		page.url.pathname === '/' || page.url.pathname === '/chat' || page.url.pathname.startsWith('/chat/')
	);
	let mobileOpen = $state(false);
	let mainEl = $state<HTMLElement | null>(null);
	let contentEl = $state<HTMLElement | null>(null);

	// Navigation feel. Two things happen on a route change:
	//
	// 1. The scroll container resets. SvelteKit only manages *window* scroll, and
	//    our scroller is <main> — without this, a new page opens wherever the last
	//    one was scrolled to. Back/forward (popstate) is left alone so returning
	//    lands roughly where you were.
	// 2. The incoming content rises in (opacity + 8px). Only between *different*
	//    routes: moving between chat conversations or paginating in place must
	//    not replay an entrance. animate() overrides inline styles while running
	//    and releases them after, so it composes with anything Tailwind set.
	afterNavigate(({ from, to, type }) => {
		// `from.url` (not just `from`) can be null: on a CSR start the router fires the
		// initial `enter` navigation before `current.url` exists, so `from` is an object
		// whose url is null — `from?.url.pathname` threw on every production page load.
		if (type !== 'popstate' && from?.url?.pathname !== to?.url?.pathname) {
			mainEl?.scrollTo(0, 0);
		}
		if (!contentEl || prefersReducedMotion()) return;
		if (!from || from.route.id === to?.route.id) return;
		animate(contentEl, {
			opacity: [0, 1],
			translateY: [8, 0],
			duration: 240,
			ease: 'outCubic'
		});
	});

	// Client-side route guard (defense in depth; the API enforces auth too).
	$effect(() => {
		if (!authStore.isAuthenticated && !isPublic) {
			const target = page.url.pathname + page.url.search;
			goto(`/login?redirect=${encodeURIComponent(target)}`);
		} else if (authStore.isAuthenticated && isLogin) {
			const redirect = page.url.searchParams.get('redirect');
			goto(redirect && redirect.startsWith('/') ? redirect : '/');
		}
	});

	// The session hydrates from localStorage, which anyone can edit — so the role
	// the whole UI gates on (sidebar, palette, RoleGate) is only trusted after
	// /auth/me confirms it. Once per signed-in user; a failed check just leaves
	// the local value, since the API refuses forbidden calls regardless.
	let identityCheckedFor: string | null = null;
	$effect(() => {
		const session = authStore.session;
		if (!session || identityCheckedFor === session.userId) return;
		identityCheckedFor = session.userId;
		me().then(
			(m) => authStore.reconcile({ username: m.username, role: m.role }),
			() => {}
		);
	});

	$effect(() => {
		const userId = authStore.session?.userId;
		if (!userId) {
			navigationVisibilityStore.clear();
			moduleStore.clear();
			return;
		}
		void navigationVisibilityStore.load(userId);
		// The manifest is authenticated, so it can only be asked for once there is a
		// session. Every navigation surface reads it; until it answers the store holds
		// the fail-closed value, which is why `navigationReady` waits for both.
		void moduleStore.load(userId);
	});

	const navigationReady = $derived(
		!!authStore.session &&
			navigationVisibilityStore.isReadyFor(authStore.session.userId) &&
			moduleStore.loadedUserId === authStore.session.userId
	);
	// One decision, read by the guard and by what replaces the page: the shell must
	// never render a page it would then explain away, and must never explain away a
	// refusal with the wrong reason.
	const blockedReason = $derived(
		blockedReasonForPath(
			page.url.pathname,
			authStore.session?.role,
			moduleStore.availability,
			navigationVisibilityStore.visibility
		)
	);
	const canViewCurrentPath = $derived(blockedReason === null);
</script>

{#if isLogin || (page.url.pathname === '/link' && !authStore.isAuthenticated)}
	<div class="ui-app grid min-h-screen place-items-center px-4 text-surface-950-50">
		{@render children?.()}
	</div>
{:else if authStore.isAuthenticated && !navigationReady}
	<div class="ui-app grid min-h-screen place-items-center text-surface-950-50">
		<span class="text-sm text-surface-600-400">Loading workspace…</span>
	</div>
{:else if authStore.isAuthenticated && page.url.pathname === '/link'}
	<div class="ui-app grid min-h-screen place-items-center px-4 text-surface-950-50">
		{#if canViewCurrentPath}
			{@render children?.()}
		{:else}
			<UnavailableNotice reason={blockedReason ?? 'permission'} />
		{/if}
	</div>
{:else if authStore.isAuthenticated}
	<div class="ui-app flex h-screen overflow-hidden text-surface-950-50">
		{#if mobileOpen}
			<button
				type="button"
				aria-label="Close navigation"
				class="fixed inset-0 z-30 bg-black/50 backdrop-blur-sm md:hidden"
				onclick={() => (mobileOpen = false)}
			></button>
		{/if}

		<!-- Narrower and a touch darker than the panels beside it, so the shell reads
		     as nav → content instead of two equal columns. -->
		<aside
			class="fixed inset-y-0 left-0 z-40 w-56 shrink-0 border-r border-surface-200-800/60 bg-surface-100-950/70 backdrop-blur-xl transition-transform duration-200 md:static md:translate-x-0 {mobileOpen
				? 'translate-x-0'
				: '-translate-x-full'}"
			aria-label="Sidebar"
		>
			<Sidebar onnavigate={() => (mobileOpen = false)} />
		</aside>

		<main bind:this={mainEl} class="relative min-w-0 flex-1 {isChat ? 'overflow-hidden' : 'overflow-y-auto'}">
			<!-- The manifest could not be read, so the shell is showing core and nothing
			     else. Said out loud and with a way back: a UI that quietly shrank to a
			     quarter of its pages, with no explanation, reads as data loss. -->
			{#if moduleStore.status === 'error'}
				<div class="px-4 pt-4 sm:px-6 lg:px-8">
					<Alert tone="warning" title="Showing core features only">
						<div class="flex flex-wrap items-center gap-3">
							<span>
								This deployment's module list could not be loaded, so pages beyond the
								core are hidden until it is.
							</span>
							<Button
								size="sm"
								variant="ghost"
								onclick={() => authStore.session && void moduleStore.retry(authStore.session.userId)}
							>
								Retry
							</Button>
						</div>
					</Alert>
				</div>
			{/if}
			<button
				type="button"
				onclick={() => (mobileOpen = true)}
				aria-label="Open navigation"
				class="fixed left-3 top-3 z-20 inline-flex h-10 w-10 items-center justify-center rounded-lg border border-surface-200-800 bg-surface-50-950/80 backdrop-blur md:hidden"
			>
				<Menu size={18} />
			</button>
			{#if isChat}
				<div bind:this={contentEl} class="h-full">
					{#if canViewCurrentPath}
						{@render children?.()}
					{:else}
						<div class="grid h-full place-items-center px-4">
							<UnavailableNotice reason={blockedReason ?? 'permission'} />
						</div>
					{/if}
				</div>
			{:else}
				<div bind:this={contentEl} class="mx-auto w-full max-w-6xl px-4 py-6 sm:px-6 lg:px-8">
					{#if canViewCurrentPath}
						{@render children?.()}
					{:else}
						<div class="ui-surface px-4 py-16">
							<UnavailableNotice reason={blockedReason ?? 'permission'} />
						</div>
					{/if}
				</div>
			{/if}
		</main>
	</div>
{:else}
	<!-- Redirecting to /login — render a neutral placeholder, never protected content. -->
	<div class="ui-app grid min-h-screen place-items-center text-surface-950-50">
		<span class="text-sm text-surface-600-400">Redirecting…</span>
	</div>
{/if}

<!-- App-wide singletons: transient toasts, the confirm() dialog host, and the
     Cmd/Ctrl+K palette (only meaningful once signed in). -->
{#if authStore.isAuthenticated && !isPublic}
	<CommandPalette />
{/if}
<Toaster />
<ConfirmHost />
