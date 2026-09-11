<script lang="ts">
	import { page } from '$app/state';
	import {
		listChannels,
		createChannel,
		updateChannel,
		deleteChannel,
		sendMessage,
		checkChannel,
		listDeliveries,
		CHANNEL_KINDS,
		type NotificationChannel,
		type ChannelPayload,
		type Delivery
	} from '$lib/api/notifications.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Textarea,
		Select,
		CodeEditor,
		Checkbox,
		Alert,
		Spinner,
		ErrorState,
		confirm,
		toast,
		type Column,
		type Tone
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Plus, Pencil, Trash2, Activity, Send, History } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	let items = $state<NotificationChannel[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let checkingId = $state<string | null>(null);

	let modalOpen = $state(false);
	let editing = $state<NotificationChannel | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fKind = $state('slack');
	let fDescription = $state('');
	let fWebhookUrl = $state('');
	let fHeaders = $state('');
	let fAllowPrivate = $state(false);
	let fEnabled = $state(true);

	let sendOpen = $state(false);
	let sendFor = $state<NotificationChannel | null>(null);
	let sendText = $state('');
	let sending = $state(false);

	let historyOpen = $state(false);
	let historyFor = $state<NotificationChannel | null>(null);
	let deliveries = $state<Delivery[]>([]);
	let historyLoading = $state(false);

	const kindOptions = CHANNEL_KINDS.map((k) => ({ value: k, label: k }));

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'kind', header: 'Kind' },
		{ key: 'target', header: 'Target' },
		{ key: 'status', header: 'Status' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	function statusTone(s: string): Tone {
		if (s === 'healthy') return 'success';
		if (s === 'unreachable') return 'error';
		return 'neutral';
	}

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listChannels()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function openCreate() {
		editing = null;
		formError = '';
		fName = '';
		fKind = 'slack';
		fDescription = '';
		fWebhookUrl = '';
		fHeaders = '';
		fAllowPrivate = false;
		fEnabled = true;
		modalOpen = true;
	}

	function openEdit(c: NotificationChannel) {
		editing = c;
		formError = '';
		fName = c.name;
		fKind = c.kind;
		fDescription = c.description ?? '';
		// Deliberately blank: the API never returns it, so pre-filling a placeholder
		// would let an unrelated edit overwrite the real URL with the placeholder.
		fWebhookUrl = '';
		fHeaders = c.headers ?? '';
		fAllowPrivate = c.allowPrivateNetwork;
		fEnabled = c.enabled;
		modalOpen = true;
	}

	function payload(): ChannelPayload {
		return {
			name: fName.trim(),
			kind: fKind,
			description: fDescription,
			webhookUrl: fWebhookUrl,
			headers: fHeaders,
			allowPrivateNetwork: fAllowPrivate,
			enabled: fEnabled
		};
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		if (!editing && !fWebhookUrl.trim()) {
			formError = 'A webhook URL is required.';
			return;
		}
		formError = '';
		saving = true;
		try {
			if (editing) await updateChannel(editing.id, payload());
			else await createChannel(payload());
			modalOpen = false;
			toast.success(editing ? 'Channel updated' : 'Channel created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the channel');
		} finally {
			saving = false;
		}
	}

	async function remove(c: NotificationChannel) {
		const ok = await confirm({
			title: 'Delete channel?',
			message: `"${c.name}" will stop receiving notifications. Its delivery history goes with it.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteChannel(c.id);
			items = items.filter((x) => x.id !== c.id);
			toast.success('Channel deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the channel");
		}
	}

	async function check(c: NotificationChannel) {
		checkingId = c.id;
		try {
			const r = await checkChannel(c.id);
			// A check posts a real message — say so, so nobody wonders where it came from.
			if (r.status === 'healthy') toast.success(`${c.name}: delivered (${r.elapsedMs} ms)`);
			else toast.error(`${c.name}: ${r.error ?? r.status}`);
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't reach the channel");
		} finally {
			checkingId = null;
		}
	}

	function openSend(c: NotificationChannel) {
		sendFor = c;
		sendText = '';
		sendOpen = true;
	}

	async function doSend() {
		if (!sendFor || !sendText.trim()) return;
		sending = true;
		try {
			const r = await sendMessage(sendFor.id, sendText);
			if (r.success) {
				toast.success(`Delivered in ${r.elapsedMs} ms`);
				sendOpen = false;
			} else {
				toast.error(r.error ?? 'Delivery failed');
			}
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't send the message");
		} finally {
			sending = false;
		}
	}

	async function openHistory(c: NotificationChannel) {
		historyFor = c;
		deliveries = [];
		historyOpen = true;
		historyLoading = true;
		try {
			deliveries = (await listDeliveries(c.id)).items;
		} catch (e) {
			toast.fromError(e, "Couldn't load the delivery history");
		} finally {
			historyLoading = false;
		}
	}

	function fmt(iso: string): string {
		return new Date(iso).toLocaleString();
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Notifications · Nashira</title></svelte:head>

<SectionNav id="integrations" />

<PageHeader
	title="Notifications"
	description="Outbound notification channels — Slack, Teams, or a generic webhook."
>
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New channel</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

<RoleGate require="admin">
	{#snippet fallback()}
		<div class="ui-surface px-4 py-16 text-center text-sm text-surface-600-400">
			Administrator access is required for this area.
		</div>
	{/snippet}

	{#if error}
		<ErrorState {error} onRetry={load} />
	{:else}
		<div class="space-y-3">
			<Alert tone="neutral">
				For Slack and Teams the webhook URL <strong>is</strong> the credential — anyone holding it can
				post to the channel — so it is stored encrypted and never returned.
			</Alert>

			<DataTable {loading} {columns} rows={items} rowKey={(c) => c.id} empty="No channels yet — add one so a run can tell somebody what happened.">
				{#snippet cell(row, col)}
					{#if col.key === 'name'}
						<div class="font-medium">{row.name}</div>
						{#if row.description}
							<div class="max-w-[22rem] truncate text-xs text-surface-600-400">{row.description}</div>
						{/if}
					{:else if col.key === 'kind'}
						<Badge>{row.kind}</Badge>
					{:else if col.key === 'target'}
						{#if row.hasWebhookUrl}
							<code class="text-xs text-surface-600-400">{row.targetHost ?? '—'}</code>
						{:else}
							<span class="text-xs text-warning-600-400">no URL set</span>
						{/if}
					{:else if col.key === 'status'}
						<div class="flex items-center gap-1.5">
							<Badge tone={statusTone(row.status)}>{row.status}</Badge>
							{#if !row.enabled}<Badge tone="warning">disabled</Badge>{/if}
						</div>
						{#if row.lastCheckError}
							<div class="mt-0.5 max-w-[26rem] truncate text-xs text-error-600-400" title={row.lastCheckError}>
								{row.lastCheckError}
							</div>
						{/if}
					{:else if col.key === 'actions'}
						<div class="flex justify-end gap-1">
							<IconButton label={`Delivery history for ${row.name}`} onclick={() => openHistory(row)}>
								<History size={14} />
							</IconButton>
							<IconButton label={`Send a message to ${row.name}`} onclick={() => openSend(row)}>
								<Send size={14} />
							</IconButton>
							<IconButton
								label={`Test ${row.name}`}
								disabled={checkingId === row.id}
								onclick={() => check(row)}
							>
								{#if checkingId === row.id}<Spinner size="sm" />{:else}<Activity size={14} />{/if}
							</IconButton>
							<IconButton label="Edit channel" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
							<IconButton label="Delete channel" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
						</div>
					{/if}
				{/snippet}
			</DataTable>
		</div>
	{/if}
</RoleGate>

<Modal bind:open={modalOpen} title={editing ? 'Edit channel' : 'New channel'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Name" bind:value={fName} required />
			<Select label="Kind" bind:value={fKind} options={kindOptions} />
		</div>
		<Textarea label="Description" bind:value={fDescription} rows={2} />

		<Input
			label="Webhook URL"
			bind:value={fWebhookUrl}
			required={!editing}
			hint={editing
				? 'Leave blank to keep the stored URL — the API never returns it.'
				: 'The incoming-webhook URL. Treated as a credential.'}
		/>

		{#if fKind === 'webhook'}
			<CodeEditor
				label="Static headers"
				language="json"
				bind:value={fHeaders}
				rows={3}
				hint="Optional JSON object. Not a place for secrets — these are returned by the API."
			/>
		{/if}

		<div class="flex flex-wrap gap-4 pt-1">
			<Checkbox bind:checked={fEnabled} label="Enabled" />
			<Checkbox bind:checked={fAllowPrivate} label="Allow private network" />
		</div>
		{#if fAllowPrivate}
			<Alert tone="warning">
				This channel may post to a private (RFC-1918) address. Loopback and the cloud metadata
				address stay blocked regardless.
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<Modal bind:open={sendOpen} title={`Send to ${sendFor?.name ?? ''}`}>
	<div class="space-y-3">
		<Alert tone="warning">This posts a real message to the channel.</Alert>
		<Textarea label="Message" bind:value={sendText} rows={5} />
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (sendOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={sending} onclick={doSend}>Send</Button>
	{/snippet}
</Modal>

<Modal bind:open={historyOpen} title={`${historyFor?.name ?? ''} — deliveries`} size="lg">
	{#if historyLoading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if deliveries.length === 0}
		<p class="px-4 py-10 text-center text-sm text-surface-600-400">Nothing sent yet.</p>
	{:else}
		<div class="max-h-[26rem] space-y-1 overflow-y-auto">
			{#each deliveries as d (d.id)}
				<div class="rounded-lg border border-surface-100-900 px-3 py-2">
					<div class="flex items-center gap-2">
						<Badge tone={d.success ? 'success' : 'error'}>
							{d.success ? 'delivered' : 'failed'}
						</Badge>
						{#if d.statusCode}<span class="text-xs text-surface-600-400">HTTP {d.statusCode}</span>{/if}
						<span class="text-xs text-surface-600-400">{d.attempts} attempt(s) · {d.elapsedMs} ms</span>
						<span class="ml-auto text-xs text-surface-600-400">{fmt(d.sentAt)}</span>
					</div>
					<p class="mt-1 truncate text-xs text-surface-600-400">{d.preview}</p>
					{#if d.error}<p class="mt-0.5 text-xs text-error-600-400">{d.error}</p>{/if}
				</div>
			{/each}
		</div>
	{/if}
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (historyOpen = false)}>Close</Button>
	{/snippet}
</Modal>
