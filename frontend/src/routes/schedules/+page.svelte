<script lang="ts">
	// Every trigger on the platform, cron and webhook alike.
	//
	// Ordered by what fires next, because the question this page is opened with is
	// "what is about to happen" — and the second question, when something already has,
	// is "what should have happened and didn't". A trigger whose next firing is in the
	// past is called out for exactly that: the scheduler has not been round to it.
	import { untrack } from 'svelte';
	import { listSchedules, type FleetTrigger } from '$lib/api/fleet.api';
	import {
		PageHeader,
		Card,
		Select,
		SearchInput,
		Badge,
		Alert,
		StatCard,
		Spinner,
		ErrorState,
		EmptyState,
		Button,
		toast
	} from '$lib/components/ui';
	import { RefreshCw, CalendarClock, Webhook, AlertTriangle } from 'lucide-svelte';

	let rows = $state<FleetTrigger[]>([]);
	let total = $state(0);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let type = $state('');
	let enabled = $state('');
	let q = $state('');

	let seq = 0;

	async function load() {
		const mine = ++seq;
		error = null;
		try {
			const page = await listSchedules({
				type: type || undefined,
				enabled: enabled === '' ? undefined : enabled === 'true',
				q: q.trim() || undefined,
				limit: 200
			});
			if (mine !== seq) return;
			rows = page.items;
			total = page.total;
		} catch (e) {
			if (mine !== seq) return;
			if (rows.length === 0) error = e;
			else toast.fromError(e, "Couldn't refresh the schedules");
		} finally {
			if (mine === seq) loading = false;
		}
	}

	$effect(() => {
		type;
		enabled;
		q;
		untrack(() => load());
	});

	const overdue = $derived(rows.filter((r) => r.overdue));
	const crons = $derived(rows.filter((r) => r.type === 'cron').length);
	const hooks = $derived(rows.filter((r) => r.type === 'webhook').length);

	// Relative, because "in 4 hours" is what somebody wants from a schedule and an
	// absolute timestamp makes them do the arithmetic. The absolute one is the tooltip.
	function until(at: string | null): string {
		if (!at) return '—';
		const ms = new Date(at).getTime() - Date.now();
		const abs = Math.abs(ms);
		const mins = Math.round(abs / 60_000);
		const text =
			mins < 60
				? `${mins}m`
				: mins < 1440
					? `${Math.round(mins / 60)}h`
					: `${Math.round(mins / 1440)}d`;
		return ms < 0 ? `${text} ago` : `in ${text}`;
	}

	function absolute(at: string | null): string {
		return at ? new Date(at).toLocaleString() : '';
	}

	function lastTone(status: string | null): 'success' | 'error' | 'neutral' {
		if (status === 'completed') return 'success';
		if (status === 'failed' || status === 'error') return 'error';
		return 'neutral';
	}

	const filtered = $derived(Boolean(type || enabled || q.trim()));
</script>

<svelte:head><title>Schedules · Nashira</title></svelte:head>

