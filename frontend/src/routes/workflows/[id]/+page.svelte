<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import {
		getWorkflow,
		getPlan,
		getYaml,
		downloadYaml,
		downloadBundle,
		deleteWorkflow,
		listRuns,
		type Workflow,
		type WorkflowPlan,
		type WorkflowRun
	} from '$lib/api/workflows.api';
	import { prefetchFleetRun } from '$lib/api/runs.api';
	import {
		PageHeader,
		Tabs,
		Badge,
		Button,
		StatusBadge,
		Spinner,
		ErrorState,
		confirm,
		toast
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import WorkflowConfirmCard from '$lib/components/workflow/WorkflowConfirmCard.svelte';
	import TriggerPanel from '$lib/components/workflow/TriggerPanel.svelte';
	import TestPanel from '$lib/components/workflow/TestPanel.svelte';
	import { ArrowLeft, ChevronRight, Download, Package, Trash2 } from 'lucide-svelte';

	// The [id] route param is always present here; assert to drop `| undefined`.
	const id = $derived(page.params.id!);

	let workflow = $state<Workflow | null>(null);
	let plan = $state<WorkflowPlan | null>(null);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let tab = $state('plan');
	const tabs = [
		{ value: 'plan', label: 'Plan' },
		{ value: 'yaml', label: 'YAML' },
		{ value: 'runs', label: 'Runs' },
		{ value: 'triggers', label: 'Triggers' },
		{ value: 'tests', label: 'Tests' },
		{ value: 'versions', label: 'Versions' }
	];

	let yaml = $state<string | null>(null);
	let yamlLoading = $state(false);
	let yamlError = $state<unknown>(null);

	let runs = $state<WorkflowRun[]>([]);
	let runsLoading = $state(false);
	let runsError = $state<unknown>(null);

	async function loadCore() {
		loading = true;
		error = null;
		try {
			const [w, p] = await Promise.all([getWorkflow(id), getPlan(id)]);
			workflow = w;
			plan = p;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		id;
		loadCore();
	});

	// Same fetch as `loadCore`, without the page-level spinner. A snippet edit is
	// saved by the time this runs, and swapping the whole page for a spinner
	// afterwards reads as if something had been lost — so the plan is replaced in
	// place, and a failure here says plainly that the save was not the part that
	// failed.
	async function refreshPlan() {
		try {
			const [w, p] = await Promise.all([getWorkflow(id), getPlan(id)]);
			workflow = w;
			plan = p;
		} catch (e) {
			toast.fromError(e, "Saved, but couldn't refresh the plan — reload to see it");
		}
	}

	async function loadYaml() {
		if (yaml !== null || yamlLoading) return;
		yamlLoading = true;
		yamlError = null;
		try {
			yaml = await getYaml(id);
		} catch (e) {
			yamlError = e;
		} finally {
			yamlLoading = false;
		}
	}

	async function loadRuns() {
		runsLoading = true;
		runsError = null;
		try {
			runs = (await listRuns(id, 20, 0)).items;
		} catch (e) {
			runsError = e;
		} finally {
			runsLoading = false;
		}
	}

	// Lazy-load the yaml/runs panels the first time each is shown.
	$effect(() => {
		if (tab === 'yaml') loadYaml();
		else if (tab === 'runs') loadRuns();
	});

	function fmt(iso: string): string {
		const d = new Date(iso);
		return isNaN(d.getTime()) ? iso : d.toLocaleString();
	}

	let exporting = $state(false);

	async function exportYaml(w: Workflow) {
		exporting = true;
		try {
			await downloadYaml(w.id, w.name, w.version, w.environment);
		} catch (e) {
			toast.fromError(e, "Couldn't export the workflow");
		} finally {
			exporting = false;
		}
	}

	let bundling = $state(false);

	// The YAML above names this instance's snippets by GUID, so it only ever imports
	// back here. The bundle carries their definitions — and the sub-workflows, the
	// triggers and what the receiving side must support — so it survives the crossing.
	async function exportBundle(w: Workflow) {
		bundling = true;
		try {
			await downloadBundle(w.id, w.name);
		} catch (e) {
			toast.fromError(e, "Couldn't export the workflow bundle");
		} finally {
			bundling = false;
		}
	}

	let deleting = $state(false);

	// Naming the environment is the point of the prompt: the API removes a promoted copy
	// as readily as a draft, and this page is the one place the reader can already see
	// which copy they are looking at — the confirmation should not make them guess.
	async function remove(w: Workflow) {
		const ok = await confirm({
			title: `Delete "${w.name}"?`,
			message:
				`This removes the ${w.environment} copy at v${w.version}. Its past runs and audit ` +
				`trail stay readable, and any promoted copy in another environment is untouched.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;

		deleting = true;
		try {
			await deleteWorkflow(w.id);
			toast.success(`Deleted "${w.name}"`);
			await goto('/workflows');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the workflow");
			deleting = false;
		}
	}
</script>

<svelte:head><title>{workflow?.name ?? 'Workflow'} · Nashira</title></svelte:head>

<a
	href="/workflows"
	class="mb-3 inline-flex items-center gap-1.5 text-sm text-surface-600-400 hover:text-surface-950-50"
>
	<ArrowLeft size={15} />Workflows
</a>

{#if loading}
	<div class="flex justify-center py-16"><Spinner size="lg" /></div>
{:else if error}
	<ErrorState {error} onRetry={loadCore} />
{:else if workflow && plan}
	{@const w = workflow}
	<PageHeader title={w.name} description={w.description ?? ''}>
		{#snippet actions()}
			<Badge>{w.environment}</Badge>
			<span class="text-xs text-surface-600-400">v{w.version}</span>
			<Button variant="secondary" size="sm" loading={exporting} onclick={() => exportYaml(w)}>
				<Download size={14} />Export YAML
			</Button>
			<Button
				variant="secondary"
				size="sm"
				loading={bundling}
				title="Portable: carries the snippet, sub-workflow and trigger definitions this workflow needs, without any credentials."
				onclick={() => exportBundle(w)}
			>
				<Package size={14} />Export bundle
			</Button>
			<RoleGate require="operator">
				<Button variant="secondary" size="sm" loading={deleting} onclick={() => remove(w)}>
					<Trash2 size={14} />Delete
				</Button>
			</RoleGate>
		{/snippet}
	</PageHeader>

	<Tabs {tabs} bind:value={tab} />

	<div class="pt-4">
		{#if tab === 'plan'}
			<!-- Editing a step's snippet can change its idempotency, and the plan's
			     reversibility is computed from exactly that — so the plan is re-fetched
			     rather than left showing what was true before the edit. -->
			<WorkflowConfirmCard workflow={w} {plan} onsnippetsaved={refreshPlan} />
		{:else if tab === 'yaml'}
			{#if yamlLoading}
				<div class="flex justify-center py-10"><Spinner /></div>
			{:else if yamlError}
				<ErrorState error={yamlError} onRetry={loadYaml} compact />
			{:else}
				<pre
					class="overflow-x-auto rounded-xl border border-surface-200-800 bg-surface-100-900 p-4 text-xs leading-relaxed"><code>{yaml}</code></pre>
			{/if}
		{:else if tab === 'triggers'}
			<TriggerPanel workflowId={id} workflow={w} />
		{:else if tab === 'tests'}
			<TestPanel workflowId={id} workflow={w} mode="tests" />
		{:else if tab === 'versions'}
			<TestPanel workflowId={id} workflow={w} mode="versions" />
		{:else if tab === 'runs'}
			{#if runsLoading}
				<div class="flex justify-center py-10"><Spinner /></div>
			{:else if runsError}
				<ErrorState error={runsError} onRetry={loadRuns} compact />
			{:else if runs.length === 0}
				<p
					class="rounded-xl border border-surface-200-800 px-4 py-10 text-center text-sm text-surface-600-400"
				>
					No runs yet.
				</p>
			{:else}
				<!-- Each row opens the run on its own page. The detail used to expand in
				     place, after every step payload had arrived — megabytes for a
				     per-device run, and seconds of stall laying it out. The run page
				     fetches step headers first and each payload only when its card is
				     opened; hovering a row warms it. -->
				<div class="overflow-hidden rounded-xl border border-surface-200-800">
					{#each runs as r (r.id)}
						<a
							href={`/runs/${r.id}`}
							onmouseenter={() => prefetchFleetRun(r.id)}
							onfocus={() => prefetchFleetRun(r.id)}
							class="flex items-center gap-3 border-b border-surface-100-900 px-4 py-2.5 text-sm transition last:border-0 hover:bg-surface-100-900/60"
						>
							<StatusBadge status={r.status} />
							<span class="text-surface-700-300">{r.finalState}</span>
							{#if r.error}
								<span class="max-w-md truncate text-[11px] text-error-700-300" title={r.error}>
									{r.error}
								</span>
							{/if}
							<span class="ml-auto text-xs tabular-nums text-surface-600-400">{fmt(r.startedAt)}</span>
							<ChevronRight size={14} class="shrink-0 text-surface-600-400" />
						</a>
					{/each}
				</div>
			{/if}
		{/if}
	</div>
{/if}
