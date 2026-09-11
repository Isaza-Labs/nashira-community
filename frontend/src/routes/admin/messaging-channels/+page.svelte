<script lang="ts">
	import { page } from '$app/state';
	import {
		listChannels,
		createChannel,
		updateChannel,
		deleteChannel,
		getActivity,
		listLinks,
		revokeLink,
		MESSAGING_PROVIDERS,
		type MessagingChannel,
		type ChannelPayload,
		type ChannelActivity,
		type MessagingIdentityLink
	} from '$lib/api/messaging.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Select,
		Checkbox,
		Alert,
		Spinner,
		ErrorState,
		FieldHint,
		confirm,
		toast,
		type Column,
		type Tone
	} from '$lib/components/ui';
	import ParamsEditor, { pruneBlank } from '$lib/components/ParamsEditor.svelte';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';
	import { copyText } from '$lib/utils/clipboard';
	import { Plus, Pencil, Trash2, Eye, Copy, RefreshCw } from 'lucide-svelte';

	// The three secret fields mean something different on every platform — a Teams
	// "bot token" is an Entra client secret, and Teams has no inbound signing
	// secret at all. A generic form makes an admin read the source to find out;
	// this spells it out per provider and hides what does not apply.
	//
	// `configSchema` drives the External config editor: with properties it renders
	// labelled inputs (ParamsEditor → JsonSchemaForm) so nobody has to hand-write
	// JSON; empty, it falls back to the Add-field key/value editor for the
	// providers whose config is open-ended.
	type ProviderSpec = {
		botToken: { label: string; placeholder: string } | null;
		signingSecret: { label: string; placeholder: string } | null;
		appToken: { label: string; placeholder: string } | null;
		configSchema: Record<string, unknown>;
		setup: string;
	};

	const PROVIDER_SPECS: Record<string, ProviderSpec> = {
		telegram: {
			botToken: { label: 'Bot token', placeholder: '123456:ABC-DEF… from @BotFather' },
			signingSecret: {
				label: 'Secret token',
				placeholder: 'the secret_token you passed to setWebhook'
			},
			appToken: null,
			configSchema: {},
			setup: 'Register the webhook URL below with setWebhook, passing the same secret_token.'
		},
		slack: {
			botToken: { label: 'Bot token', placeholder: 'xoxb-…' },
			signingSecret: {
				label: 'Signing secret',
				placeholder: "from the app's Basic Information page"
			},
			appToken: {
				label: 'App-level token · Socket Mode (optional)',
				placeholder: 'xapp-… → outbound WebSocket, no public webhook needed'
			},
			configSchema: {},
			setup:
				'Either paste the webhook URL as the Events API Request URL, or set an app-level token to use Socket Mode instead.'
		},
		whatsapp: {
			botToken: { label: 'Access token', placeholder: 'Meta Cloud API permanent access token' },
			signingSecret: { label: 'App secret', placeholder: 'verifies X-Hub-Signature-256' },
			appToken: null,
			configSchema: {
				type: 'object',
				required: ['phone_number_id', 'verify_token'],
				'x-order': ['phone_number_id', 'verify_token', 'graph_version'],
				properties: {
					phone_number_id: {
						type: 'string',
						title: 'Phone number ID',
						description:
							'From the WhatsApp → API setup page of your Meta app. Identifies the number replies are sent from.',
						placeholder: '1234567890'
					},
					verify_token: {
						type: 'string',
						title: 'Verify token',
						description:
							'A string you invent. Meta echoes it back during the GET webhook handshake; paste the same value on both sides.'
					},
					graph_version: {
						type: 'string',
						title: 'Graph API version (optional)',
						description: 'Overrides the default Graph API version used for outbound messages.',
						placeholder: 'v21.0'
					}
				}
			},
			setup:
				"phone_number_id identifies the sending number; verify_token is echoed back during Meta's GET handshake."
		},
		teams: {
			botToken: {
				label: 'App password (Entra client secret)',
				placeholder: "the client secret of the bot's app registration"
			},
			// Teams authenticates inbound traffic with the Bot Framework JWT, so
			// there is no secret to store for it.
			signingSecret: null,
			// Teams has no Socket Mode; Azure Relay plays the same role, and the
			// credential lives in the same opt-in field.
			appToken: {
				label: 'Azure Relay connection string · no public ingress (optional)',
				placeholder:
					'Endpoint=sb://…servicebus.windows.net/;SharedAccessKeyName=…;SharedAccessKey=…;EntityPath=…'
			},
			configSchema: {
				type: 'object',
				required: ['app_id'],
				'x-order': ['app_id', 'tenant_id'],
				properties: {
					app_id: {
						type: 'string',
						title: 'Microsoft App ID',
						description:
							'Azure Bot → Configuration → Microsoft App ID. Both the audience the inbound token is validated against and the client_id replies are minted with.',
						placeholder: '00000000-0000-0000-0000-000000000000'
					},
					tenant_id: {
						type: 'string',
						title: 'App Tenant ID',
						description:
							'Azure Bot → Configuration → App Tenant ID. Required for single-tenant bots (the only type Azure still creates); leave empty for a legacy multi-tenant bot.',
						placeholder: '00000000-0000-0000-0000-000000000000'
					}
				}
			},
			setup:
				"app_id is the Azure Bot's Microsoft App ID (required — it is both the token audience and the reply client_id). " +
				'tenant_id is only needed for single-tenant / managed-identity bots. Paste the webhook URL below as the bot\'s messaging endpoint.'
		}
	};

	// ─── state ────────────────────────────────────────────────────────────
	let items = $state<MessagingChannel[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<MessagingChannel | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fProvider = $state('telegram');
	let fName = $state('');
	let fBotToken = $state('');
	let fSigningSecret = $state('');
	let fAppToken = $state('');
	let fConfig = $state<Record<string, unknown>>({});
	let fMaxRole = $state('');
	let fAllowedIds = $state('');
	let fRequireLinked = $state(true);
	let fAllowUnsigned = $state(false);
	let fEnabled = $state(true);

	const spec = $derived(PROVIDER_SPECS[fProvider] ?? PROVIDER_SPECS.telegram);

	let detailsOpen = $state(false);
	let detailsFor = $state<MessagingChannel | null>(null);
	let activity = $state<ChannelActivity | null>(null);
	let detailLinks = $state<MessagingIdentityLink[]>([]);
	let detailsLoading = $state(false);

	// Aggregated panel: every identity link across every channel in one place, so an
	// admin can unlink a Slack/WhatsApp/… account without drilling into each channel.
	type LinkRow = { id: string; channel: MessagingChannel; link: MessagingIdentityLink };
	let allLinks = $state<LinkRow[]>([]);
	let linksLoading = $state(false);

	const providerOptions = MESSAGING_PROVIDERS.map((p) => ({ value: p, label: p }));

	const roleOptions = [
		{ value: '', label: "No ceiling (user's role)" },
		{ value: 'viewer', label: 'viewer' },
		{ value: 'operator', label: 'operator' },
		{ value: 'admin', label: 'admin' }
	];

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'provider', header: 'Provider' },
		{ key: 'transport', header: 'Transport', sortable: false },
		{ key: 'status', header: 'Status', sortValue: (c: MessagingChannel) => c.enabled },
		{ key: 'lastDeliveryAt', header: 'Last delivery' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	const linkColumns: Column[] = [
		{ key: 'provider', header: 'Provider', sortable: false },
		{ key: 'channel', header: 'Channel', sortable: false },
		{ key: 'external', header: 'External user', sortable: false },
		{ key: 'user', header: 'Nashira user', sortable: false },
		{ key: 'created', header: 'Linked', sortable: false },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	function statusTone(c: MessagingChannel): Tone {
		if (!c.enabled) return 'warning';
		if (c.lastDeliveryStatus && c.lastDeliveryStatus !== 'sent') return 'error';
		return 'success';
	}

	function fmt(iso: string | null): string {
		return iso ? new Date(iso).toLocaleString() : '—';
	}

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listChannels()).items;
			void loadAllLinks();
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	// The backend exposes links per channel; fan out and flatten so the panel shows
	// them together. A failed channel fetch degrades to no rows for that channel
	// rather than failing the whole panel.
	async function loadAllLinks() {
		if (items.length === 0) {
			allLinks = [];
			return;
		}
		linksLoading = true;
		try {
			const results = await Promise.all(
				items.map((c) =>
					listLinks(c.id)
						.then((links) => links.map((link) => ({ id: link.id, channel: c, link })))
						.catch(() => [] as LinkRow[])
				)
			);
			allLinks = results.flat();
		} finally {
			linksLoading = false;
		}
	}

	$effect(() => {
		load();
	});

	// ─── create / edit ────────────────────────────────────────────────────
	function openCreate() {
		editing = null;
		formError = '';
		fProvider = 'telegram';
		fName = '';
		fBotToken = '';
		fSigningSecret = '';
		fAppToken = '';
		fConfig = {};
		fMaxRole = '';
		fAllowedIds = '';
		fRequireLinked = true;
		fAllowUnsigned = false;
		fEnabled = true;
		modalOpen = true;
	}

	function openEdit(c: MessagingChannel) {
		editing = c;
		formError = '';
		fProvider = c.provider;
		fName = c.name;
		// Deliberately blank: the API never returns a secret, so anything pre-filled
		// here would be resubmitted over the real one on an unrelated edit.
		fBotToken = '';
		fSigningSecret = '';
		fAppToken = '';
		fConfig = { ...c.externalConfig };
		fMaxRole = c.maxRole ?? '';
		fAllowedIds = c.allowedExternalIds.join(', ');
		fRequireLinked = c.requireLinkedUser;
		fAllowUnsigned = c.allowUnsigned;
		fEnabled = c.enabled;
		modalOpen = true;
	}

	function payload(): ChannelPayload {
		// Blank entries are dropped rather than stored as "": an optional key the
		// admin left alone should not persist, and a required one left alone should
		// trip the backend's "missing" error instead of its "empty" one. The wire
		// type is string→string, so whatever the JSON tab produced is stringified.
		const externalConfig: Record<string, string> = {};
		for (const [k, v] of Object.entries(pruneBlank(fConfig ?? {}))) {
			externalConfig[k] = typeof v === 'string' ? v : JSON.stringify(v);
		}

		const ids = fAllowedIds
			.split(',')
			.map((s) => s.trim())
			.filter(Boolean);

		return {
			provider: fProvider,
			name: fName.trim(),
			// A secret the admin did not type stays undefined, which drops the key and
			// leaves the stored value alone. A secret the selected provider has no
			// field for is dropped too, so switching provider mid-form cannot smuggle
			// in a value the admin can no longer see.
			botToken: spec.botToken && fBotToken ? fBotToken : undefined,
			signingSecret: spec.signingSecret && fSigningSecret ? fSigningSecret : undefined,
			appToken: spec.appToken && fAppToken ? fAppToken : undefined,
			externalConfig,
			maxRole: fMaxRole || null,
			requireLinkedUser: fRequireLinked,
			allowedExternalIds: ids,
			allowUnsigned: fAllowUnsigned,
			enabled: fEnabled
		};
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
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

	async function remove(c: MessagingChannel) {
		const ok = await confirm({
			title: `Delete ${c.name}?`,
			message: 'The channel is disabled and soft-deleted. Inbound webhooks stop working.',
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteChannel(c.id);
			items = items.filter((x) => x.id !== c.id);
			allLinks = allLinks.filter((e) => e.channel.id !== c.id);
			toast.success('Channel deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the channel");
		}
	}

	// ─── details (webhook URL + activity + links) ─────────────────────────
	async function openDetails(c: MessagingChannel) {
		detailsFor = c;
		activity = null;
		detailLinks = [];
		detailsOpen = true;
		detailsLoading = true;
		try {
			const [act, lnk] = await Promise.all([getActivity(c.id), listLinks(c.id)]);
			activity = act;
			detailLinks = lnk;
		} catch (e) {
			toast.fromError(e, "Couldn't load the channel activity");
		} finally {
			detailsLoading = false;
		}
	}

	async function revoke(channel: MessagingChannel, link: MessagingIdentityLink) {
		const ok = await confirm({
			title: 'Revoke link?',
			message: `External user ${link.externalUserId} loses agent access on "${channel.name}" until they link again.`,
			tone: 'danger',
			confirmLabel: 'Revoke'
		});
		if (!ok) return;
		try {
			await revokeLink(channel.id, link.id);
			detailLinks = detailLinks.filter((l) => l.id !== link.id);
			allLinks = allLinks.filter((e) => e.link.id !== link.id);
			toast.success('Link revoked');
		} catch (e) {
			toast.fromError(e, "Couldn't revoke the link");
		}
	}

	async function copyWebhook(url: string) {
		if (await copyText(url)) toast.success('Webhook URL copied');
		else toast.error('Copy failed — select the URL and copy it by hand.');
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Messaging channels · Nashira</title></svelte:head>

<SectionNav id="integrations" />

<PageHeader
	title="Messaging channels"
	description="Connect Slack, Teams, WhatsApp and Telegram to the agent — inbound and out."
>
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
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
		<div class="space-y-6">
			<Alert tone="neutral">
				A channel is a bot credential plus a permission ceiling for everyone who talks through it.
				Credentials are encrypted at rest and never returned — the form shows whether one is set,
				never what it is.
			</Alert>

			<DataTable
				{loading}
				{columns}
				rows={items}
				rowKey={(c) => c.id}
				empty="No channels yet — add one so people can reach the agent from Slack, Teams, WhatsApp or Telegram."
			>
				{#snippet cell(row, col)}
					{#if col.key === 'name'}
						<div class="font-medium">{row.name}</div>
						<div class="text-xs text-surface-600-400">{row.slug}</div>
					{:else if col.key === 'provider'}
						<Badge tone="primary">{row.provider}</Badge>
					{:else if col.key === 'transport'}
						{#if row.hasAppToken}
							<span class="text-xs text-surface-600-400">
								{row.provider === 'teams' ? 'Azure Relay' : 'Socket Mode'} · no ingress
							</span>
						{:else if row.webhookUrl}
							<span class="text-xs text-surface-600-400">webhook</span>
						{:else}
							<span class="text-xs text-warning-600-400">no public base URL set</span>
						{/if}
					{:else if col.key === 'status'}
						<div class="flex flex-wrap items-center gap-1.5">
							<Badge tone={statusTone(row)}>{row.enabled ? 'enabled' : 'disabled'}</Badge>
							{#if !row.hasBotToken}<Badge tone="warning">no bot token</Badge>{/if}
							{#if row.allowUnsigned}<Badge tone="error">unsigned allowed</Badge>{/if}
							{#if !row.requireLinkedUser}<Badge tone="warning">unlinked ok</Badge>{/if}
						</div>
					{:else if col.key === 'lastDeliveryAt'}
						<span class="text-xs text-surface-600-400">{fmt(row.lastDeliveryAt)}</span>
						{#if row.lastDeliveryStatus}
							<span class="text-xs text-surface-600-400"> ({row.lastDeliveryStatus})</span>
						{/if}
					{:else if col.key === 'actions'}
						<div class="flex justify-end gap-1">
							<IconButton label={`Details for ${row.name}`} onclick={() => openDetails(row)}>
								<Eye size={14} />
							</IconButton>
							<IconButton label={`Edit ${row.name}`} onclick={() => openEdit(row)}>
								<Pencil size={14} />
							</IconButton>
							<IconButton label={`Delete ${row.name}`} onclick={() => remove(row)}>
								<Trash2 size={14} />
							</IconButton>
						</div>
					{/if}
				{/snippet}
			</DataTable>

			<!-- Dedicated unlink panel: every identity link across every channel. -->
			{#if !loading && items.length > 0}
				<div class="space-y-2">
					<div class="flex flex-wrap items-start justify-between gap-3">
						<div>
							<h2 class="inline-flex items-center gap-1 text-sm font-semibold">
								Linked accounts
								<FieldHint
									label="Linked accounts"
									help="The external identities bound to Nashira accounts. Each binding is what lets a message carry that user's permissions."
									detail="Unlinking revokes access immediately for that identity without touching the channel or anyone else on it."
								/>
							</h2>
							<p class="mt-0.5 text-xs text-surface-600-400">
								External identities (Slack, WhatsApp, Telegram, Teams) bound to a Nashira user.
								Revoking unlinks the account — the user must link again to regain agent access.
							</p>
						</div>
						<Button variant="ghost" onclick={loadAllLinks}><RefreshCw size={15} />Refresh</Button>
					</div>

					<DataTable
						loading={linksLoading}
						columns={linkColumns}
						rows={allLinks}
						rowKey={(r) => r.id}
						density="compact"
						empty="No linked accounts yet."
					>
						{#snippet cell(row, col)}
							{#if col.key === 'provider'}
								<Badge tone="primary">{row.channel.provider}</Badge>
							{:else if col.key === 'channel'}
								{row.channel.name}
							{:else if col.key === 'external'}
								<code class="text-xs">{row.link.externalUserId}</code>
								{#if row.link.displayName}
									<span class="text-xs text-surface-600-400"> ({row.link.displayName})</span>
								{/if}
							{:else if col.key === 'user'}
								<span class="text-sm">{row.link.linkedUsername ?? '—'}</span>
							{:else if col.key === 'created'}
								<span class="text-xs text-surface-600-400">{fmt(row.link.createdAt)}</span>
							{:else if col.key === 'actions'}
								<div class="flex justify-end">
									<IconButton
										label={`Revoke link for ${row.link.externalUserId}`}
										onclick={() => revoke(row.channel, row.link)}
									>
										<Trash2 size={14} />
									</IconButton>
								</div>
							{/if}
						{/snippet}
					</DataTable>
				</div>
			{/if}
		</div>
	{/if}
</RoleGate>

<!-- Create / edit -->
<Modal bind:open={modalOpen} title={editing ? `Edit ${editing.name}` : 'New channel'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<div class="grid gap-3 sm:grid-cols-2">
			<div class="space-y-1">
				<label for="mc-provider" class="inline-flex items-center gap-1 text-sm font-medium">
					Provider
					<FieldHint
						label="Provider"
						help="Which messaging platform this channel connects: Slack, Teams, WhatsApp or Telegram. Fixed once created, since the credentials are provider-specific."
					/>
				</label>
				<Select
					id="mc-provider"
					bind:value={fProvider}
					options={providerOptions}
					disabled={saving || !!editing}
				/>
			</div>
			<div class="space-y-1">
				<label for="mc-name" class="inline-flex items-center gap-1 text-sm font-medium">
					Name
					<FieldHint
						label="Name"
						help='How this channel is identified in the list. Name it after the workspace or the audience — "ops-telegram", "noc-slack".'
					/>
				</label>
				<Input id="mc-name" bind:value={fName} placeholder="ops-telegram" disabled={saving} />
			</div>
		</div>

		<Alert tone="primary">{spec.setup}</Alert>

		{#if spec.botToken}
			<div class="space-y-1">
				<label for="mc-bot-token" class="inline-flex items-center gap-1 text-sm font-medium">
					{editing ? `${spec.botToken.label} (leave blank to keep)` : spec.botToken.label}
					<FieldHint
						label="Bot token"
						help="The outbound credential used to post messages back to the platform. Encrypted at rest and never returned to the UI."
						detail="Slack: the xoxb- bot token. Telegram: the @BotFather token. WhatsApp: the Cloud API access token. Teams: the Entra client secret of the bot's app registration. On an edit, leaving it blank keeps the token already stored."
					/>
				</label>
				<Input
					id="mc-bot-token"
					bind:value={fBotToken}
					placeholder={spec.botToken.placeholder}
					disabled={saving}
					hint={editing && editing.hasBotToken ? 'A token is stored.' : ''}
				/>
			</div>
		{/if}

		{#if spec.signingSecret}
			<div class="space-y-1">
				<label for="mc-signing-secret" class="inline-flex items-center gap-1 text-sm font-medium">
					{editing ? `${spec.signingSecret.label} (leave blank to keep)` : spec.signingSecret.label}
					<FieldHint
						label="Signing secret"
						help="Verifies that inbound webhooks really came from the platform. Without it, anyone who learns the webhook URL can impersonate the provider."
						detail="Teams does not use it: Bot Framework signs every activity with a JWT that is validated against Microsoft's public keys, so the field is hidden for that provider."
					/>
				</label>
				<Input
					id="mc-signing-secret"
					bind:value={fSigningSecret}
					placeholder={spec.signingSecret.placeholder}
					disabled={saving}
					hint={editing && editing.hasSigningSecret ? 'A secret is stored.' : ''}
				/>
			</div>
		{/if}

		{#if spec.appToken}
			<div class="space-y-1">
				<label for="mc-app-token" class="inline-flex items-center gap-1 text-sm font-medium">
					{editing ? `${spec.appToken.label} (leave blank to keep)` : spec.appToken.label}
					<FieldHint
						label="No-ingress credential"
						help="Optional. Lets the backend dial out and receive messages over that connection, so no publicly reachable webhook URL is needed — the answer for a VPN-only or NAT-ed deployment."
						detail="Slack: the App-Level Token (xapp-…, scope connections:write) that enables Socket Mode. Teams: an Azure Relay Hybrid Connection string — Teams has no Socket Mode, so the Relay stands in for one and the Azure Bot's messaging endpoint becomes the Relay URL. Telegram and WhatsApp are webhook-only."
					/>
				</label>
				<Input
					id="mc-app-token"
					bind:value={fAppToken}
					placeholder={spec.appToken.placeholder}
					disabled={saving}
					hint={editing && editing.hasAppToken ? 'A credential is stored.' : ''}
				/>
			</div>
		{/if}

		<div class="space-y-1">
			<span class="inline-flex items-center gap-1 text-sm font-medium">
				External config
				<FieldHint
					label="External config"
					help="Provider-specific settings that do not fit the fields above."
					detail="Teams requires app_id (the Azure Bot's Microsoft App ID) and takes an optional tenant_id for single-tenant bots. WhatsApp takes phone_number_id and verify_token. Slack and Telegram need nothing here."
				/>
			</span>
			<ParamsEditor schema={spec.configSchema} bind:value={fConfig} readonly={saving} rows={8} />
		</div>

		<div class="grid gap-3 sm:grid-cols-2">
			<div class="space-y-1">
				<label for="mc-max-role" class="inline-flex items-center gap-1 text-sm font-medium">
					Max role (ceiling)
					<FieldHint
						label="Max role (ceiling)"
						help="The highest privilege anything arriving through this channel may exercise, whatever the linked user normally holds."
						detail="It is a ceiling, never a grant: a viewer talking to the bot stays a viewer. Setting it to operator does not promote anybody — it only caps admins down."
					/>
				</label>
				<Select id="mc-max-role" bind:value={fMaxRole} options={roleOptions} disabled={saving} />
			</div>
			<div class="space-y-1">
				<label for="mc-allowed-ids" class="inline-flex items-center gap-1 text-sm font-medium">
					Allowed external ids
					<FieldHint
						label="Allowed external ids"
						help="Comma-separated list of platform user ids permitted to talk to the bot. Anyone not listed gets no access at all."
					/>
				</label>
				<Input
					id="mc-allowed-ids"
					bind:value={fAllowedIds}
					placeholder="U123, U456"
					disabled={saving}
					hint="Leave empty to allow every identity the channel reaches."
				/>
			</div>
		</div>

		<div class="flex flex-wrap items-center gap-x-4 gap-y-2 pt-1">
			<Checkbox bind:checked={fEnabled} label="Enabled" disabled={saving} />
			<span class="inline-flex items-center gap-1">
				<Checkbox bind:checked={fRequireLinked} label="Require linked account" disabled={saving} />
				<FieldHint
					label="Require linked account"
					help="Nobody gets agent access through this channel until their platform identity is bound to a Nashira account."
					detail="This is what makes the permission model work over a chat channel: with it off, there is no user to check permissions against. Leave it on."
				/>
			</span>
			<span class="inline-flex items-center gap-1">
				<Checkbox bind:checked={fAllowUnsigned} label="Allow unsigned deliveries" disabled={saving} />
				<FieldHint
					label="Allow unsigned deliveries"
					help="Accepts inbound webhooks that carry no signature."
					detail="The signature is what proves the delivery came from the platform. Without it, anyone who learns the URL can post as the provider — testing only."
				/>
			</span>
		</div>

		{#if fAllowUnsigned}
			<Alert tone="warning">
				Unsigned deliveries are accepted on this channel. Anyone who learns the webhook URL can post
				as the provider — turn this off outside of testing.
			</Alert>
		{/if}
		{#if !fRequireLinked}
			<Alert tone="warning">
				Without a linked account there is no user to check permissions against, so the channel's max
				role is all that limits what the agent will do.
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)} disabled={saving}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>
			{editing ? 'Save changes' : 'Create'}
		</Button>
	{/snippet}
</Modal>

<!-- Details -->
<Modal
	bind:open={detailsOpen}
	title={detailsFor ? `${detailsFor.name} — activity` : 'Activity'}
	size="lg"
>
	{#if detailsFor}
		<div class="space-y-5">
			{#if detailsFor.hasAppToken}
				<!-- The channel dials out, so the webhook URL is not what the provider
				     should be pointed at — saying otherwise sends an admin off
				     configuring an endpoint that will never be called. -->
				<Alert tone="primary">
					{#if detailsFor.provider === 'teams'}
						This channel receives over <strong>Azure Relay</strong>. Point the Azure Bot's
						<em>Messaging endpoint</em>
						at the Hybrid Connection's public URL
						(<code>https://&lt;namespace&gt;.servicebus.windows.net/&lt;connection-name&gt;</code>), not
						at the URL below. That public URL belongs in Azure only — the channel's Azure Relay
						field wants the Hybrid Connection's <em>Primary Connection String</em>, which is a
						different value.
					{:else if detailsFor.provider === 'slack'}
						This channel receives over <strong>Socket Mode</strong>. No Request URL is needed in the
						Slack app.
					{:else}
						This channel is configured to receive over an outbound connection rather than the
						webhook below.
					{/if}
				</Alert>
			{/if}

			<div>
				<div class="mb-1 text-xs uppercase tracking-wide text-surface-600-400">Webhook URL</div>
				{#if detailsFor.webhookUrl}
					<div class="flex items-center gap-2">
						<code class="ui-control flex-1 break-all px-2 py-1 text-xs">{detailsFor.webhookUrl}</code>
						<IconButton
							label="Copy webhook URL"
							onclick={() => copyWebhook(detailsFor?.webhookUrl ?? '')}
						>
							<Copy size={14} />
						</IconButton>
					</div>
					<p class="mt-1 text-xs text-surface-600-400">
						{detailsFor.hasAppToken
							? 'Kept for reference — this channel does not need it.'
							: "Register this URL in the provider's webhook settings."}
					</p>
				{:else}
					<p class="text-xs text-warning-600-400">
						No URL yet — the server has no public base URL configured (Messaging:PublicBaseUrl), and
						half a URL is worse than none.
					</p>
				{/if}
			</div>

			{#if detailsLoading}
				<div class="flex justify-center py-8"><Spinner /></div>
			{:else}
				<div>
					<div class="mb-2 text-sm font-medium">Linked accounts ({detailLinks.length})</div>
					{#if detailLinks.length === 0}
						<p class="text-sm text-surface-600-400">No linked users yet.</p>
					{:else}
						<ul class="space-y-1">
							{#each detailLinks as link (link.id)}
								<li
									class="flex items-center justify-between gap-2 rounded-lg border border-surface-100-900 px-3 py-1.5"
								>
									<span class="min-w-0 truncate text-sm">
										<code class="text-xs">{link.externalUserId}</code>
										{#if link.displayName}
											<span class="text-surface-600-400"> ({link.displayName})</span>
										{/if}
										{#if link.linkedUsername}
											<span class="text-surface-600-400"> → {link.linkedUsername}</span>
										{/if}
									</span>
									<Button variant="ghost" onclick={() => revoke(detailsFor!, link)}>Revoke</Button>
								</li>
							{/each}
						</ul>
					{/if}
				</div>

				<div class="grid gap-4 sm:grid-cols-2">
					<div>
						<div class="mb-2 text-sm font-medium">Inbound ({activity?.inbound.length ?? 0})</div>
						{#if (activity?.inbound.length ?? 0) === 0}
							<p class="text-xs text-surface-600-400">Nothing received yet.</p>
						{:else}
							<ul class="max-h-56 space-y-1 overflow-y-auto text-xs">
								{#each activity?.inbound ?? [] as e (e.id)}
									<li class="rounded-lg border border-surface-100-900 px-2 py-1.5">
										<span class="font-medium">{e.status}</span>
										{#if e.event}<span class="text-surface-600-400"> · {e.event}</span>{/if}
										<span class="text-surface-600-400"> · {fmt(e.at)}</span>
										{#if e.error}<div class="text-error-600-400">{e.error}</div>{/if}
									</li>
								{/each}
							</ul>
						{/if}
					</div>
					<div>
						<div class="mb-2 text-sm font-medium">
							Deliveries ({activity?.deliveries.length ?? 0})
						</div>
						{#if (activity?.deliveries.length ?? 0) === 0}
							<p class="text-xs text-surface-600-400">Nothing sent yet.</p>
						{:else}
							<ul class="max-h-56 space-y-1 overflow-y-auto text-xs">
								{#each activity?.deliveries ?? [] as d (d.id)}
									<li class="rounded-lg border border-surface-100-900 px-2 py-1.5">
										<span class="font-medium">{d.status}</span>
										<span class="text-surface-600-400">
											· attempt {d.attempt} · {fmt(d.at)}
										</span>
										{#if d.error}<div class="text-error-600-400">{d.error}</div>{/if}
									</li>
								{/each}
							</ul>
						{/if}
					</div>
				</div>
			{/if}
		</div>
	{/if}

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (detailsOpen = false)}>Close</Button>
	{/snippet}
</Modal>