<PageHeader title="Schedules" description="What is going to run, and what should have.">
	{#snippet actions()}
		<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-4">
		<div class="grid gap-3 sm:grid-cols-3">
			<StatCard label="Cron triggers" value={crons} hint="Fire on a schedule">
				{#snippet icon()}<CalendarClock size={16} />{/snippet}
			</StatCard>
			<StatCard label="Webhooks" value={hooks} hint="Fire when something calls in">
				{#snippet icon()}<Webhook size={16} />{/snippet}
			</StatCard>
			<StatCard
				label="Overdue"
				value={overdue.length}
				tone={overdue.length > 0 ? 'error' : 'neutral'}
				hint="Due in the past and still waiting"
			>
				{#snippet icon()}<AlertTriangle size={16} />{/snippet}
			</StatCard>
		</div>

		{#if overdue.length > 0}
			<Alert
				tone="error"
				title={overdue.length === 1
					? '1 trigger is past its firing time'
					: `${overdue.length} triggers are past their firing time`}
			>
				{overdue.map((t) => t.name).join(', ')}. The scheduler has not picked them up —
				check that the backend is running and look at
				<a class="underline" href="/admin/traces?category=scheduler">the scheduler's traces</a>.
			</Alert>
		{/if}

		<Card>
			<div class="flex flex-wrap items-end gap-2">
				<div class="w-40">
					<Select
						bind:value={type}
						label="Type"
						options={[
							{ value: '', label: 'Any type' },
							{ value: 'cron', label: 'Cron' },
							{ value: 'webhook', label: 'Webhook' }
						]}
					/>
				</div>
				<div class="w-40">
					<Select
						bind:value={enabled}
						label="State"
						options={[
							{ value: '', label: 'Any state' },
							{ value: 'true', label: 'Enabled' },
							{ value: 'false', label: 'Disabled' }
						]}
					/>
				</div>
				<div class="min-w-56 flex-1">
					<SearchInput bind:value={q} width="w-full" placeholder="Trigger or workflow name…" />
				</div>
			</div>
		</Card>

		{#if loading && rows.length === 0}
			<div class="flex justify-center py-12"><Spinner size="lg" /></div>
		{:else if rows.length === 0}
			<EmptyState
				title={filtered ? 'Nothing matches' : 'Nothing is scheduled'}
				description={filtered
					? 'No trigger matches these filters.'
					: 'Add a cron or webhook trigger from a workflow to see it here.'}
			/>
		{:else}
			<Card>
				<div class="mb-2 text-xs text-surface-600-400">Showing {rows.length} of {total}</div>
				<div class="overflow-x-auto">
					<table class="w-full text-sm">
						<thead class="text-left text-xs uppercase tracking-wide text-surface-600-400">
							<tr class="border-b border-surface-200-800">
								<th class="py-2 pr-3 font-medium">Next</th>
								<th class="py-2 pr-3 font-medium">Trigger</th>
								<th class="py-2 pr-3 font-medium">Workflow</th>
								<th class="py-2 pr-3 font-medium">When</th>
								<th class="py-2 pr-3 font-medium">Last run</th>
								<th class="py-2 pr-3 text-right font-medium">Fired</th>
							</tr>
						</thead>
						<tbody>
							{#each rows as t (t.workflowTriggerId)}
								<tr
									class="border-b border-surface-100-900 hover:bg-surface-100-900/60"
									class:opacity-60={!t.enabled}
								>
									<td
										class="py-2 pr-3 whitespace-nowrap text-xs tabular-nums"
										class:text-error-700-300={t.overdue}
										title={absolute(t.nextRunAt)}
									>
										{t.enabled ? until(t.nextRunAt) : 'disabled'}
									</td>
									<td class="py-2 pr-3">
										<div class="flex flex-wrap items-center gap-1.5">
											<span>{t.name}</span>
											<Badge tone={t.type === 'cron' ? 'neutral' : 'primary'}>{t.type}</Badge>
											{#if t.overdue}<Badge tone="error">overdue</Badge>{/if}
										</div>
									</td>
									<td class="py-2 pr-3">
										<a class="hover:underline" href={`/workflows/${t.workflowId}`}>
											{t.workflowName}
										</a>
										<span class="ml-1 text-xs text-surface-600-400">
											{t.workflowEnvironment}
										</span>
									</td>
									<td class="py-2 pr-3 font-mono text-xs text-surface-600-400">
										{#if t.type === 'cron'}
											{t.cronExpression}
											<span class="opacity-70">{t.timezone}</span>
										{:else}
											{t.route ?? '—'}
										{/if}
									</td>
									<td class="py-2 pr-3 text-xs">
										{#if t.lastRunStatus}
											<Badge tone={lastTone(t.lastRunStatus)}>{t.lastRunStatus}</Badge>
											<span class="ml-1 text-surface-600-400" title={absolute(t.lastRunAt)}>
												{until(t.lastRunAt)}
											</span>
										{:else}
											<span class="text-surface-600-400">never</span>
										{/if}
									</td>
									<td class="py-2 pr-3 text-right tabular-nums text-xs text-surface-600-400">
										{t.fireCount}
									</td>
								</tr>
								{#if t.lastError}
									<tr class="border-b border-surface-100-900">
										<td colspan="6" class="px-3 pb-2 text-xs text-error-700-300">
											{t.lastError}
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
