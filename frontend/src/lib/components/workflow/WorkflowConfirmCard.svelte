<script lang="ts">
	// Governance-C confirmation surface: shows the materialized execution plan
	// (topological order) and rollback safety, then gates a confirmed run behind the
	// Operator role. The run executes synchronously server-side; we then load its
	// detail (steps) and show the outcome.
	//
	// The plan's SHAPE is not editable here — nodes and edges belong to the builder.
	// A step's snippet is, because that is what a reader looking at a failed plan
	// actually wants to change, and walking to /admin/snippets to find it by name
	// loses which node they were looking at. Same editor as that page, Operator-gated
	// there and here, and the API gate is the one that counts.
	import {
		runWorkflow,
		getRun,
		type Workflow,
		type WorkflowPlan,
		type WorkflowRunDetail,
		type RunWorkflowPayload
	} from '$lib/api/workflows.api';
	import { getSnippet, type Snippet } from '$lib/api/snippets.api';
	import { Alert, Badge, Button, IconButton, Spinner, toast } from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import SnippetEditorModal from '$lib/components/snippet/SnippetEditorModal.svelte';
	import RunResult from './RunResult.svelte';
	import RunWorkflowDialog from './RunWorkflowDialog.svelte';
	import { ShieldCheck, ShieldAlert, Play, Pencil } from 'lucide-svelte';

	let {
		workflow,
		plan,
		onsnippetsaved
	}: {
		workflow: Workflow;
		plan: WorkflowPlan;
		/**
		 * Called after a snippet is saved from here. The caller must re-fetch the plan:
		 * reversibility is computed from the snippets' idempotency, so editing one can
		 * change what the banner above and the run button below are claiming.
		 */
		onsnippetsaved?: () => void;
	} = $props();

	const nodeById = $derived(new Map(workflow.nodes.map((n) => [n.id, n])));
	const nonReversible = $derived(new Set(plan.nonReversibleNodes));

	let running = $state(false);
	let run = $state<WorkflowRunDetail | null>(null);
	// The dialog is the confirmation: it states the rollback risk and collects what
	// the run needs in one step, so the user is never asked to accept the risk before
	// seeing what the run will target.
	let dialogOpen = $state(false);

	// A node carries only `snippet_id`, so the definition has to be fetched before
	// the editor has anything to show.
	let editorOpen = $state(false);
	let editing = $state<Snippet | null>(null);
	let loadingFor = $state<string | null>(null);

	async function editSnippet(nodeId: string, snippetId: string) {
		loadingFor = nodeId;
		try {
			editing = await getSnippet(snippetId);
			editorOpen = true;
		} catch (e) {
			toast.fromError(e, "Couldn't load the snippet for this step");
		} finally {
			loadingFor = null;
		}
	}

	async function onRun(payload: RunWorkflowPayload) {
		running = true;
		run = null;
		try {
			const started = await runWorkflow(workflow.id, payload);
			// The run completes synchronously; fetch the detail for its steps.
			run = await getRun(started.id);
			dialogOpen = false;
			toast.success('Workflow executed', { description: `Final state: ${run.finalState}` });
		} catch (e) {
			// The dialog stays open on failure: a rejected target or a malformed input is
			// fixed in the form that produced it, not by reopening and re-entering it.
			toast.fromError(e, 'Failed to run the workflow');
		} finally {
			running = false;
		}
	}
</script>

<div class="space-y-4">
	{#if plan.rollbackReversible}
		<Alert tone="success" title="Fully reversible">
			Every step can be automatically rolled back if a later step fails.
		</Alert>
	{:else}
		<Alert tone="warning" title="Contains non-reversible steps">
			{plan.nonReversibleNodes.length}
			{plan.nonReversibleNodes.length === 1 ? 'step' : 'steps'} cannot be undone automatically. If a
			later step fails, they stay applied.
		</Alert>
	{/if}

	<div class="overflow-hidden rounded-xl border border-surface-200-800">
		<div class="flex items-center justify-between border-b border-surface-200-800 px-4 py-2.5">
			<h3 class="text-sm font-semibold">Execution plan</h3>
			<span class="text-xs text-surface-600-400">
				{plan.order.length}
				{plan.order.length === 1 ? 'step' : 'steps'}, topological order
			</span>
		</div>
		{#if plan.order.length === 0}
			<p class="px-4 py-3 text-xs text-surface-600-400">This workflow has no nodes.</p>
		{:else}
			<ol class="divide-y divide-surface-100-900">
				{#each plan.order as id, i (id)}
					{@const node = nodeById.get(id)}
					{@const irreversible = nonReversible.has(id)}
					{@const snippetId = node?.snippetId ?? ''}
					<li class="flex items-center gap-3 px-4 py-2.5 text-sm">
						<span
							class="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-surface-100-900 text-xs tabular-nums text-surface-600-400"
						>
							{i + 1}
						</span>
						<div class="min-w-0 flex-1">
							<div class="flex items-center gap-2">
								<span class="font-medium">{node?.type ?? 'node'}</span>
								{#if irreversible}<Badge tone="warning">non-reversible</Badge>{/if}
							</div>
							<code class="text-xs text-surface-600-400">{id}</code>
						</div>
						{#if snippetId}
							<RoleGate require="operator">
								{#if loadingFor === id}
									<Spinner size="sm" class="shrink-0 text-surface-600-400" />
								{:else}
									<IconButton label="Edit this step's snippet" onclick={() => editSnippet(id, snippetId)}>
										<Pencil size={14} />
									</IconButton>
								{/if}
							</RoleGate>
						{/if}
						{#if irreversible}
							<ShieldAlert size={15} class="shrink-0 text-warning-500" />
						{:else}
							<ShieldCheck size={15} class="shrink-0 text-surface-400" />
						{/if}
					</li>
				{/each}
			</ol>
		{/if}
	</div>

	<div class="flex flex-wrap items-center justify-between gap-3">
		<p class="text-xs text-surface-600-400">Running executes every step against live systems.</p>
		<RoleGate require="operator">
			{#snippet fallback()}
				<span class="text-xs text-surface-600-400">Operator role required to run.</span>
			{/snippet}
			<Button
				variant={plan.rollbackReversible ? 'primary' : 'danger'}
				loading={running}
				disabled={running || plan.order.length === 0}
				onclick={() => (dialogOpen = true)}
			>
				<Play size={15} />Run workflow
			</Button>
		</RoleGate>
	</div>

	<RunWorkflowDialog bind:open={dialogOpen} {workflow} {plan} {running} onrun={onRun} />

	<SnippetEditorModal bind:open={editorOpen} snippet={editing} onsaved={() => onsnippetsaved?.()} />

	{#if run}
		<div class="space-y-2">
			<div class="flex items-center justify-between gap-3">
				<h3 class="text-sm font-semibold">Run result</h3>
				<a href={`/runs/${run.id}`} class="text-xs text-primary-700-300 hover:underline">Open run page</a>
			</div>
			<RunResult {run} />
		</div>
	{/if}
</div>
