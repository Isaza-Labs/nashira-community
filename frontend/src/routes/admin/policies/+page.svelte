<script lang="ts">
	// Guardrail editor. Every policy is one expandable card: the header says what it
	// does and whether it is armed, and opening it edits the rule in place — either
	// through the guided builder or as raw JSON, backed by the same document.
	//
	// Editing in the row rather than in a dialog is deliberate. A policy is read far
	// more often than it is written, and the question people arrive with is "what
	// does this one actually block?" — which a modal answers only after you have
	// decided to change something.
	import { page } from '$app/state';
	import {
		listPolicies,
		createPolicy,
		updatePolicy,
		setPolicyEnabled,
		deletePolicy,
		evaluatePolicy,
		type Policy,
		type PolicyDecision
	} from '$lib/api/policies.api';
	import {
		PageHeader,
		Button,
		IconButton,
		Badge,
		Alert,
		EmptyState,
		ErrorState,
		Modal,
		Tabs,
		Input,
		Select,
		Checkbox,
		CodeEditor,
		Spinner,
		confirm,
		toast
	} from '$lib/components/ui';
	import RuleBuilder from '$lib/components/policy/RuleBuilder.svelte';
	import {
		Plus,
		Trash2,
		Power,
		Save,
		RefreshCw,
		FlaskConical,
		ScrollText,
		ChevronRight
	} from 'lucide-svelte';

	// One editing buffer, used for both the create form and each open row. `tab` and
	// `notice` live here rather than in a single page-level pair so two open rows do
	// not fight over which one is showing JSON.
	type Draft = {
		name: string;
		description: string;
		rule: string;
		enabled: boolean;
		tab: string;
		notice: string | null;
		error: string;
		saving: boolean;
	};

	const DENY_TEMPLATE = `{
  "action": "deny",
  "reason": "core routers need a change window",
  "when": {
    "environment": ["production"],
    "device_role": ["core"]
  }
}`;

	const TABS = [
		{ value: 'visual', label: 'Visual' },
		{ value: 'json', label: 'JSON' }
	];

	let items = $state<Policy[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let creating = $state<Draft | null>(null);
	let expanded = $state<string | null>(null);
	let drafts = $state<Record<string, Draft>>({});
	let busy = $state<Record<string, boolean>>({});

	// Dry-run panel.
	let testOpen = $state(false);
	let testRule = $state('');
	let testEnv = $state('production');
	let testDescription = $state('');
	let testRoles = $state('');
	let testTypes = $state('');
	let testing = $state(false);
	let decision = $state<PolicyDecision | null>(null);

	const envOptions = [
		{ value: 'draft', label: 'draft' },
		{ value: 'qa', label: 'qa' },
		{ value: 'production', label: 'production' }
	];

	function newDraft(p?: Policy): Draft {
		return {
			name: p?.name ?? '',
			description: p?.description ?? '',
			rule: p ? JSON.stringify(p.rule, null, 2) : DENY_TEMPLATE,
			// A new guardrail is born disarmed on purpose — one first exercised in
			// production is one nobody has read carefully.
			enabled: p?.enabled ?? false,
			tab: 'visual',
			notice: null,
			error: '',
			saving: false
		};
	}

	function shapeOf(p: Policy): string {
		return (p.rule as { action?: string } | null)?.action === 'gate' ? 'gate' : 'deny';
	}

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listPolicies()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !creating) creating = newDraft();
	});

	async function create() {
		const draft = creating;
		if (!draft) return;
		if (!draft.name.trim()) {
			draft.error = 'Name is required.';
			return;
		}
		draft.error = '';
		draft.saving = true;
		try {
			await createPolicy({
				name: draft.name.trim(),
				description: draft.description.trim(),
				rule: draft.rule,
				enabled: draft.enabled
			});
			toast.success(`Policy "${draft.name.trim()}" created`);
			creating = null;
			await load();
		} catch (e) {
			if (e instanceof SyntaxError) draft.error = `Rule: ${e.message}`;
			else toast.fromError(e, "Couldn't create the policy");
		} finally {
			draft.saving = false;
		}
	}

	function toggleExpand(p: Policy) {
		if (expanded === p.id) {
			expanded = null;
			return;
		}
		expanded = p.id;
		// Seeded once, so an edit survives collapsing and reopening the row.
		if (!drafts[p.id]) drafts = { ...drafts, [p.id]: newDraft(p) };
	}

	async function save(p: Policy) {
		const draft = drafts[p.id];
		if (!draft) return;
		if (!draft.name.trim()) {
			draft.error = 'Name is required.';
			return;
		}
		draft.error = '';
		draft.saving = true;
		try {
			await updatePolicy(p.id, {
				name: draft.name.trim(),
				description: draft.description.trim(),
				rule: draft.rule,
				enabled: draft.enabled
			});
			toast.success('Policy updated');
			await load();
		} catch (e) {
			if (e instanceof SyntaxError) draft.error = `Rule: ${e.message}`;
			else toast.fromError(e, "Couldn't save the policy");
		} finally {
			draft.saving = false;
		}
	}

	async function toggleEnabled(p: Policy) {
		const next = !p.enabled;
		// Arming takes effect on the very next run and promotion; disarming lets
		// through whatever it was holding back. Either direction is worth a beat.
		const ok = await confirm({
			title: next ? 'Arm this policy?' : 'Disarm this policy?',
			message: next
				? `"${p.name}" starts being enforced immediately, on every run and promotion.`
				: `"${p.name}" stops guarding. Anything it was blocking becomes possible again.`,
			tone: next ? 'primary' : 'danger',
			confirmLabel: next ? 'Arm' : 'Disarm'
		});
		if (!ok) return;

		busy = { ...busy, [p.id]: true };
		try {
			await setPolicyEnabled(p.id, next);
			items = items.map((x) => (x.id === p.id ? { ...x, enabled: next } : x));
			if (drafts[p.id]) drafts[p.id].enabled = next;
			toast.success(next ? `"${p.name}" armed` : `"${p.name}" disarmed`);
		} catch (e) {
			toast.fromError(e, "Couldn't change the policy");
		} finally {
			busy = { ...busy, [p.id]: false };
		}
	}

	async function remove(p: Policy) {
		const ok = await confirm({
			title: 'Delete policy?',
			message: `"${p.name}" will stop guarding. Anything it was blocking becomes possible again.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deletePolicy(p.id);
			items = items.filter((x) => x.id !== p.id);
			if (expanded === p.id) expanded = null;
			toast.success('Policy deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the policy");
		}
	}

	function openTest(p?: Policy) {
		testRule = p ? JSON.stringify(p.rule, null, 2) : (creating?.rule ?? '');
		decision = null;
		testOpen = true;
	}

	async function runTest() {
		testing = true;
		decision = null;
		try {
			decision = await evaluatePolicy({
				rule: testRule.trim() || undefined,
				environment: testEnv,
				workflowDescription: testDescription,
				deviceRoles: split(testRoles),
				devicePools: [],
				snippetTypes: split(testTypes)
			});
		} catch (e) {
			if (e instanceof SyntaxError) toast.error(`Rule: ${e.message}`);
			else toast.fromError(e, "Couldn't evaluate the policy");
		} finally {
			testing = false;
		}
	}

	function split(raw: string): string[] {
		return raw
			.split(',')
			.map((s) => s.trim())
			.filter(Boolean);
	}
</script>

<svelte:head><title>Policies · Nashira</title></svelte:head>

<PageHeader
	title="Policies"
	description="Guardrails checked before a run executes and before a workflow is promoted."
>
	{#snippet actions()}
		<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
		<Button variant="ghost" href="/admin/audit?entityType=policy">
			<ScrollText size={15} />Audit
		</Button>
		<Button variant="secondary" onclick={() => openTest()}>
			<FlaskConical size={15} />Dry run
		</Button>
		<Button variant="primary" onclick={() => (creating = creating ? null : newDraft())}>
			<Plus size={15} />New policy
		</Button>
	{/snippet}
</PageHeader>

<div class="space-y-3">
	<Alert tone="neutral">
		Default-allow, opt-in-deny. A <code>deny</code> blocks a run; a <code>gate</code> blocks a
		promotion until the workflow has earned it. A rule that cannot be evaluated
		<strong>blocks</strong> and names itself, rather than silently letting work through.
	</Alert>

	{#if creating}
		<div class="ui-surface overflow-hidden">
			<div class="flex items-center justify-between gap-3 border-b border-surface-200-800 px-4 py-3">
				<h2 class="text-sm font-medium">New policy</h2>
				<Button variant="ghost" size="sm" onclick={() => (creating = null)}>Cancel</Button>
			</div>

			<div class="space-y-3 p-4">
				{#if creating.error}<Alert tone="error">{creating.error}</Alert>{/if}

				<div class="grid gap-3 sm:grid-cols-2">
					<Input
						label="Name"
						bind:value={creating.name}
						required
						placeholder="no-ssh-in-production"
						hint="Quoted back to the operator on a refusal"
					/>
					<Input label="Description" bind:value={creating.description} />
				</div>

				<div>
					<Tabs bind:value={creating.tab} tabs={TABS} />
					<div class="mt-3 space-y-3">
						{#if creating.notice}
							<Alert tone="warning">{creating.notice}</Alert>
						{/if}
						{#if creating.tab === 'visual'}
							<RuleBuilder
								bind:ruleJson={creating.rule}
								onNotice={(m) => {
									if (creating) creating.notice = m;
								}}
							/>
						{:else}
							<CodeEditor label="Rule" language="json" bind:value={creating.rule} rows={14} />
						{/if}
					</div>
				</div>

				<Checkbox bind:checked={creating.enabled} label="Armed" />
				{#if creating.enabled}
					<Alert tone="warning">
						An armed policy takes effect immediately for every run and promotion. Dry-run it first.
					</Alert>
				{:else}
					<Alert tone="neutral">Disarmed: stored but not enforced.</Alert>
				{/if}
			</div>

			<div
				class="flex items-center justify-end gap-2 border-t border-surface-200-800 px-4 py-3"
			>
				<Button variant="secondary" onclick={() => openTest()}>
					<FlaskConical size={15} />Dry run
				</Button>
				<Button variant="primary" loading={creating.saving} onclick={create}>
					<Save size={15} />Create
				</Button>
			</div>
		</div>
	{/if}

	{#if error}
		<ErrorState {error} onRetry={load} />
	{:else if loading}
		<div class="flex justify-center py-12"><Spinner size="lg" /></div>
	{:else if items.length === 0}
		<div class="ui-surface">
			<EmptyState
				title="No policies. Nothing is blocked."
				description="A policy encodes a rule the team already has — core routers only inside a change window, production only after a week of clean qa — so the engine enforces it instead of whoever is on shift."
			>
				{#snippet actions()}
					<Button variant="primary" onclick={() => (creating = newDraft())}>
						<Plus size={15} />Create the first policy
					</Button>
				{/snippet}
			</EmptyState>
		</div>
	{:else}
		<div class="space-y-2">
			{#each items as p (p.id)}
				{@const open = expanded === p.id}
				{@const draft = drafts[p.id]}
				<div class="ui-surface overflow-hidden">
					<div class="flex items-stretch">
						<button
							type="button"
							aria-expanded={open}
							onclick={() => toggleExpand(p)}
							class="flex min-w-0 flex-1 items-center gap-2 px-4 py-3 text-left transition hover:bg-surface-100-900"
						>
							<ChevronRight
								size={14}
								class="shrink-0 text-surface-600-400 transition-transform {open ? 'rotate-90' : ''}"
							/>
							<span class="truncate font-medium">{p.name}</span>
							{#if shapeOf(p) === 'gate'}
								<Badge tone="primary">gate</Badge>
							{:else}
								<Badge tone="neutral">deny</Badge>
							{/if}
							<Badge tone={p.enabled ? 'success' : 'neutral'}>
								{p.enabled ? 'armed' : 'disarmed'}
							</Badge>
							{#if p.description}
								<span class="hidden truncate text-xs text-surface-600-400 sm:inline">
									{p.description}
								</span>
							{/if}
						</button>

						<div class="flex items-center gap-1 border-l border-surface-200-800 px-2">
							<IconButton label={`Dry-run ${p.name}`} onclick={() => openTest(p)}>
								<FlaskConical size={14} />
							</IconButton>
							<IconButton
								label={p.enabled ? `Disarm ${p.name}` : `Arm ${p.name}`}
								variant={p.enabled ? 'secondary' : 'ghost'}
								disabled={busy[p.id]}
								onclick={() => toggleEnabled(p)}
							>
								<Power size={14} />
							</IconButton>
							<IconButton label={`Delete ${p.name}`} onclick={() => remove(p)}>
								<Trash2 size={14} />
							</IconButton>
						</div>
					</div>

					{#if open && draft}
						<div class="space-y-3 border-t border-surface-200-800 p-4">
							{#if draft.error}<Alert tone="error">{draft.error}</Alert>{/if}

							<div class="grid gap-3 sm:grid-cols-2">
								<Input label="Name" bind:value={draft.name} required />
								<Input label="Description" bind:value={draft.description} />
							</div>

							<div>
								<Tabs bind:value={draft.tab} tabs={TABS} />
								<div class="mt-3 space-y-3">
									{#if draft.notice}
										<Alert tone="warning">{draft.notice}</Alert>
									{/if}
									{#if draft.tab === 'visual'}
										<RuleBuilder
											bind:ruleJson={draft.rule}
											onNotice={(m) => {
												if (drafts[p.id]) drafts[p.id].notice = m;
											}}
										/>
									{:else}
										<CodeEditor label="Rule" language="json" bind:value={draft.rule} rows={14} />
									{/if}
								</div>
							</div>

							<Checkbox bind:checked={draft.enabled} label="Armed" />

							<div class="flex justify-end gap-2">
								<Button variant="secondary" onclick={() => openTest(p)}>
									<FlaskConical size={15} />Dry run
								</Button>
								<Button variant="primary" loading={draft.saving} onclick={() => save(p)}>
									<Save size={15} />Save changes
								</Button>
							</div>
						</div>
					{/if}
				</div>
			{/each}
		</div>

		<p class="text-xs text-surface-600-400">
			{items.length}
			{items.length === 1 ? 'policy' : 'policies'} ·
			{items.filter((p) => p.enabled).length} armed
		</p>
	{/if}
</div>

<Modal bind:open={testOpen} title="Dry run" size="lg">
	<div class="space-y-3">
		<p class="text-xs text-surface-600-400">
			Describe a hypothetical run and see whether it would be refused. Leave the rule blank to
			evaluate every armed policy instead of one draft.
		</p>

		<CodeEditor
			label="Rule (blank = all armed policies)"
			language="json"
			bind:value={testRule}
			rows={10}
		/>

		<div class="grid gap-3 sm:grid-cols-2">
			<Select label="Environment" bind:value={testEnv} options={envOptions} />
			<Input label="Device roles" bind:value={testRoles} hint="Comma-separated, e.g. core, edge" />
		</div>
		<Input label="Snippet types" bind:value={testTypes} hint="Comma-separated, e.g. ssh, rest_call" />
		<Input label="Workflow description" bind:value={testDescription} />

		{#if decision}
			{#if decision.denied}
				<Alert tone="error">
					<div class="font-medium">Refused{decision.policy ? ` by "${decision.policy}"` : ''}</div>
					<div class="mt-0.5 text-xs">{decision.reason}</div>
				</Alert>
			{:else}
				<Alert tone="success">Allowed — nothing blocks this.</Alert>
			{/if}
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (testOpen = false)}>Close</Button>
		<Button variant="primary" loading={testing} onclick={runTest}>Evaluate</Button>
	{/snippet}
</Modal>
