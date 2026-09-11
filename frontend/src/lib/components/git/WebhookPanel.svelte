<script lang="ts">
	// Inbound webhooks on a repository: a verified push pulls the working copy and
	// optionally runs a workflow.
	//
	// Two things this screen exists to keep visible, because both fail silently
	// otherwise: a webhook that accepts unsigned deliveries is an unauthenticated way
	// to run a workflow, and a branch filter is the usual reason a push "did nothing".
	// The badges say the first; the dry-run answers the second without needing a push.
	import {
		listWebhooks,
		createWebhook,
		updateWebhook,
		deleteWebhook,
		rotateWebhookSecret,
		listDeliveries,
		dryRunWebhook,
		type GitWebhook,
		type GitWebhookDelivery,
		type GitWebhookProvider,
		type WebhookPayload
	} from '$lib/api/git.api';
	import { listWorkflows, type WorkflowSummary } from '$lib/api/workflows.api';
	import {
		Alert,
		Badge,
		Button,
		Checkbox,
		ErrorState,
		IconButton,
		Input,
		Modal,
		Select,
		Spinner,
		confirm,
		toast,
		type Tone
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Plus, Pencil, Trash2, KeyRound, History, Webhook, FlaskConical } from 'lucide-svelte';

	let { repoId, defaultBranch = 'main' }: { repoId: string; defaultBranch?: string } = $props();

	let items = $state<GitWebhook[]>([]);
	let workflows = $state<WorkflowSummary[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<GitWebhook | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fProvider = $state<GitWebhookProvider>('github');
	let fWorkflowId = $state('');
	let fBranches = $state('');
	let fAutoPull = $state(true);
	let fEnabled = $state(true);
	let fAllowUnsigned = $state(false);

	// Shown exactly once, after create or rotate.
	let secretOpen = $state(false);
	let secretValue = $state('');
	let secretPath = $state('');
	let secretHeader = $state('');

	let deliveriesOpen = $state(false);
	let deliveriesFor = $state<GitWebhook | null>(null);
	let deliveries = $state<GitWebhookDelivery[]>([]);
	let deliveriesLoading = $state(false);

	let dryRunOpen = $state(false);
	let dryRunFor = $state<GitWebhook | null>(null);
	let dryRunBranch = $state('');
	let dryRunResult = $state<{ wouldDispatch: boolean; reason: string } | null>(null);
	let dryRunning = $state(false);

	const providerOptions = [
		{ value: 'github', label: 'GitHub' },
		{ value: 'gitlab', label: 'GitLab' },
		{ value: 'generic', label: 'Generic (HMAC)' }
	];

	const workflowOptions = $derived([
		{ value: '', label: '— pull only, run nothing —' },
		...workflows.map((w) => ({ value: w.id, label: w.name }))
	]);

	// The server returns a path, not a URL: it has no way to know which hostname it is
	// reachable on from GitHub. This page does.
	const origin = $derived(typeof location !== 'undefined' ? location.origin : '');
	const secretUrl = $derived(secretPath ? `${origin}${secretPath}` : '');

	function statusTone(s: string | null): Tone {
		if (s === 'dispatched') return 'success';
		if (s === 'rejected' || s === 'failed') return 'error';
		if (s === 'verified') return 'neutral';
		return 'neutral';
	}

	function fmt(iso: string | null): string {
		return iso ? new Date(iso).toLocaleString() : '—';
	}

	async function load() {
		loading = true;
		error = null;
		try {
			items = await listWebhooks(repoId);
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		repoId;
		load();
		// A picker with no options would read as "there are no workflows"; failing
		// quietly leaves the field empty, which is the same thing the user sees when
		// they genuinely have none.
		listWorkflows()
			.then((r) => (workflows = r.items))
			.catch(() => (workflows = []));
	});

	function resetForm() {
		formError = '';
		fName = '';
		fProvider = 'github';
		fWorkflowId = '';
		fBranches = defaultBranch;
		fAutoPull = true;
		fEnabled = true;
		fAllowUnsigned = false;
	}

	function openCreate() {
		editing = null;
		resetForm();
		modalOpen = true;
	}

	function openEdit(w: GitWebhook) {
		editing = w;
		formError = '';
		fName = w.name;
		fProvider = w.provider;
		fWorkflowId = w.onPushWorkflowId ?? '';
		fBranches = w.onPushBranches.join(', ');
		fAutoPull = w.autoPull;
		fEnabled = w.enabled;
		fAllowUnsigned = w.allowUnsigned;
		modalOpen = true;
	}

	function payload(): WebhookPayload {
		return {
			name: fName.trim(),
			provider: fProvider,
			onPushWorkflowId: fWorkflowId || null,
			onPushBranches: fBranches
				.split(',')
				.map((b) => b.trim())
				.filter(Boolean),
			autoPull: fAutoPull,
			enabled: fEnabled,
			allowUnsigned: fAllowUnsigned
		};
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		saving = true;
		formError = '';
		try {
			if (editing) {
				await updateWebhook(repoId, editing.id, payload());
				toast.success('Webhook updated');
			} else {
				const created = await createWebhook(repoId, payload());
				secretValue = created.secret ?? '';
				secretPath = created.ingestPath;
				secretHeader = created.signatureHeader;
				secretOpen = true;
			}
			modalOpen = false;
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't save the webhook");
		} finally {
			saving = false;
		}
	}

	async function rotate(w: GitWebhook) {
		const ok = await confirm({
			title: 'Rotate secret?',
			message: `The current secret for "${w.name}" stops working immediately. Pushes will be rejected until you update it on the provider side.`,
			tone: 'danger',
			confirmLabel: 'Rotate'
		});
		if (!ok) return;
		try {
			const updated = await rotateWebhookSecret(repoId, w.id);
			secretValue = updated.secret ?? '';
			secretPath = updated.ingestPath;
			secretHeader = updated.signatureHeader;
			secretOpen = true;
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't rotate the secret");
		}
	}

	async function remove(w: GitWebhook) {
		const ok = await confirm({
			title: 'Delete webhook?',
			message: `Deliveries to "${w.name}" will be rejected from now on. The webhook is still configured on the provider — remove it there too, or it will keep posting to a dead route.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteWebhook(repoId, w.id);
			items = items.filter((x) => x.id !== w.id);
			toast.success('Webhook deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the webhook");
		}
	}

	async function openDeliveries(w: GitWebhook) {
		deliveriesFor = w;
		deliveriesOpen = true;
		deliveriesLoading = true;
		deliveries = [];
		try {
			deliveries = await listDeliveries(repoId, w.id);
		} catch (e) {
			toast.fromError(e, "Couldn't load deliveries");
		} finally {
			deliveriesLoading = false;
		}
	}

	function openDryRun(w: GitWebhook) {
		dryRunFor = w;
		dryRunBranch = w.onPushBranches[0] ?? defaultBranch;
		dryRunResult = null;
		dryRunOpen = true;
	}

	async function runDryRun() {
		if (!dryRunFor) return;
		dryRunning = true;
		try {
			dryRunResult = await dryRunWebhook(repoId, dryRunFor.id, dryRunBranch.trim());
		} catch (e) {
			toast.fromError(e, "Couldn't evaluate the webhook");
		} finally {
			dryRunning = false;
		}
	}
</script>

<div class="space-y-3">
	<div class="flex items-center justify-between gap-3">
		<p class="text-sm text-surface-600-400">
			Inbound receivers. A verified push pulls this repository and can start a workflow.
		</p>
		<RoleGate require="admin">
			<Button variant="secondary" size="sm" onclick={openCreate}>
				<Plus size={14} />New webhook
			</Button>
		</RoleGate>
	</div>

	{#if loading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if error}
		<ErrorState {error} onRetry={load} compact />
	{:else if items.length === 0}
		<div
			class="rounded-xl border border-surface-200-800 px-4 py-10 text-center text-sm text-surface-600-400"
		>
			<Webhook size={20} class="mx-auto mb-2 opacity-60" />
			No webhooks. Nothing happens here when someone pushes to this repository.
		</div>
	{:else}
		<div class="space-y-2">
			{#each items as w (w.id)}
				<div class="rounded-xl border border-surface-200-800 p-3">
					<div class="flex items-start gap-3">
						<div class="min-w-0 flex-1">
							<div class="flex flex-wrap items-center gap-2">
								<span class="font-medium">{w.name}</span>
								<Badge>{w.provider}</Badge>
								{#if !w.enabled}<Badge tone="warning">disabled</Badge>{/if}
								{#if !w.hasSecret && w.allowUnsigned}
									<Badge tone="error">unauthenticated</Badge>
								{/if}
							</div>

							<div class="mt-1 text-xs text-surface-600-400">
								<code class="break-all">{w.ingestPath}</code> · signed via
								<code>{w.signatureHeader}</code>
							</div>

							<div class="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-surface-600-400">
								<span>
									fires for
									{#if w.onPushBranches.length === 0}
										<strong>every branch</strong>
									{:else}
										<strong>{w.onPushBranches.join(', ')}</strong>
									{/if}
								</span>
								<span>·</span>
								<span>{w.autoPull ? 'pulls first' : 'no pull'}</span>
								<span>·</span>
								{#if w.onPushWorkflowName}
									<span>runs <strong>{w.onPushWorkflowName}</strong></span>
								{:else}
									<span>runs nothing</span>
								{/if}
							</div>

							<div class="mt-1 flex flex-wrap items-center gap-2 text-xs text-surface-600-400">
								<span>{w.deliveryCount} deliver{w.deliveryCount === 1 ? 'y' : 'ies'}</span>
								{#if w.lastDeliveryStatus}
									<Badge tone={statusTone(w.lastDeliveryStatus)}>{w.lastDeliveryStatus}</Badge>
									<span>{fmt(w.lastDeliveryAt)}</span>
								{/if}
							</div>
						</div>

						<div class="flex gap-1">
							<IconButton label={`Deliveries for ${w.name}`} onclick={() => openDeliveries(w)}>
								<History size={14} />
							</IconButton>
							<IconButton label={`Test ${w.name}`} onclick={() => openDryRun(w)}>
								<FlaskConical size={14} />
							</IconButton>
							<RoleGate require="admin">
								<IconButton label={`Rotate ${w.name} secret`} onclick={() => rotate(w)}>
									<KeyRound size={14} />
								</IconButton>
								<IconButton label="Edit webhook" onclick={() => openEdit(w)}>
									<Pencil size={14} />
								</IconButton>
								<IconButton label="Delete webhook" onclick={() => remove(w)}>
									<Trash2 size={14} />
								</IconButton>
							</RoleGate>
						</div>
					</div>
				</div>
			{/each}
		</div>
	{/if}
</div>

<Modal bind:open={modalOpen} title={editing ? `Edit ${editing.name}` : 'New webhook'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input label="Name" bind:value={fName} required />

		{#if editing}
			<p class="text-xs text-surface-600-400">
				The provider (<code>{editing.provider}</code>) cannot be changed — it decides which header
				the sender signs with, so switching it would start rejecting every delivery from a hook
				that is still configured the old way on the other side.
			</p>
		{:else}
			<Select label="Provider" bind:value={fProvider} options={providerOptions} />
		{/if}

		<Select
			label="Run on push"
			bind:value={fWorkflowId}
			options={workflowOptions}
			hint="The run receives repository_id, branch and commit_sha as its input."
		/>

		<Input
			label="Branches"
			bind:value={fBranches}
			hint="Comma-separated, matched exactly. Leave empty to fire on every branch. A tag push carries no branch, so a filtered webhook ignores tags."
		/>

		<Checkbox bind:checked={fAutoPull} label="Pull the working copy before running" />
		<Checkbox bind:checked={fEnabled} label="Enabled" />
		<Checkbox bind:checked={fAllowUnsigned} label="Accept unsigned deliveries (testing only)" />
		{#if fAllowUnsigned}
			<Alert tone="error">
				This only takes effect if the webhook has no secret. When it does, anyone who learns the
				URL can pull this repository and start the workflow above.
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<Modal bind:open={secretOpen} title="Webhook secret" size="lg">
	<div class="space-y-3">
		<Alert tone="warning">
			This is the only time the secret is shown. Nothing returns it again — if you lose it, you
			have to rotate.
		</Alert>

		<div>
			<div class="mb-1 text-sm font-medium">Payload URL</div>
			<code
				class="block overflow-x-auto rounded-lg border border-surface-200-800 bg-surface-100-900 p-2 text-xs"
				>{secretUrl}</code
			>
		</div>

		<div>
			<div class="mb-1 text-sm font-medium">Secret</div>
			<code
				class="block overflow-x-auto rounded-lg border border-surface-200-800 bg-surface-100-900 p-2 text-xs"
				>{secretValue}</code
			>
		</div>

		<p class="text-xs text-surface-600-400">
			In the repository's webhook settings, use that URL with content type
			<code>application/json</code> and paste the secret. The signature arrives as
			<code>{secretHeader}</code>. Until it is configured there, deliveries are rejected.
		</p>
	</div>

	{#snippet footer()}
		<Button variant="primary" onclick={() => (secretOpen = false)}>Done</Button>
	{/snippet}
</Modal>

<Modal
	bind:open={deliveriesOpen}
	title={deliveriesFor ? `Deliveries · ${deliveriesFor.name}` : 'Deliveries'}
	size="lg"
>
	{#if deliveriesLoading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if deliveries.length === 0}
		<p class="py-8 text-center text-sm text-surface-600-400">
			Nothing has been delivered to this webhook yet. If you expected a push to arrive, check that
			the URL and secret are configured on the provider.
		</p>
	{:else}
		<div class="space-y-2">
			{#each deliveries as d (d.id)}
				<div class="rounded-lg border border-surface-200-800 p-2 text-xs">
					<div class="flex flex-wrap items-center gap-2">
						<Badge tone={statusTone(d.status)}>{d.status}</Badge>
						<span class="text-surface-600-400">{fmt(d.at)}</span>
						{#if d.event}<code>{d.event}</code>{/if}
						{#if d.branch}<span>on <strong>{d.branch}</strong></span>{/if}
						{#if d.commitSha}<code class="text-surface-600-400">{d.commitSha.slice(0, 7)}</code>{/if}
					</div>
					{#if d.error}
						<p class="mt-1 text-error-600-400">{d.error}</p>
					{/if}
				</div>
			{/each}
		</div>
		<p class="mt-3 text-xs text-surface-600-400">
			Bodies and headers are not stored — a push payload carries commit messages and author
			emails. Deliveries older than 30 days are pruned.
		</p>
	{/if}

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (deliveriesOpen = false)}>Close</Button>
	{/snippet}
</Modal>

<Modal
	bind:open={dryRunOpen}
	title={dryRunFor ? `Test · ${dryRunFor.name}` : 'Test webhook'}
	size="md"
>
	<div class="space-y-3">
		<p class="text-sm text-surface-600-400">
			Answers what a push to a branch would do. Nothing is pulled and nothing is enqueued.
		</p>
		<Input label="Branch" bind:value={dryRunBranch} placeholder={defaultBranch} />
		{#if dryRunResult}
			<Alert tone={dryRunResult.wouldDispatch ? 'success' : 'warning'}>
				{dryRunResult.reason}
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (dryRunOpen = false)}>Close</Button>
		<Button variant="primary" loading={dryRunning} onclick={runDryRun}>Evaluate</Button>
	{/snippet}
</Modal>
