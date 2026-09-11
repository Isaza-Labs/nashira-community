<script lang="ts">
	// The operational trail — what the platform is doing, live.
	//
	// Distinct from the two screens either side of it, and the difference is the point:
	//   /admin/audit    — what CHANGED, with before/after, tamper-evident.
	//   /admin/sessions — what the agent did in a conversation.
	//   here            — what HAPPENED, including everything that changed nothing:
	//                     the job the worker claimed and never finished, the cron that
	//                     came due while the platform was down, the pip install that
	//                     hung, the tool call the agent was refused.
	//
	// Three filters and what each is for:
	//   Search      — one box over every text column, metadata included. People arrive
	//                 with a fragment ("timeout", a job id seen in a log), not the head
	//                 of a dotted action name.
	//   User        — id, name, mail, or the actor string an unattended run bound
	//                 itself to. "Who set this off" is the question; the id is rarely
	//                 what the asker is holding.
	//   Slower than — the only filter that also changes the ORDER. Newest-first is
	//                 wrong here: the slowest rows are what was asked for, and a
	//                 time-ordered page of a wide window will not contain them.
	import { untrack } from 'svelte';
	import { page } from '$app/state';
	import {
		getTraces,
		getTraceSummary,
		type TraceEvent,
		type TraceSummary
	} from '$lib/api/traces.api';
	import {
		PageHeader,
		Card,
		StatCard,
		Select,
		Button,
		Badge,
		Input,
		Spinner,
		ErrorState,
		EmptyState,
		toast
	} from '$lib/components/ui';
	import { RefreshCw, Pause, Play, Activity, AlertTriangle, Clock, X } from 'lucide-svelte';

	let rows = $state<TraceEvent[]>([]);
	let total = $state(0);
	// Echoed by the server so the header can say which order this page is in, rather
	// than claiming a chronology the slower-than filter has already overridden.
	let sortedBy = $state<'at' | 'duration'>('at');
	let summary = $state<TraceSummary | null>(null);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let lastRefreshed = $state<Date | null>(null);

	// Filters. Kept flat and ANDed — the questions people bring here are conjunctions
	// ("failed worker jobs", "everything this user set off"), never disjunctions.
	//
	// Seeded from the query string, read once at init: /schedules links here with
	// ?category=scheduler, and a later navigation must not overwrite what has been
	// typed since.
	let category = $state(page.url.searchParams.get('category') ?? '');
	let status = $state(page.url.searchParams.get('status') ?? '');
	// Empty means unbounded. The default: a search or a user filter is almost always
	// about something that happened earlier than the last hour, and a window that
	// empties every filter reads as "the filters do not work", not as "narrow window".
	let windowMinutes = $state('');
	// The cards always need a window, and a week is the most the server will count.
	const CARD_MAX_MINUTES = 7 * 24 * 60;
	const WINDOWS = [
		{ value: '15', label: 'Last 15 min' },
		{ value: '60', label: 'Last hour' },
		{ value: '360', label: 'Last 6 hours' },
		{ value: '1440', label: 'Last 24 hours' },
		{ value: '10080', label: 'Last 7 days' },
		{ value: '', label: 'All time' }
	];

	// Exact, and deliberately not an input: it is set by clicking a row or by a deep
	// link, and shows as a removable chip. Typing the same id into Search finds it too,
	// just more loosely.
	let requestId = $state(page.url.searchParams.get('request_id') ?? '');

	// What is typed, and the copies the query actually uses. Search scans the metadata
	// blob, so firing on every keystroke would put a full text scan behind every letter
	// and flash results for 2, 20 and 200 on the way to 2000.
	const DEBOUNCE_MS = 300;
	const seeded = {
		search: page.url.searchParams.get('search') ?? '',
		user: page.url.searchParams.get('user') ?? '',
		minDuration: page.url.searchParams.get('min_duration_ms') ?? ''
	};
	let searchInput = $state(seeded.search);
	let userInput = $state(seeded.user);
	let minDurationInput = $state(seeded.minDuration);
	let search = $state(seeded.search);
	let user = $state(seeded.user);
	let minDuration = $state(seeded.minDuration);

	const REFRESH_MS = 5_000;
	let autoRefresh = $state(true);
	let expanded = $state<string | null>(null);

	let seq = 0;

	$effect(() => {
		const s = searchInput;
		const u = userInput;
		const d = minDurationInput;
		// Compared untracked: the timeout writes these three, and tracking them here
		// would make every settled keystroke schedule another round.
		const settled = untrack(() => s === search && u === user && d === minDuration);
		if (settled) return;
		const id = setTimeout(() => {
			search = s;
			user = u;
			minDuration = d;
		}, DEBOUNCE_MS);
		return () => clearTimeout(id);
	});

	async function load() {
		const mine = ++seq;
		error = null;
		const minutes = Number.parseInt(windowMinutes, 10) || 0;
		// The list is bounded by the same window the cards report, so the two agree.
		// With no window the list is unbounded and the cards fall back to the widest
		// span the server counts, and say so.
		const from = minutes ? new Date(Date.now() - minutes * 60_000).toISOString() : undefined;
		try {
			const [list, sum] = await Promise.all([
				getTraces({
					search: search.trim() || undefined,
					category: category || undefined,
					status: status || undefined,
					user: user.trim() || undefined,
					requestId: requestId.trim() || undefined,
					minDurationMs: Number.parseInt(minDuration, 10) || undefined,
					from,
					limit: 150
				}),
				getTraceSummary(minutes || CARD_MAX_MINUTES)
			]);
			if (mine !== seq) return;
			rows = list.items;
			total = list.total;
			sortedBy = list.sortedBy;
			summary = sum;
			lastRefreshed = new Date();
		} catch (e) {
			if (mine !== seq) return;
			if (rows.length === 0) error = e;
			else toast.fromError(e, "Couldn't refresh the trail");
		} finally {
			if (mine === seq) loading = false;
		}
	}

	// Only the filters are tracked. Reading `rows` in here would make every load
	// schedule the next one, which is a spin, not a refresh.
	$effect(() => {
		category;
		status;
		search;
		user;
		requestId;
		minDuration;
		windowMinutes;
		untrack(() => load());
	});

	$effect(() => {
		if (!autoRefresh) return;
		const id = setInterval(() => {
			if (document.hidden) return;
			untrack(() => load());
		}, REFRESH_MS);
		return () => clearInterval(id);
	});

	const CATEGORIES = [
		{ value: '', label: 'All categories' },
		{ value: 'http', label: 'HTTP' },
		{ value: 'ai', label: 'AI' },
		{ value: 'tool', label: 'Tools' },
		{ value: 'worker', label: 'Worker' },
		{ value: 'scheduler', label: 'Scheduler' },
		{ value: 'workflow', label: 'Workflow' },
		{ value: 'system', label: 'System' }
	];

	function statusTone(s: string): 'success' | 'error' | 'warning' | 'neutral' {
		if (s === 'completed') return 'success';
		if (s === 'failed') return 'error';
		// `started` is not neutral: a row still saying it has not finished is the thing
		// worth noticing, so it gets a colour that asks to be looked at.
		if (s === 'started') return 'warning';
		return 'neutral';
	}

	function duration(ms: number | null): string {
		if (ms === null) return 'in flight';
		if (ms < 1000) return `${ms} ms`;
		if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
		return `${(ms / 60_000).toFixed(1)} min`;
	}

	function windowLabel(minutes: number): string {
		return WINDOWS.find((w) => w.value === String(minutes))?.label ?? `Last ${minutes} min`;
	}

	function when(at: string): string {
		return new Date(at).toLocaleTimeString(undefined, { hour12: false });
	}

	function pretty(json: string | null): string {
		if (!json) return '';
		try {
			return JSON.stringify(JSON.parse(json), null, 2);
		} catch {
			// Clipped metadata is no longer valid JSON. Showing the truncated text beats
			// showing nothing, and is the honest thing when the row says it was clipped.
			return json;
		}
	}

	function clearFilters() {
		category = '';
		status = '';
		requestId = '';
		searchInput = '';
		userInput = '';
		minDurationInput = '';
		search = '';
		user = '';
		minDuration = '';
	}

	const filtered = $derived(
		Boolean(
			category ||
				status ||
				requestId.trim() ||
				searchInput.trim() ||
				userInput.trim() ||
				minDurationInput
		)
	);
