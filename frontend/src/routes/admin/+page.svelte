<script lang="ts">
	// The admin dashboard, laid out after flow-weaver's: queue depth, the admin tools,
	// run activity beside what is failing, and sign-ins.
	//
	// It used to be an index of every destination in the product, which put Git and
	// Knowledge on a page about running the platform. The tools section below is a
	// curated list of admin destinations instead — the sidebar is still the map.
	import { untrack } from 'svelte';
	import {
		getRunMetrics,
		getAuthMetrics,
		getQueueMetrics,
		type RunMetrics,
		type AuthMetrics,
		type QueueMetrics
	} from '$lib/api/metrics.api';
	import {
		PageHeader,
		Card,
		StatCard,
		StackedBarChart,
		Select,
		Button,
		Alert,
		Spinner,
		ErrorState,
		toast,
		type BarBucket,
		type BarSeries
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { authStore } from '$lib/stores/auth.svelte';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';
	import { canViewPath } from '$lib/nav/registry';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import {
		RefreshCw,
		Pause,
		Play,
		Clock,
		Activity,
		CheckCircle2,
		AlertTriangle,
		Users,
		ShieldAlert,
		MessagesSquare,
		Radar,
		FileText,
		Gauge,
		Lock,
		SlidersHorizontal,
		Palette,
		ArrowUpRight,
		BarChart3,
		TrendingDown,
		LogIn,
		PanelLeft
	} from 'lucide-svelte';

	const isAdmin = $derived(authStore.session?.role === 'admin');

	let days = $state('7');
	let runs = $state<RunMetrics | null>(null);
	let auth = $state<AuthMetrics | null>(null);
	let queue = $state<QueueMetrics | null>(null);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let lastRefreshed = $state<Date | null>(null);

	// Polling rather than a stream: these are whole-day aggregates, so a socket would be
	// a lot of machinery to deliver a number that changes once a minute at most.
	// Pausable, because a panel that redraws while you are reading it is worse than a
	// slightly stale one.
	const REFRESH_MS = 30_000;
	let autoRefresh = $state(true);

	// The interval, the window selector and the button can all fire while a load is
	// still open. Refusing to start a second one would silently drop a window change
	// made mid-refresh, so overlapping loads are allowed and only the newest applies —
	// otherwise a slow 90-day response could land after a fast 7-day one and repaint
	// the chart with the window nobody asked for.
	let seq = 0;

	// The numbers on this page come from the administrative metrics API, which belongs
	// to observability. The hub itself is core and stays reachable without it — an
	// administrator locked out of the hub has no way back into a deployment's settings
	// — so with observability off the panels are absent and the tools remain.
	const hasMetrics = $derived(moduleStore.isEnabled('observability'));

	async function load() {
		if (!hasMetrics) {
			loading = false;
			return;
		}
		const mine = ++seq;
		error = null;
		try {
			const windowDays = Number.parseInt(days, 10) || 7;
			const [r, a, q] = await Promise.all([
				getRunMetrics(windowDays),
				getAuthMetrics(windowDays),
				getQueueMetrics()
			]);
			if (mine !== seq) return;
			runs = r;
			auth = a;
			queue = q;
			lastRefreshed = new Date();
		} catch (e) {
			if (mine !== seq) return;
			// Only the first failure blanks the page. Once there is something on screen, a
			// transient failure should not replace it with an error — the numbers are
			// stale, not wrong, and the timestamp below says how stale.
			if (!runs) error = e;
			else toast.fromError(e, "Couldn't refresh the metrics");
		} finally {
			if (mine === seq) loading = false;
		}
	}

	$effect(() => {
		if (!isAdmin || !hasMetrics) return;
		days;
		untrack(() => load());
	});

	$effect(() => {
		if (!isAdmin || !autoRefresh || !hasMetrics) return;
		const id = setInterval(() => {
			// A background tab polling every thirty seconds all afternoon is work nobody
			// is looking at; the next foreground tick catches up anyway.
			if (document.hidden) return;
			untrack(() => load());
		}, REFRESH_MS);
		return () => clearInterval(id);
	});

	// ── Chart shapes ──────────────────────────────────────────────────────
	//
	// Completed sits at the base so failures stack visibly on top: the eye reads a solid
	// foundation with the bad news sticking out, instead of comparing two similar blocks.
	const RUN_SERIES: BarSeries[] = [
		{ key: 'completed', label: 'Completed', class: 'bg-success-500' },
		{ key: 'failed', label: 'Failed', class: 'bg-error-500 dark:bg-error-400' },
		{ key: 'running', label: 'Running', class: 'bg-primary-500 dark:bg-primary-400' }
	];

	const AUTH_SERIES: BarSeries[] = [
		{ key: 'login_success', label: 'Signed in', class: 'bg-success-500' },
		{ key: 'login_failure', label: 'Failed', class: 'bg-warning-500' },
		{ key: 'lockout', label: 'Lockouts', class: 'bg-error-500 dark:bg-error-400' }
	];

	// The calendar date the server sent, taken literally.
	//
	// The buckets are UTC days — the server groups on `StartedAt.Date` and emits UTC
	// midnight — so handing the timestamp to `new Date()` and formatting it locally
	// shifts every label back a day for anyone west of Greenwich. Reading the date part
	// and formatting in UTC labels each bar with the day it actually counts, whatever
	// offset the serializer does or does not attach.
	function toBuckets(series: { date: string; counts: Record<string, number> }[]): BarBucket[] {
		return series.map((b) => {
			const [y, m, d] = b.date.slice(0, 10).split('-').map(Number);
			return {
				label: new Date(Date.UTC(y, m - 1, d)).toLocaleDateString(undefined, {
					day: '2-digit',
					month: 'short',
					timeZone: 'UTC'
				}),
				counts: b.counts
			};
		});
	}

	const runBuckets = $derived(runs ? toBuckets(runs.series) : []);
	const authBuckets = $derived(auth ? toBuckets(auth.series) : []);

	const totalRuns = $derived(
		runBuckets.reduce((sum, b) => sum + Object.values(b.counts).reduce((a, c) => a + c, 0), 0)
	);

	// The number that actually needs somebody: a failed run whose changes were reversed
	// is contained, one that ended `failed` left the world half-changed.
	const leftChanged = $derived(runs?.finalStates.failed ?? 0);
	const rolledBack = $derived(runs?.finalStates.rolled_back ?? 0);

	function ago(seconds: number): string {
		if (seconds < 60) return `${seconds}s`;
		if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
		return `${Math.floor(seconds / 3600)}h`;
	}

	// The admin destinations, and only those. Deliberately hand-written rather than
	// derived from the nav registry: the registry knows every page in the product, and
	// deriving from it is exactly how Git and Knowledge ended up on this page.
	const TOOLS = [
		{
			href: '/admin/users',
			label: 'Users',
			icon: Users,
			accent: 'bg-primary-500/10 text-primary-600-400 ring-primary-500/25',
			desc: 'Create accounts, assign roles (admin / operator / viewer), lock access.'
		},
		{
			href: '/admin/audit',
			label: 'Audit log',
			icon: ShieldAlert,
			accent: 'bg-warning-500/10 text-warning-600-400 ring-warning-500/25',
			desc: 'Hash-chained trail of every mutation, plus the sign-in log.'
		},
		{
			href: '/admin/traces',
			label: 'Traces',
			icon: Radar,
			accent: 'bg-primary-500/10 text-primary-600-400 ring-primary-500/25',
			desc: 'What the platform is doing, live — and what is stuck. For debugging.'
		},
		{
			href: '/admin/sessions',
			label: 'Sessions',
			icon: MessagesSquare,
			accent: 'bg-primary-500/10 text-primary-600-400 ring-primary-500/25',
			desc: 'Every agent conversation and the tool calls behind it. For debugging.'
		},
		{
			href: '/reports',
			label: 'Reports',
			icon: FileText,
			accent: 'bg-primary-500/10 text-primary-600-400 ring-primary-500/25',
			desc: 'Generated files — who, when, and from which prompt.'
		},
		{
			href: '/admin/slo',
			label: 'SLO',
			icon: Gauge,
			accent: 'bg-warning-500/10 text-warning-600-400 ring-warning-500/25',
			desc: 'Service-level objectives — latency, error rate, throughput, promotion.'
		},
		{
			href: '/admin/settings',
			label: 'Settings',
			icon: SlidersHorizontal,
			accent: 'bg-surface-500/10 text-surface-700-300 ring-surface-500/25',
			desc: 'Platform behaviour you can change without a redeploy.'
		},
		{
			href: '/admin/permissions',
			label: 'Tool permissions',
			icon: Lock,
			accent: 'bg-surface-500/10 text-surface-700-300 ring-surface-500/25',
			desc: 'Which tools and which resources each user may reach.'
		},
		{
			href: '/admin/navigation-permissions',
			label: 'Navigation access',
			icon: PanelLeft,
			accent: 'bg-primary-500/10 text-primary-600-400 ring-primary-500/25',
			desc: 'Choose visible Nashira areas by role, with per-user exceptions.'
		},
		{
			href: '/themes',
			label: 'Themes',
			icon: Palette,
			accent: 'bg-tertiary-500/10 text-tertiary-700-300 ring-tertiary-500/25',
			desc: "Build colour themes and publish them to every user's picker."
		}
	];
	const visibleTools = $derived(
		TOOLS.filter((tool) =>
			canViewPath(
				tool.href,
				authStore.session?.role,
				moduleStore.availability,
				navigationVisibilityStore.visibility
			)
		)
	);

	const SECTION = 'mb-3 text-xs font-semibold uppercase tracking-[0.08em] text-surface-600-400';
</script>

<svelte:head><title>Admin · Nashira</title></svelte:head>

<PageHeader title="Admin" description="What the platform has been doing, and the tools to run it.">
	{#snippet actions()}
		<RoleGate require="admin">
			{#if hasMetrics}
			<div class="w-36">
				<Select
					bind:value={days}
					options={[
						{ value: '1', label: 'Last day' },
						{ value: '7', label: 'Last 7 days' },
						{ value: '30', label: 'Last 30 days' },
						{ value: '90', label: 'Last 90 days' }
					]}
				/>
			</div>
			<Button variant="ghost" onclick={() => (autoRefresh = !autoRefresh)}>
				{#if autoRefresh}<Pause size={15} />Pause{:else}<Play size={15} />Resume{/if}
			</Button>
			<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
			{/if}
		</RoleGate>
	{/snippet}
</PageHeader>

{#if !isAdmin}
	<Alert tone="neutral" title="Admins only">
		This page reports on the platform as a whole. Your own work is under Overview.
	</Alert>
{:else if hasMetrics && error}
	<Card><ErrorState {error} onRetry={load} /></Card>
{:else if hasMetrics && loading}
	<div class="flex justify-center py-12"><Spinner size="lg" /></div>
{:else}
	<div class="space-y-6">
		{#if hasMetrics}
		<!-- Queue — the quickest health signal, and the one that goes bad first. -->
		<section>
			<h2 class={SECTION}>Queue</h2>
			<div class="grid grid-cols-2 gap-3 lg:grid-cols-4">
				<StatCard
					label="Queued"
					value={queue?.queued ?? 0}
					tone={(queue?.oldestQueuedSeconds ?? 0) > 300 ? 'warning' : 'neutral'}
					hint={queue?.oldestQueuedSeconds != null
						? `Oldest waiting ${ago(queue.oldestQueuedSeconds)}`
						: 'Nothing waiting'}
				>
					{#snippet icon()}<Clock size={16} />{/snippet}
				</StatCard>
				<StatCard
					label="Claimed"
					value={queue?.claimed ?? 0}
					tone="primary"
					hint="Being worked on"
				>
					{#snippet icon()}<Activity size={16} />{/snippet}
				</StatCard>
				<StatCard
					label="Succeeded"
					value={queue?.byStatus.succeeded ?? 0}
					tone="success"
					hint="Kept for a month"
				>
					{#snippet icon()}<CheckCircle2 size={16} />{/snippet}
				</StatCard>
				<StatCard
					label="Failed"
					value={queue?.byStatus.failed ?? 0}
					tone={(queue?.byStatus.failed ?? 0) > 0 ? 'error' : 'neutral'}
					hint="Kept for a month"
				>
					{#snippet icon()}<AlertTriangle size={16} />{/snippet}
				</StatCard>
			</div>
		</section>

		<!-- Depth on its own is ambiguous: ten jobs queued for a second is a healthy
		     queue, one queued for an hour is a worker that stopped. -->
		{#if queue && queue.oldestQueuedSeconds != null && queue.oldestQueuedSeconds > 300}
			<Alert tone="warning" title={`A job has been waiting ${ago(queue.oldestQueuedSeconds)}`}>
				Work is queued and not being claimed. Check that a worker is running — the queue depth
				alone would not have shown this.
			</Alert>
		{/if}
		{/if}

		<section>
			<h2 class={SECTION}>Tools</h2>
			<div class="grid gap-3 lg:grid-cols-3">
				{#each visibleTools as t (t.href)}
					<a href={t.href} class="ui-surface group block p-4 transition hover:-translate-y-0.5">
						<div class="flex items-start gap-3">
							<div
								class="grid h-9 w-9 shrink-0 place-items-center rounded-md ring-1 ring-inset {t.accent}"
							>
								<t.icon size={16} />
							</div>
							<div class="min-w-0 flex-1">
								<div class="flex items-center gap-1.5">
									<span class="text-sm font-semibold text-surface-900-100">{t.label}</span>
									<ArrowUpRight
										size={12}
										class="text-surface-600-400 transition group-hover:text-primary-600-400"
									/>
								</div>
								<p class="mt-0.5 text-xs text-surface-600-400">{t.desc}</p>
							</div>
						</div>
					</a>
				{/each}
			</div>
		</section>

		{#if hasMetrics}
		<!-- Run activity beside what is failing: the chart says how much, the list says
		     which. Neither answers the other's question. -->
		<section class="grid gap-3 lg:grid-cols-3">
			<Card class="lg:col-span-2">
				<div class="mb-3 flex items-start justify-between gap-3">
					<div>
						<div class="text-xs font-semibold uppercase tracking-[0.08em] text-surface-600-400">
							Runs activity
						</div>
						<div class="mt-0.5 text-sm text-surface-700-300">
							<span class="font-semibold tabular-nums text-surface-900-100">{totalRuns}</span>
							runs · last {runs?.days} day{runs?.days === 1 ? '' : 's'}
							{#if leftChanged > 0 || rolledBack > 0}
								·
								<span class="tabular-nums text-error-600-400">{leftChanged}</span>
								left changes behind,
								<span class="tabular-nums">{rolledBack}</span> rolled back
							{/if}
						</div>
					</div>
					<BarChart3 size={16} class="shrink-0 text-surface-600-400" />
				</div>
				<StackedBarChart
					buckets={runBuckets}
					series={RUN_SERIES}
					caption="Workflow runs per day by status"
					empty="No runs in this window."
				/>
			</Card>

			<Card>
				<div class="mb-3 flex items-start justify-between gap-3">
					<div>
						<div class="text-xs font-semibold uppercase tracking-[0.08em] text-surface-600-400">
							Top failing
						</div>
						<div class="mt-0.5 text-sm text-surface-700-300">Workflows with most failed runs</div>
					</div>
					<TrendingDown size={16} class="shrink-0 text-error-600-400" />
				</div>
				{#if !runs?.topFailing.length}
					<p class="py-6 text-center text-sm text-surface-600-400">
						Nothing failed in this window.
					</p>
				{:else}
					<ul class="space-y-2">
						{#each runs.topFailing as w (w.workflowId)}
							<li class="flex items-center justify-between gap-3">
								<a
									class="min-w-0 truncate text-sm hover:underline"
									href={`/workflows/${w.workflowId}`}
								>
									{w.workflowName}
								</a>
								<span class="shrink-0 text-xs tabular-nums text-error-600-400">
									{w.failedCount}
								</span>
							</li>
						{/each}
					</ul>
				{/if}
			</Card>
		</section>

		<section>
			<Card>
				<div class="mb-3 flex items-start justify-between gap-3">
					<div>
						<div class="text-xs font-semibold uppercase tracking-[0.08em] text-surface-600-400">
							Sign-in activity
						</div>
						<div class="mt-0.5 text-sm text-surface-700-300">
							<span class="font-semibold tabular-nums text-surface-900-100">
								{auth?.successes ?? 0}
							</span>
							successes ·
							<span class="font-semibold tabular-nums text-error-600-400">
								{auth?.failures ?? 0}
							</span>
							failures ·
							<span class="font-semibold tabular-nums text-warning-600-400">
								{auth?.lockouts ?? 0}
							</span>
							lockouts
						</div>
					</div>
					<LogIn size={16} class="shrink-0 text-surface-600-400" />
				</div>
				<StackedBarChart
					buckets={authBuckets}
					series={AUTH_SERIES}
					caption="Sign-in activity per day by outcome"
					empty="No sign-in activity in this window."
				/>
			</Card>
		</section>

		{/if}

		{#if lastRefreshed}
			<p class="text-xs text-surface-600-400">
				Updated {lastRefreshed.toLocaleTimeString()}{autoRefresh
					? ' · refreshing every 30s'
					: ' · auto-refresh paused'}
			</p>
		{/if}
	</div>
{/if}
