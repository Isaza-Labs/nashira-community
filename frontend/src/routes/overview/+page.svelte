<script lang="ts">
	import { deviceStats, type DeviceStats } from '$lib/api/devices.api';
	import { listSources, type InventorySource } from '$lib/api/inventory.api';
	import { listArticles } from '$lib/api/knowledge.api';
	import { listWorkflows } from '$lib/api/workflows.api';
	import { listAudit, type AuditEvent } from '$lib/api/audit.api';
	import {
		PageHeader,
		Card,
		StatCard,
		StatusBadge,
		Badge,
		ErrorState,
		EmptyState
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { authStore } from '$lib/stores/auth.svelte';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { timeAgo, absolute, isFresh } from '$lib/utils/time';
	import { toolLabel } from '$lib/utils/tool-labels';
	import { Server, Boxes, Workflow as WorkflowIcon, BookOpen } from 'lucide-svelte';

	// Operational landing surface: what is out there, what is stale, what the agent
	// has been doing. Every number links to the list it summarizes, so this is a
	// starting point rather than a dead end.
	//
	// Overview is core, so it has to open in every deployment — including ones that run
	// none of the capabilities it summarises. Each card is asked for only where its
	// capability is on: a card whose producer is disabled is absent, never a zero, and
	// never the error that would take the whole page down with it.
	let stats = $state<DeviceStats>({ total: 0, byStatus: {} });
	let sources = $state<InventorySource[]>([]);
	let workflowCount = $state(0);
	let articleCount = $state(0);
	let events = $state<AuditEvent[]>([]);

	let loading = $state(true);
	let error = $state<unknown>(null);

	const isAdmin = $derived(authStore.session?.role === 'admin');

	const hasFleet = $derived(moduleStore.isEnabled('fleet'));
	const hasAutomation = $derived(moduleStore.isEnabled('automation'));
	const hasKnowledge = $derived(moduleStore.isEnabled('knowledge'));
	const hasGovernance = $derived(moduleStore.isEnabled('governance'));
	const hasAnything = $derived(hasFleet || hasAutomation || hasKnowledge);

	async function load() {
		loading = true;
		error = null;
		try {
			// Audit is admin-only; asking for it as an operator would 403 and sink
			// the whole page, so it is requested conditionally and tolerated failing.
			const [d, s, w, k] = await Promise.all([
				hasFleet ? deviceStats() : Promise.resolve<DeviceStats>({ total: 0, byStatus: {} }),
				hasFleet ? listSources(100, 0) : Promise.resolve({ items: [] as InventorySource[] }),
				hasAutomation ? listWorkflows(undefined, 1, 0) : Promise.resolve({ total: 0 }),
				hasKnowledge ? listArticles(1, 0) : Promise.resolve({ total: 0 })
			]);
			stats = d;
			sources = s.items;
			workflowCount = w.total;
			articleCount = k.total;

			if (isAdmin && hasGovernance) {
				events = await listAudit({ entityType: 'agent.tool', limit: 8 })
					.then((r) => r.items)
					.catch(() => []);
			}
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	// Devices grouped by status, most-alarming first so the eye lands on trouble.
	// Statuses arrive as the server stored them; fold case and blanks here.
	const RANK: Record<string, number> = { failed: 0, error: 0, unhealthy: 0, pending: 1, stale: 1 };
	const byStatus = $derived.by(() => {
		const m = new Map<string, number>();
		for (const [status, count] of Object.entries(stats.byStatus)) {
			const k = (status || 'unknown').toLowerCase();
			m.set(k, (m.get(k) ?? 0) + count);
		}
		return [...m.entries()]
			.map(([status, count]) => ({ status, count }))
			.sort((a, b) => (RANK[a.status] ?? 2) - (RANK[b.status] ?? 2) || b.count - a.count);
	});

	const BAD = new Set(['failed', 'error', 'unhealthy']);
	const unhealthy = $derived(
		byStatus.filter((s) => BAD.has(s.status)).reduce((n, s) => n + s.count, 0)
	);

	// A source that has never synced is as much of a problem as a stale one.
	const staleSources = $derived(sources.filter((s) => !isFresh(s.lastSyncedAt, 86400)).length);
</script>

<svelte:head><title>Overview · Nashira</title></svelte:head>

<PageHeader title="Overview" description="Current state of the estate." />

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-4">
		{#if !hasAnything}
			<!-- Nothing this page summarises is deployed. Saying so beats four zeros,
			     which would read as an estate that exists and is empty. -->
			<EmptyState
				title="Nothing to summarise yet"
				description="This deployment does not run the capabilities this page reports on."
			/>
		{/if}
		<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
			{#if hasFleet}
			<StatCard
				label="Devices"
				value={stats.total}
				{loading}
				href="/devices"
				tone={unhealthy > 0 ? 'error' : 'neutral'}
				hint={unhealthy > 0 ? `${unhealthy} need attention` : 'all reporting healthy'}
			>
				{#snippet icon()}<Server size={16} />{/snippet}
			</StatCard>

			<StatCard
				label="Inventory sources"
				value={sources.length}
				{loading}
				href="/inventory"
				tone={staleSources > 0 ? 'warning' : 'neutral'}
				hint={staleSources > 0 ? `${staleSources} not synced in 24h` : 'all synced recently'}
			>
				{#snippet icon()}<Boxes size={16} />{/snippet}
			</StatCard>
			{/if}

			{#if hasAutomation}
			<StatCard label="Workflows" value={workflowCount} {loading} href="/workflows">
				{#snippet icon()}<WorkflowIcon size={16} />{/snippet}
			</StatCard>
			{/if}

			{#if hasKnowledge}
			<StatCard label="Knowledge articles" value={articleCount} {loading} href="/knowledge">
				{#snippet icon()}<BookOpen size={16} />{/snippet}
			</StatCard>
			{/if}
		</div>

		{#if hasFleet}
		<div class="grid gap-4 lg:grid-cols-2">
			<Card title="Devices by status">
				{#if loading}
					<div class="space-y-2">
						{#each { length: 3 } as _, i (i)}
							<div class="h-6 animate-pulse rounded bg-surface-200-800"></div>
						{/each}
					</div>
				{:else if stats.total === 0}
					<EmptyState title="No devices yet" description="Register a device or sync an inventory source." />
				{:else}
					<ul class="space-y-2">
						{#each byStatus as s (s.status)}
							<li class="flex items-center gap-3">
								<StatusBadge status={s.status} />
								<!-- Proportional bar: relative weight reads faster than the raw count. -->
								<div class="h-1.5 flex-1 overflow-hidden rounded-full bg-surface-200-800">
									<div
										class="h-full rounded-full bg-primary-400/70"
										style={`width: ${Math.round((s.count / stats.total) * 100)}%`}
									></div>
								</div>
								<span class="w-8 text-right text-sm tabular-nums text-surface-700-300">{s.count}</span>
							</li>
						{/each}
					</ul>
				{/if}
			</Card>

			<Card title="Inventory freshness">
				{#if loading}
					<div class="space-y-2">
						{#each { length: 3 } as _, i (i)}
							<div class="h-6 animate-pulse rounded bg-surface-200-800"></div>
						{/each}
					</div>
				{:else if sources.length === 0}
					<EmptyState title="No inventory sources" description="Add a NetBox source to sync devices." />
				{:else}
					<ul class="divide-y divide-surface-100-900">
						{#each sources as s (s.id)}
							<li class="flex items-center gap-3 py-2 first:pt-0 last:pb-0">
								<span class="min-w-0 flex-1 truncate text-sm font-medium">{s.name}</span>
								<Badge>{s.kind}</Badge>
								<span
									class="text-xs {isFresh(s.lastSyncedAt, 86400)
										? 'text-surface-600-400'
										: 'text-warning-700-300'}"
									title={s.lastSyncedAt ? absolute(s.lastSyncedAt) : 'never synced'}
								>
									{s.lastSyncedAt ? timeAgo(s.lastSyncedAt) : 'never synced'}
								</span>
							</li>
						{/each}
					</ul>
				{/if}
			</Card>
		</div>
		{/if}

		{#if hasGovernance}
		<RoleGate require="admin">
			<Card title="Recent agent activity">
				{#if loading}
					<div class="space-y-2">
						{#each { length: 4 } as _, i (i)}
							<div class="h-5 animate-pulse rounded bg-surface-200-800"></div>
						{/each}
					</div>
				{:else if events.length === 0}
					<EmptyState title="No agent actions recorded yet" />
				{:else}
					<ul class="divide-y divide-surface-100-900">
						{#each events as e (e.id)}
							<li class="flex items-center gap-3 py-2 text-sm first:pt-0 last:pb-0">
								<span class="min-w-0 flex-1 truncate">{toolLabel(e.action)}</span>
								<code class="hidden text-[11px] text-surface-600-400 sm:block">{e.action}</code>
								<span class="shrink-0 text-xs text-surface-600-400" title={absolute(e.at)}>
									{timeAgo(e.at)}
								</span>
							</li>
						{/each}
					</ul>
				{/if}
			</Card>
		</RoleGate>
		{/if}
	</div>
{/if}