</script>

<svelte:head><title>Traces · Admin · Nashira</title></svelte:head>

<PageHeader
	title="Traces"
	description="What the platform is doing — including everything that changed nothing."
>
	{#snippet actions()}
		<div class="w-40">
			<Select bind:value={windowMinutes} options={WINDOWS} />
		</div>
		<Button variant="ghost" onclick={() => (autoRefresh = !autoRefresh)}>
			{#if autoRefresh}<Pause size={15} />Pause{:else}<Play size={15} />Resume{/if}
		</Button>
		<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-4">
		<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
			<StatCard
				label="Events"
				value={summary?.total ?? 0}
				hint={summary ? windowLabel(summary.minutes) : '—'}
			>
				{#snippet icon()}<Activity size={16} />{/snippet}
			</StatCard>
			<StatCard
				label="In flight"
				value={summary?.inFlight ?? 0}
				tone={(summary?.inFlight ?? 0) > 0 ? 'warning' : 'neutral'}
				hint="Started, no outcome yet"
			>
				{#snippet icon()}<Clock size={16} />{/snippet}
			</StatCard>
			<StatCard
				label="Failed"
				value={summary?.failed ?? 0}
				tone={(summary?.failed ?? 0) > 0 ? 'error' : 'neutral'}
				hint="In this window"
			>
				{#snippet icon()}<AlertTriangle size={16} />{/snippet}
			</StatCard>
			<StatCard
				label="Slowest"
				value={summary?.slowest[0] ? duration(summary.slowest[0].durationMs) : '—'}
				hint={summary?.slowest[0]?.action ?? 'Nothing measured yet'}
			/>
		</div>

		<Card>
			<!-- One grid, one row at desktop, and no per-field hints: a hint under some
			     controls and not others pushes those boxes up off the shared baseline. -->
			<div
				class="grid items-end gap-2 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-[minmax(0,2fr)_minmax(0,1.2fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_auto]"
			>
				<Input bind:value={searchInput} label="Search" placeholder="worker.job, timeout, a device id…" />
				<Input bind:value={userInput} label="User" placeholder="name, mail or id" />
				<Select bind:value={category} label="Category" options={CATEGORIES} />
				<Select
					bind:value={status}
					label="Status"
					options={[
						{ value: '', label: 'Any status' },
						{ value: 'started', label: 'In flight' },
						{ value: 'completed', label: 'Completed' },
						{ value: 'failed', label: 'Failed' }
					]}
				/>
				<Input
					bind:value={minDurationInput}
					label="Slower than (ms)"
					type="number"
					min="0"
					placeholder="2000"
				/>
				<div class="flex items-end">
					{#if filtered}
						<Button variant="ghost" onclick={clearFilters}><X size={14} />Clear</Button>
					{/if}
				</div>
			</div>
			<p class="mt-2 text-xs text-surface-600-400">
				Search covers action, category, error, request id and metadata · User matches name,
				mail, id or the actor of unattended work · Slower than lists the slowest first.
			</p>

			{#if requestId.trim()}
				<div class="mt-3 flex items-center gap-2 text-xs text-surface-600-400">
					<span>Pinned to one request</span>
					<button
						type="button"
						class="inline-flex items-center gap-1 rounded-full bg-primary-500/15 px-2.5 py-1 font-mono text-primary-700-300 ring-1 ring-inset ring-primary-500/30"
						onclick={() => (requestId = '')}
					>
						{requestId}
						<X size={12} />
					</button>
				</div>
			{/if}

			{#if summary && Object.keys(summary.byCategory).length > 0}
				<div class="mt-3 flex flex-wrap gap-1.5 border-t border-surface-200-800 pt-3">
					{#each Object.entries(summary.byCategory).sort((a, b) => b[1] - a[1]) as [key, count] (key)}
						{@const on = category === key}
						<button
							type="button"
							aria-pressed={on}
							onclick={() => (category = on ? '' : key)}
							class="inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-xs transition {on
								? 'bg-primary-500/15 text-primary-700-300 ring-1 ring-inset ring-primary-500/30'
								: 'bg-surface-200-800/60 text-surface-700-300 hover:bg-surface-200-800'}"
						>
							{key}
							<span class="tabular-nums opacity-70">{count}</span>
						</button>
					{/each}
				</div>
			{/if}
		</Card>

		{#if loading && rows.length === 0}
			<div class="flex justify-center py-12"><Spinner size="lg" /></div>
		{:else if rows.length === 0}
			<EmptyState
				title="Nothing matches"
				description={filtered
					? windowMinutes
						? 'No trace in this window matches these filters. Widen the window, or clear one.'
						: 'No trace matches these filters.'
					: 'Nothing has been recorded yet. Traces appear as soon as the platform does something.'}
			/>
		{:else}
			<Card>
				<div class="mb-2 flex items-baseline justify-between text-xs text-surface-600-400">
					<span>
						Showing {rows.length} of {total}
						{#if sortedBy === 'duration'}· slowest first{:else}· newest first{/if}
					</span>
					{#if lastRefreshed}
						<span>
							Updated {lastRefreshed.toLocaleTimeString()}{autoRefresh
								? ' · live'
								: ' · paused'}
						</span>
					{/if}
				</div>
				<div class="overflow-x-auto">
					<table class="w-full text-sm">
						<thead class="text-left text-xs uppercase tracking-wide text-surface-600-400">
							<tr class="border-b border-surface-200-800">
								<th class="py-2 pr-3 font-medium">Time</th>
								<th class="py-2 pr-3 font-medium">Action</th>
								<th class="py-2 pr-3 font-medium">Status</th>
								<th class="py-2 pr-3 text-right font-medium">Took</th>
								<th class="py-2 pr-3 font-medium">Who</th>
							</tr>
						</thead>
						<tbody>
							{#each rows as r (r.traceEventId)}
								{@const open = expanded === r.traceEventId}
								<tr
									class="cursor-pointer border-b border-surface-100-900 hover:bg-surface-100-900/60"
									onclick={() => (expanded = open ? null : r.traceEventId)}
								>
									<td class="py-2 pr-3 font-mono text-xs tabular-nums text-surface-600-400">
										{when(r.at)}
									</td>
									<td class="py-2 pr-3 font-mono text-xs">{r.action}</td>
									<td class="py-2 pr-3">
										<Badge tone={statusTone(r.status)}>{r.status}</Badge>
									</td>
									<td
										class="py-2 pr-3 text-right tabular-nums text-xs"
										class:text-warning-700-300={r.durationMs === null}
									>
										{duration(r.durationMs)}
									</td>
									<td class="py-2 pr-3 text-xs text-surface-600-400">
										{#if r.actor}
											<!-- One click from "this row" to "everything this account set
											     off", which is the follow-up question in almost every case. -->
											<button
												type="button"
												class="underline decoration-dotted underline-offset-2"
												onclick={(e) => {
													e.stopPropagation();
													userInput = r.actor ?? '';
												}}
											>
												{r.actor}
											</button>
										{:else}
											—
										{/if}
									</td>
								</tr>
								{#if open}
									<tr class="border-b border-surface-100-900 bg-surface-100-900/40">
										<td colspan="5" class="px-3 py-3">
											<div class="space-y-2 text-xs">
												{#if r.errorMessage}
													<div class="text-error-700-300">{r.errorMessage}</div>
												{/if}
												<div class="flex flex-wrap gap-x-6 gap-y-1 text-surface-600-400">
													<span>category <span class="font-mono">{r.category}</span></span>
													<span>at <span class="font-mono">{r.at}</span></span>
													{#if r.requestId}
														<!-- The join between this trail and the audit one: the
														     same id is stored in both. -->
														<button
															type="button"
															class="underline"
															onclick={(e) => {
																e.stopPropagation();
																requestId = r.requestId ?? '';
															}}
														>
															request {r.requestId}
														</button>
														<a
															class="underline"
															href={`/admin/audit?requestId=${encodeURIComponent(r.requestId)}`}
															onclick={(e) => e.stopPropagation()}
														>
															see what it changed
														</a>
													{/if}
												</div>
												{#if r.metadataJson}
													<pre
														class="overflow-x-auto rounded bg-surface-200-800/50 p-2 font-mono text-[11px]">{pretty(
															r.metadataJson
														)}</pre>
												{/if}
											</div>
										</td>
									</tr>
								{/if}
							{/each}
						</tbody>
					</table>
				</div>
			</Card>
		{/if}
	</div>
{/if}
