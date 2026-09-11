<script lang="ts">
	import { page } from '$app/state';
	import { ApiError, errorMessage } from '$lib/api/client';
	import {
		listIntegrations,
		createIntegration,
		updateIntegration,
		deleteIntegration,
		checkIntegration,
		syncActions,
		listActions,
		getIntegrationBundle,
		attachIntegrationSpec,
		attachIntegrationSkill,
		createIntegrationBundle,
		type IntegrationBundle,
		type StagedSkill,
		type StagedSpec,
		type Integration,
		type IntegrationPayload,
		type IntegrationAction,
		type IntegrationStatus
	} from '$lib/api/integrations.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Select,
		Textarea,
		CodeEditor,
		KeyValueRows,
		Checkbox,
		Alert,
		Spinner,
		Tabs,
		ErrorState,
		confirm,
		toast,
		type Column,
		type Tone
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import AuthFields from '$lib/components/auth/AuthFields.svelte';
	import CredentialPicker from '$lib/components/credential/CredentialPicker.svelte';
	import {
		emptyDraft,
		methodForCredential,
		missingMaterial,
		HTTP_CREDENTIAL_METHODS,
		type AuthDraft
	} from '$lib/auth/draft';
	import type { Credential } from '$lib/api/credentials.api';
	import { Plus, Pencil, Trash2, Activity, RefreshCw, List, Library, Upload } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	let items = $state<Integration[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	// Per-row busy state, so one probe's spinner doesn't disable every other row.
	let checkingId = $state<string | null>(null);
	let syncingId = $state<string | null>(null);

	let modalOpen = $state(false);
	let editing = $state<Integration | null>(null);
	let saving = $state(false);
	let formError = $state('');

	// Skills and specs staged for a NEW integration. They exist only in the browser
	// until Create is pressed, and then go up with the integration in a single
	// transaction — an integration is a base URL until a spec gives it operations, so
	// creating them separately means a failure halfway leaves something half-wired.
	//
	// Editing does not use these: an integration that already exists has the attach
	// panel, where an upload takes effect immediately instead of waiting for a Save.
	type ModalTab = 'integration' | 'specs' | 'skills';
	// priority is text here because the shared Input is a string field; it is coerced
	// once on submit rather than fought with on every keystroke.
	type StagedSkillRow = { name: string; content: string; priority: string };
	let modalTab = $state<ModalTab>('integration');
	let stagedSkills = $state<StagedSkillRow[]>([]);
	let stagedSpecs = $state<StagedSpec[]>([]);
	let newSkillInput = $state<HTMLInputElement | null>(null);
	let newSpecInput = $state<HTMLInputElement | null>(null);

	let fName = $state('');
	let fType = $state('');
	let fDescription = $state('');
	let fBaseUrl = $state('');

	// Auth is edited as a source ("where do the credentials come from") plus either a
	// stored credential or typed fields. The JSON editor stays as an escape hatch for
	// shapes the typed form does not cover.
	let fAuthSource = $state<'none' | 'stored' | 'custom' | 'json'>('none');
	// `token` rather than `none`, matching Flow Weaver's form. Almost every
	// integration authenticates, so `none` made the common case the one that needed
	// a deliberate choice — and the choice on offer put the OAuth-style scheme first,
	// which is the wrong one for the Django REST Framework APIs this tool talks to
	// most. Editing an existing integration still shows whatever it actually has.
	let fAuthMethod = $state('token');
	let fDraft = $state<AuthDraft>(emptyDraft());
	let fCredentialId = $state('');
	let fCredential = $state<Credential | null>(null);
	let fAuthConfig = $state('');

	let fHeaders = $state('');
	let fVerifySsl = $state(true);
	let fAllowPrivate = $state(false);
	let fHealthPath = $state('');
	let fEnabled = $state(true);

	// Action-catalog drawer.
	let actionsOpen = $state(false);
	let actionsFor = $state<Integration | null>(null);
	let actions = $state<IntegrationAction[]>([]);
	let actionsLoading = $state(false);

	// Skills + specs drawer. Both were always linkable to an integration, just only
	// from the other end — this is the same link, edited from the side that owns it.
	let bundleOpen = $state(false);
	let bundleFor = $state<Integration | null>(null);
	let bundle = $state<IntegrationBundle>({ skills: [], specs: [] });
	let bundleLoading = $state(false);
	let bundleTab = $state<'specs' | 'skills'>('specs');
	let attaching = $state(false);
	let attachError = $state('');
	// Feedback belongs NEXT TO the button that produced it. The error used to render
	// at the top of a drawer containing a list, an alert and a ten-row editor, so
	// pressing Attach and failing looked exactly like pressing Attach and nothing
	// happening — which is how a rejected upload gets retried until it reports the
	// name already exists.
	let attachOk = $state('');
	// Set when the collision is with a GLOBAL skill or spec, which is the one case a
	// button can resolve. A name owned by another integration is not offered, because
	// taking it would strip it from there without anyone being asked.
	let adoptable = $state<'skill' | 'spec' | null>(null);

	let specApi = $state('');
	let specContent = $state('');
	let skillName = $state('');
	let skillContent = $state('');
	let specInput = $state<HTMLInputElement | null>(null);
	let skillInput = $state<HTMLInputElement | null>(null);

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'type', header: 'Type' },
		{ key: 'baseUrl', header: 'Base URL' },
		{ key: 'auth', header: 'Auth' },
		{ key: 'catalog', header: 'Catalog', sortValue: (i: Integration) => i.actionCount },
		{ key: 'status', header: 'Status' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	const authSourceOptions = [
		{ value: 'none', label: 'No authentication' },
		{ value: 'stored', label: 'Use a stored credential' },
		{ value: 'custom', label: 'Enter credentials here' },
		{ value: 'json', label: 'Advanced: edit the JSON' }
	];

	// Picking a credential preselects the scheme it implies, which is right almost
	// always and still overridable — NetBox needs the "Token" scheme on what is
	// otherwise an ordinary token credential.
	$effect(() => {
		if (fAuthSource === 'stored' && fCredential) {
			fAuthMethod = methodForCredential('integration', fCredential.authMethod);
		}
	});

	function statusTone(s: IntegrationStatus): Tone {
		if (s === 'healthy') return 'success';
		if (s === 'degraded') return 'warning';
		if (s === 'unreachable') return 'error';
		return 'neutral';
	}

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listIntegrations()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function resetStaging() {
		modalTab = 'integration';
		stagedSkills = [];
		stagedSpecs = [];
	}

	// Multiple files at once: a spec catalogue is normally a directory, and picking
	// them one at a time is the thing that made people give up and use three screens.
	async function onNewSpecFiles(e: Event) {
		const input = e.target as HTMLInputElement;
		for (const file of Array.from(input.files ?? [])) {
			stagedSpecs = [
				...stagedSpecs,
				{
					api: file.name
						.replace(/\.(ya?ml|json)$/i, '')
						.toLowerCase()
						.replace(/[^a-z0-9_-]/g, '-'),
					content: await file.text()
				}
			];
		}
		// Cleared so picking the same file again still fires change.
		input.value = '';
	}

	async function onNewSkillFiles(e: Event) {
		const input = e.target as HTMLInputElement;
		for (const file of Array.from(input.files ?? [])) {
			stagedSkills = [
				...stagedSkills,
				{ name: file.name.replace(/\.(md|markdown|txt)$/i, ''), content: await file.text(), priority: '100' }
			];
		}
		input.value = '';
	}

	function removeStagedSpec(i: number) {
		stagedSpecs = stagedSpecs.filter((_, n) => n !== i);
	}

	function removeStagedSkill(i: number) {
		stagedSkills = stagedSkills.filter((_, n) => n !== i);
	}

	// Returns the tab to jump to, so a validation failure on a tab the user cannot
	// currently see does not read as nothing happening.
	function validateStaged(): ModalTab | null {
		for (const sp of stagedSpecs) {
			const api = sp.api.trim().toLowerCase();
			if (!api) {
				formError = 'Every spec needs an api name.';
				return 'specs';
			}
			if (!/^[a-z0-9_-]+$/.test(api)) {
				formError = `"${sp.api}" is not a valid api name — lowercase letters, digits, underscore, hyphen.`;
				return 'specs';
			}
			if (!sp.content.trim()) {
				formError = `Spec "${api}" is empty.`;
				return 'specs';
			}
		}
		const apis = stagedSpecs.map((sp) => sp.api.trim().toLowerCase());
		const dupApi = apis.find((a, i) => apis.indexOf(a) !== i);
		if (dupApi) {
			formError = `Two specs use the api name "${dupApi}".`;
			return 'specs';
		}

		for (const sk of stagedSkills) {
			if (!sk.name.trim()) {
				formError = 'Every skill needs a name.';
				return 'skills';
			}
			if (!sk.content.trim()) {
				formError = `Skill "${sk.name}" is empty.`;
				return 'skills';
			}
		}
		const names = stagedSkills.map((sk) => sk.name.trim());
		const dupName = names.find((n, i) => names.indexOf(n) !== i);
		if (dupName) {
			formError = `Two skills use the name "${dupName}".`;
			return 'skills';
		}
		return null;
	}

	function openCreate() {
		editing = null;
		formError = '';
		fName = '';
		fType = '';
		fDescription = '';
		fBaseUrl = '';
		fAuthSource = 'none';
		fAuthMethod = 'token';
		fDraft = emptyDraft();
		fCredentialId = '';
		fCredential = null;
		fAuthConfig = '';
		fHeaders = '';
		fVerifySsl = true;
		fAllowPrivate = false;
		fHealthPath = '';
		fEnabled = true;
		resetStaging();
		modalOpen = true;
	}

	function openEdit(i: Integration) {
		editing = i;
		formError = '';
		// Editing never stages: an existing integration attaches through the panel,
		// where an upload lands immediately rather than waiting for a Save.
		resetStaging();
		fName = i.name;
		fType = i.type;
		fDescription = i.description ?? '';
		fBaseUrl = i.baseUrl;

		// A linked credential is visible on read, so editing it is not guesswork. Typed
		// credentials are not: the API never returns them, and pre-filling a placeholder
		// would let an unrelated edit overwrite the real value with the placeholder.
		fCredentialId = i.authCredentialId ?? '';
		fCredential = null;
		fAuthMethod = i.authMethod || 'none';

		// The non-secret knobs ARE returned, so the form shows what is configured
		// instead of making the user remember that NetBox needs the "Token" prefix —
		// and re-submits them unchanged when the edit was about something else.
		fDraft = emptyDraft();
		if (i.authShape) {
			fDraft.prefix = i.authShape.prefix ?? '';
			fDraft.header = i.authShape.header ?? '';
			fDraft.username = i.authShape.username ?? '';
			fDraft.tokenUrl = i.authShape.tokenUrl ?? '';
			fDraft.clientId = i.authShape.clientId ?? '';
			fDraft.scope = i.authShape.scope ?? '';
		}

		fAuthConfig = '';
		fAuthSource = i.authCredentialId
			? 'stored'
			: i.authMethod && i.authMethod !== 'none'
				? 'custom'
				: 'none';

		fHeaders = i.headers ?? '';
		fVerifySsl = i.verifySsl;
		fAllowPrivate = i.allowPrivateNetwork;
		fHealthPath = i.healthCheckPath ?? '';
		fEnabled = i.enabled;
		modalOpen = true;
	}

	function payload(): IntegrationPayload {
		const stored = fAuthSource === 'stored';
		return {
			name: fName.trim(),
			type: fType.trim(),
			description: fDescription,
			baseUrl: fBaseUrl.trim(),
			authMethod: fAuthSource === 'none' ? 'none' : fAuthMethod,
			authDraft: fDraft,
			authCredentialId: stored ? fCredentialId : '',
			authConfigOverride: fAuthSource === 'json' ? fAuthConfig : null,
			headers: fHeaders,
			verifySsl: fVerifySsl,
			allowPrivateNetwork: fAllowPrivate,
			healthCheckPath: fHealthPath,
			enabled: fEnabled
		};
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		if (!fBaseUrl.trim()) {
			formError = 'Base URL is required.';
			return;
		}
		if (fAuthSource === 'stored' && !fCredentialId) {
			formError = 'Pick a credential, or choose another source.';
			return;
		}
		// Named here rather than arriving as a 400 — or worse, as an anonymous request
		// the upstream answers with a 401 that looks like a bad password.
		const missing =
			fAuthSource === 'custom'
				? missingMaterial('integration', fAuthMethod, fDraft, false, !!editing)
				: null;
		if (missing) {
			formError = missing;
			return;
		}
		if (!editing) {
			const badTab = validateStaged();
			if (badTab) {
				modalTab = badTab;
				return;
			}
		}
		formError = '';
		saving = true;
		try {
			if (editing) {
				await updateIntegration(editing.id, payload());
				toast.success('Integration updated');
			} else if (stagedSkills.length > 0 || stagedSpecs.length > 0) {
				// One transaction: if a spec fails to parse, no integration is left
				// behind for someone to find and wonder about.
				const skills: StagedSkill[] = stagedSkills.map((sk) => ({
					name: sk.name,
					content: sk.content,
					// A blank or non-numeric box means "no opinion", which is the same
					// thing the server would default to.
					priority: Number.parseInt(sk.priority, 10) || 100
				}));
				const r = await createIntegrationBundle(payload(), skills, stagedSpecs);
				toast.success(`Integration "${r.integration.name}" created`, {
					description: `${r.specsCreated} spec(s), ${r.skillsCreated} skill(s), ${r.actionsCreated} action(s) catalogued`
				});
			} else {
				await createIntegration(payload());
				toast.success('Integration created');
			}
			modalOpen = false;
			await load();
		} catch (e) {
			// Onto the form rather than only a toast: with three tabs the toast can be
			// the only clue, and it says nothing about which tab to go back to.
			formError = errorMessage(e);
			toast.fromError(e, 'Failed to save the integration');
		} finally {
			saving = false;
		}
	}

	async function remove(i: Integration) {
		const ok = await confirm({
			title: 'Delete integration?',
			message: `"${i.name}" and its ${i.actionCount} catalogued action(s) will be removed. Specs linked to it keep working only if they carry their own base URL and credentials.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteIntegration(i.id);
			items = items.filter((x) => x.id !== i.id);
			toast.success('Integration deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the integration");
		}
	}

	async function check(i: Integration) {
		checkingId = i.id;
		try {
			const h = await checkIntegration(i.id);
			if (h.status === 'healthy') toast.success(`${i.name}: healthy (${h.elapsedMs} ms)`);
			else toast.error(`${i.name}: ${h.error ?? h.status}`);
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't reach the integration");
		} finally {
			checkingId = null;
		}
	}

	async function sync(i: Integration) {
		syncingId = i.id;
		try {
			const r = await syncActions(i.id);
			if (r.specCount === 0) {
				toast.error(
					`${i.name} has no API specs linked to it — link one in Admin → API Specs first.`
				);
			} else {
				toast.success(
					`${i.name}: +${r.created} new, ${r.updated} updated, ${r.disappeared} retired from ${r.specCount} spec(s)`
				);
			}
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't sync the action catalog");
		} finally {
			syncingId = null;
		}
	}

	async function openActions(i: Integration) {
		actionsFor = i;
		actions = [];
		actionsOpen = true;
		actionsLoading = true;
		try {
			actions = (await listActions(i.id)).items;
		} catch (e) {
			toast.fromError(e, "Couldn't load the action catalog");
		} finally {
			actionsLoading = false;
		}
	}

	// Editing hid the specs and skills tabs and said nothing about where they went, so
	// the panel that owns them — reachable only from a book icon among four icon
	// buttons in the row — was undiscoverable to anyone who did the obvious thing and
	// opened Edit. The tabs stay hidden (attaching to a saved integration takes effect
	// immediately, which is a different interaction from staging), but the modal now
	// says so and hands over.
	async function editBundle() {
		const target = editing;
		if (!target) return;
		modalOpen = false;
		await openBundle(target);
	}

	async function openBundle(i: Integration) {
		bundleFor = i;
		bundle = { skills: [], specs: [] };
		bundleTab = 'specs';
		attachError = '';
		attachOk = '';
		adoptable = null;
		specApi = '';
		specContent = '';
		skillName = '';
		skillContent = '';
		bundleOpen = true;
		bundleLoading = true;
		try {
			bundle = await getIntegrationBundle(i.id);
		} catch (e) {
			toast.fromError(e, "Couldn't load this integration's skills and specs");
		} finally {
			bundleLoading = false;
		}
	}

	// The filename is the natural identity of both: a spec uploaded as netbox.yaml is
	// the `netbox` api the agent addresses, and a skill file keeps its own name. Only
	// prefilled when the field is still empty, so a deliberate name is never clobbered.
	async function onSpecPick(e: Event) {
		const file = (e.target as HTMLInputElement).files?.[0];
		if (!file) return;
		specContent = await file.text();
		if (!specApi.trim()) {
			specApi = file.name
				.replace(/\.(ya?ml|json)$/i, '')
				.toLowerCase()
				.replace(/[^a-z0-9_-]/g, '-');
		}
		(e.target as HTMLInputElement).value = '';
	}

	async function onSkillPick(e: Event) {
		const file = (e.target as HTMLInputElement).files?.[0];
		if (!file) return;
		skillContent = await file.text();
		if (!skillName.trim()) skillName = file.name.replace(/\.(md|markdown|txt)$/i, '');
		(e.target as HTMLInputElement).value = '';
	}

	async function attachSpec(adopt = false) {
		if (!bundleFor) return;
		const apiName = specApi.trim().toLowerCase();
		if (!apiName) {
			clearAttachFeedback();
			attachError = 'An api name is required — it is what the agent passes to discover_operations.';
			return;
		}
		if (!/^[a-z0-9_-]+$/.test(apiName)) {
			clearAttachFeedback();
			attachError = 'The api name takes lowercase letters, digits, underscore and hyphen only.';
			return;
		}
		if (!specContent.trim()) {
			clearAttachFeedback();
			attachError = 'Upload or paste the OpenAPI document.';
			return;
		}
		attachError = '';
		attachOk = '';
		adoptable = null;
		attaching = true;
		try {
			const r = await attachIntegrationSpec(bundleFor.id, apiName, specContent, adopt);
			// The action counts are the point: the spec saving is not what the admin
			// came for, the catalog changing is.
			attachOk = `${r.spec.api} attached — ${r.spec.operationCount} operation(s), `
				+ `${r.created} action(s) added, ${r.updated} updated`;
			toast.success(`${r.spec.api} attached`, { description: attachOk });
			specApi = '';
			specContent = '';
			bundle = await getIntegrationBundle(bundleFor.id);
			// Row counts (specs, actions) are stale now.
			await load();
		} catch (e) {
			attachError = errorMessage(e);
			if (conflictCode(e) === 'spec_api_global') adoptable = 'spec';
		} finally {
			attaching = false;
		}
	}

	async function attachSkill(adopt = false) {
		if (!bundleFor) return;
		const name = skillName.trim();
		if (!name) {
			clearAttachFeedback();
			attachError = 'A skill name is required.';
			return;
		}
		if (!skillContent.trim()) {
			clearAttachFeedback();
			attachError = 'Upload or paste the skill markdown.';
			return;
		}
		attachError = '';
		attachOk = '';
		adoptable = null;
		attaching = true;
		try {
			await attachIntegrationSkill(bundleFor.id, name, skillContent, undefined, adopt);
			attachOk = `${name} attached — it joins the agent prompt on the next turn.`;
			toast.success(`${name} attached`, { description: 'It joins the agent prompt on the next turn.' });
			skillName = '';
			skillContent = '';
			bundle = await getIntegrationBundle(bundleFor.id);
		} catch (e) {
			attachError = errorMessage(e);
			if (conflictCode(e) === 'skill_name_global') adoptable = 'skill';
		} finally {
			attaching = false;
		}
	}

	// Feedback describes the button that produced it, so it cannot outlive the tab
	// that button is on — otherwise a failed spec attach leaves "a global API spec
	// named 'x' already exists" sitting under the skill editor, complete with an
	// adopt button that would attach a spec.
	function clearAttachFeedback() {
		attachError = '';
		attachOk = '';
		adoptable = null;
	}

	// The server names the collision it hit. Only the "belongs to nobody" ones are
	// resolvable from here, so the code is what decides whether to offer a button or
	// just report the problem.
	function conflictCode(e: unknown): string | null {
		const body = e instanceof ApiError ? (e.body as { code?: string } | undefined) : undefined;
		return body?.code ?? null;
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Integrations · Nashira</title></svelte:head>

<SectionNav id="integrations" />

<PageHeader
	title="Integrations"
	description="External systems Nashira calls. An API spec or prompt skill linked to an integration inherits its base URL and credentials."
>
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New integration</Button>
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
		<DataTable {loading} {columns} rows={items} rowKey={(i) => i.id} empty="No integrations yet — register an external system to give the agent and your workflows its operations.">
			{#snippet cell(row, col)}
				{#if col.key === 'name'}
					<div class="font-medium">{row.name}</div>
					<div class="text-xs text-surface-600-400">{row.slug}</div>
				{:else if col.key === 'type'}
					{#if row.type}<Badge>{row.type}</Badge>{:else}<span class="text-surface-600-400">—</span>{/if}
				{:else if col.key === 'baseUrl'}
					<code class="text-xs text-surface-600-400">{row.baseUrl}</code>
				{:else if col.key === 'auth'}
					<div class="flex items-center gap-1.5">
						<Badge tone={row.hasCredentials ? 'neutral' : 'warning'}>{row.authMethod}</Badge>
						{#if row.authCredentialName && row.hasInlineCredentials}
							<!-- Both are configured and the inline value wins, so the linked
							     credential is decoration. Without saying so, rotating that
							     credential looks like it should work and silently does not. -->
							<span
								class="text-xs text-warning-600-400"
								title="This integration stores its own secret in auth_config, which takes precedence over the linked credential '{row.authCredentialName}'. Clear it from auth_config to use the credential."
							>
								inline secret overrides {row.authCredentialName}
							</span>
						{:else if row.authCredentialName}
							<span class="text-xs text-surface-600-400" title="Stored credential">
								via {row.authCredentialName}
							</span>
						{:else if !row.hasCredentials && row.authMethod !== 'none'}
							<span class="text-xs text-warning-600-400">not set</span>
						{/if}
					</div>
				{:else if col.key === 'catalog'}
					<!-- The catalogue is projected from the linked specs, so the count is
					     also the way in: click through to exactly those specs, and to the
					     skills that teach the agent how to use them. -->
					<div class="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs text-surface-600-400">
						<span>{row.actionCount} action{row.actionCount === 1 ? '' : 's'}</span>
						<span aria-hidden="true">·</span>
						<a class="underline hover:text-primary-700-300" href={`/admin/specs?integration=${row.id}`}>
							{row.specCount} spec{row.specCount === 1 ? '' : 's'}
						</a>
						<span aria-hidden="true">·</span>
						<a class="underline hover:text-primary-700-300" href={`/admin/skills?integration=${row.id}`}>
							skills
						</a>
					</div>
				{:else if col.key === 'status'}
					<div class="flex items-center gap-1.5">
						<Badge tone={statusTone(row.status)}>{row.status}</Badge>
						{#if !row.enabled}<Badge tone="warning">disabled</Badge>{/if}
					</div>
					{#if row.lastCheckError}
						<div class="mt-0.5 max-w-[28rem] truncate text-xs text-error-600-400" title={row.lastCheckError}>
							{row.lastCheckError}
						</div>
					{/if}
				{:else if col.key === 'actions'}
					<div class="flex justify-end gap-1">
						<IconButton
							label={`Browse ${row.name} actions`}
							onclick={() => openActions(row)}
						>
							<List size={14} />
						</IconButton>
						<IconButton
							label={`${row.name} skills and API specs`}
							onclick={() => openBundle(row)}
						>
							<Library size={14} />
						</IconButton>
						<IconButton
							label={`Test ${row.name}`}
							disabled={checkingId === row.id}
							onclick={() => check(row)}
						>
							{#if checkingId === row.id}<Spinner size="sm" />{:else}<Activity size={14} />{/if}
						</IconButton>
						<IconButton
							label={`Sync ${row.name} actions from its specs`}
							disabled={syncingId === row.id}
							onclick={() => sync(row)}
						>
							{#if syncingId === row.id}<Spinner size="sm" />{:else}<RefreshCw size={14} />{/if}
						</IconButton>
						<IconButton label="Edit integration" onclick={() => openEdit(row)}>
							<Pencil size={14} />
						</IconButton>
						<IconButton label="Delete integration" onclick={() => remove(row)}>
							<Trash2 size={14} />
						</IconButton>
					</div>
				{/if}
			{/snippet}
		</DataTable>
	{/if}
</RoleGate>

<Modal bind:open={modalOpen} title={editing ? 'Edit integration' : 'New integration'} size="lg">
	<div class="space-y-3">
		{#if !editing}
			<Tabs
				bind:value={modalTab}
				tabs={[
					{ value: 'integration', label: 'Integration' },
					{
						value: 'specs',
						label: stagedSpecs.length ? `API specs (${stagedSpecs.length})` : 'API specs'
					},
					{
						value: 'skills',
						label: stagedSkills.length ? `Skills (${stagedSkills.length})` : 'Skills'
					}
				]}
			/>
		{:else}
			<div
				class="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-surface-200-800 bg-surface-100-900/50 px-3 py-2"
			>
				<p class="text-xs text-surface-600-400">
					Skills and API specs attach from their own panel — an upload there takes effect
					immediately, instead of waiting for a save here.
				</p>
				<Button variant="ghost" onclick={editBundle}>
					<Library size={14} />Skills &amp; API specs
				</Button>
			</div>
		{/if}

		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<!-- The integration tab is the whole form when editing: an existing
		     integration attaches skills and specs through its own panel, where an
		     upload takes effect immediately instead of waiting for a Save. -->
		{#if editing || modalTab === 'integration'}
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Name" bind:value={fName} required />
			<Input label="Type" bind:value={fType} hint="e.g. netbox, servicenow, awx" />
		</div>
		<Input label="Base URL" bind:value={fBaseUrl} required hint="Absolute http(s) URL" />
		<Textarea label="Description" bind:value={fDescription} rows={2} />

		<fieldset class="ui-surface space-y-3 rounded-lg p-3">
			<legend class="px-1 text-sm font-medium">Authentication</legend>

			<Select label="Credentials" bind:value={fAuthSource} options={authSourceOptions} />

			{#if fAuthSource === 'stored'}
				<CredentialPicker
					bind:value={fCredentialId}
					bind:selected={fCredential}
					label="Stored credential"
					emptyLabel="— pick a credential —"
					allowedMethods={HTTP_CREDENTIAL_METHODS}
					showMethod
					hint="Managed in Administration → Credentials. The secret never leaves the server."
				/>
				{#if fCredential}
					<AuthFields dialect="integration" bind:method={fAuthMethod} bind:draft={fDraft} shapeOnly />
				{/if}
			{:else if fAuthSource === 'custom'}
				<AuthFields
					dialect="integration"
					bind:method={fAuthMethod}
					bind:draft={fDraft}
					editing={!!editing}
				/>
			{:else if fAuthSource === 'json'}
				<CodeEditor
					label="Auth config"
					language="json"
					bind:value={fAuthConfig}
					rows={7}
					hint={editing
						? 'Leave blank to keep the stored credentials. Values may be ${secret:secret:<name>:value} references.'
						: 'JSON. Values may be ${secret:secret:<name>:value} references so the secret itself stays in the secret store.'}
				/>
				<p class="text-xs text-surface-600-400">
					Methods: <code>token</code>, <code>bearer</code>, <code>basic</code>, <code>api_key</code>,
					<code>oauth2_client_credentials</code>. Example:
					<code>{'{"method":"token","token":"${secret:secret:netbox-api-token:value}","prefix":"Token"}'}</code>
				</p>
			{/if}

			{#if editing && fAuthSource === 'custom'}
				<p class="text-xs text-surface-600-300">
					Stored credentials are never returned by the API. Leave the secret fields blank to keep
					what is saved.
				</p>
			{/if}
		</fieldset>

		<!-- Rows rather than a JSON editor: adding one header should not require typing
		     braces, and a stray comma should not be the difference between a saved
		     integration and a 400. What is stored is still the JSON object. -->
		<KeyValueRows
			label="Static headers"
			bind:value={fHeaders}
			keyPlaceholder="Header"
			valuePlaceholder="Value"
			addLabel="Add header"
			hint="Optional, non-secret. Authorization is ignored here — it belongs to auth config."
		/>

		<div class="grid gap-3 sm:grid-cols-2">
			<Input
				label="Health check path"
				bind:value={fHealthPath}
				hint="Optional, e.g. /api/status. Blank probes the base URL."
			/>
		</div>

		<div class="flex flex-wrap gap-4 pt-1">
			<Checkbox bind:checked={fEnabled} label="Enabled" />
			<Checkbox bind:checked={fVerifySsl} label="Verify TLS certificate" />
			<Checkbox bind:checked={fAllowPrivate} label="Allow private network" />
		</div>

		{#if !fVerifySsl}
			<Alert tone="warning">
				TLS verification is off. Traffic to this integration — credentials included — can be
				intercepted. Only for appliances behind a private CA.
			</Alert>
		{/if}
		{#if fAllowPrivate}
			<Alert tone="warning">
				This integration may reach private (RFC-1918) addresses. Loopback and the cloud metadata
				address stay blocked regardless.
			</Alert>
		{/if}
		{:else if modalTab === 'specs'}
			<Alert tone="neutral">
				OpenAPI documents scoped to this integration. Their operations inherit its base URL and
				credentials — which is why there is no auth to fill in here — and become the action
				catalog as soon as the integration is created. Nothing is uploaded until then: if any
				file is rejected, no integration is left behind.
			</Alert>

			<input
				bind:this={newSpecInput}
				type="file"
				accept=".yaml,.yml,.json,text/yaml,application/json"
				multiple
				class="hidden"
				onchange={onNewSpecFiles}
			/>
			<Button variant="secondary" onclick={() => newSpecInput?.click()}>
				<Upload size={15} />Choose .yaml files
			</Button>

			{#if stagedSpecs.length === 0}
				<div
					class="rounded-lg border border-dashed border-surface-200-800 px-4 py-8 text-center text-xs text-surface-600-400"
				>
					No spec staged. You can add one later from the integration's own panel — an
					integration with none is a base URL the agent cannot call yet.
				</div>
			{:else}
				<div class="space-y-2">
					{#each stagedSpecs as sp, i (i)}
						<div class="space-y-2 rounded-lg border border-surface-100-900 p-3">
							<div class="flex items-end gap-2">
								<div class="flex-1">
									<Input
										label="api name"
										bind:value={sp.api}
										hint="What the agent passes to discover_operations."
									/>
								</div>
								<IconButton label={`Remove ${sp.api}`} onclick={() => removeStagedSpec(i)}>
									<Trash2 size={14} />
								</IconButton>
							</div>
							<CodeEditor label="Content" language="yaml" bind:value={sp.content} rows={6} />
						</div>
					{/each}
				</div>
			{/if}
		{:else}
			<Alert tone="neutral">
				Prompt skills scoped to this integration — how this particular system behaves, in the
				agent's own words. They join its system prompt once the integration exists, after the
				global skills.
			</Alert>

			<input
				bind:this={newSkillInput}
				type="file"
				accept=".md,.markdown,text/markdown"
				multiple
				class="hidden"
				onchange={onNewSkillFiles}
			/>
			<Button variant="secondary" onclick={() => newSkillInput?.click()}>
				<Upload size={15} />Choose .md files
			</Button>

			{#if stagedSkills.length === 0}
				<div
					class="rounded-lg border border-dashed border-surface-200-800 px-4 py-8 text-center text-xs text-surface-600-400"
				>
					No skill staged. Optional — the agent can call the integration without one.
				</div>
			{:else}
				<div class="space-y-2">
					{#each stagedSkills as sk, i (i)}
						<div class="space-y-2 rounded-lg border border-surface-100-900 p-3">
							<div class="flex items-end gap-2">
								<div class="flex-1">
									<Input label="Name" bind:value={sk.name} hint="Unique across all skills." />
								</div>
								<div class="w-32">
									<Input
										label="Priority"
										type="number"
										bind:value={sk.priority}
										hint="Lower loads earlier."
									/>
								</div>
								<IconButton label={`Remove ${sk.name}`} onclick={() => removeStagedSkill(i)}>
									<Trash2 size={14} />
								</IconButton>
							</div>
							<CodeEditor label="Content" language="markdown" bind:value={sk.content} rows={6} />
						</div>
					{/each}
				</div>
			{/if}
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>
			{editing
				? 'Save'
				: stagedSpecs.length || stagedSkills.length
					? `Create with ${stagedSpecs.length + stagedSkills.length} file(s)`
					: 'Create'}
		</Button>
	{/snippet}
</Modal>

<Modal bind:open={actionsOpen} title={`${actionsFor?.name ?? ''} — action catalog`} size="lg">
	{#if actionsLoading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if actions.length === 0}
		<div class="px-4 py-10 text-center text-sm text-surface-600-400">
			No actions catalogued. Link an API spec to this integration, then run the sync.
		</div>
	{:else}
		<div class="max-h-[26rem] space-y-1 overflow-y-auto">
			{#each actions as a (a.id)}
				<div class="flex items-start gap-3 rounded-lg border border-surface-100-900 px-3 py-2">
					<Badge tone={a.readOnly ? 'neutral' : 'warning'}>{a.method}</Badge>
					<div class="min-w-0 flex-1">
						<div class="truncate text-sm font-medium">{a.name}</div>
						<code class="text-xs text-surface-600-400">{a.path}</code>
					</div>
					{#if !a.enabled}<Badge tone="warning">disabled</Badge>{/if}
					{#if !a.isActive}<Badge tone="error">retired</Badge>{/if}
				</div>
			{/each}
		</div>
	{/if}
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (actionsOpen = false)}>Close</Button>
	{/snippet}
</Modal>

<Modal
	bind:open={bundleOpen}
	title={`${bundleFor?.name ?? ''} — skills & API specs`}
	size="lg"
>
	{#if bundleLoading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else}
		<div class="space-y-3">
			<Alert tone="neutral">
				A spec or skill attached here belongs to <strong>{bundleFor?.name}</strong> and inherits its
				base URL and credentials — which is why there is no auth to fill in. Uploading a spec also
				rebuilds the action catalog in the same step, so the agent and the workflow builder cannot
				end up describing this system differently.
			</Alert>

			<!-- onchange as well as the binding: switching tabs has to clear the
			     feedback belonging to the tab being left, and a binding alone cannot
			     tell a user's click apart from a programmatic reset. -->
			<Tabs
				bind:value={bundleTab}
				tabs={[
					{ value: 'specs', label: `API specs (${bundle.specs.length})` },
					{ value: 'skills', label: `Skills (${bundle.skills.length})` }
				]}
				onchange={clearAttachFeedback}
			/>

			{#if bundleTab === 'specs'}
				{#if bundle.specs.length === 0}
					<div class="px-4 py-6 text-center text-sm text-surface-600-400">
						No spec attached yet. Load one and this integration's operations become callable.
					</div>
				{:else}
					<div class="space-y-1">
						{#each bundle.specs as sp (sp.id)}
							<div
								class="flex items-center gap-3 rounded-lg border border-surface-100-900 px-3 py-2"
							>
								<code class="min-w-0 flex-1 truncate text-sm font-medium">{sp.api}</code>
								<Badge tone="neutral">{sp.operationCount} ops</Badge>
								<a class="text-xs underline text-surface-600-400" href={`/admin/specs?integration=${bundleFor?.id}`}>
									Edit
								</a>
							</div>
						{/each}
					</div>
				{/if}

				<fieldset class="ui-surface space-y-3 rounded-lg p-3">
					<legend class="px-1 text-sm font-medium">Attach an OpenAPI spec</legend>
					<input
						bind:this={specInput}
						type="file"
						accept=".yaml,.yml,.json,text/yaml,application/json"
						class="hidden"
						onchange={onSpecPick}
					/>
					<div class="flex items-end gap-2">
						<div class="flex-1">
							<Input
								label="api name"
								bind:value={specApi}
								hint="What the agent passes to discover_operations, e.g. netbox. Re-using a name replaces that spec."
							/>
						</div>
						<Button variant="secondary" onclick={() => specInput?.click()}>
							<Upload size={15} />Load file
						</Button>
					</div>
					<CodeEditor
						label="OpenAPI document"
						language="yaml"
						bind:value={specContent}
						rows={10}
						hint="YAML or JSON. Parsed before it saves — an unparseable spec is refused rather than stored with an empty action list."
					/>
					{#if attachError}
						<Alert tone="error">
							{attachError}
							{#if adoptable === 'spec'}
								<div class="mt-2 space-y-1.5">
									<p class="text-xs">
										Taking it over replaces its stored document with the one above, and
										repoints it at {bundleFor?.name}'s base URL and credentials. Its own
										are discarded.
									</p>
									<Button variant="secondary" loading={attaching} onclick={() => attachSpec(true)}>
										Replace it and attach to {bundleFor?.name}
									</Button>
								</div>
							{/if}
						</Alert>
					{/if}
					{#if attachOk}<Alert tone="success">{attachOk}</Alert>{/if}

					<div class="flex justify-end">
						<Button variant="primary" loading={attaching} onclick={() => attachSpec()}>
							Attach spec
						</Button>
					</div>
				</fieldset>
			{:else}
				{#if bundle.skills.length === 0}
					<div class="px-4 py-6 text-center text-sm text-surface-600-400">
						No skill attached yet. A skill here tells the agent how this particular system behaves.
					</div>
				{:else}
					<div class="space-y-1">
						{#each bundle.skills as sk (sk.id)}
							<div
								class="flex items-center gap-3 rounded-lg border border-surface-100-900 px-3 py-2"
							>
								<span class="min-w-0 flex-1 truncate text-sm font-medium">{sk.name}</span>
								<Badge tone="neutral">priority {sk.priority}</Badge>
								<a class="text-xs underline text-surface-600-400" href={`/admin/skills?integration=${bundleFor?.id}`}>
									Edit
								</a>
							</div>
						{/each}
					</div>
				{/if}

				<fieldset class="ui-surface space-y-3 rounded-lg p-3">
					<legend class="px-1 text-sm font-medium">Attach a prompt skill</legend>
					<input
						bind:this={skillInput}
						type="file"
						accept=".md,.markdown,text/markdown"
						class="hidden"
						onchange={onSkillPick}
					/>
					<div class="flex items-end gap-2">
						<div class="flex-1">
							<Input
								label="Skill name"
								bind:value={skillName}
								hint="Unique. Re-using a name replaces that skill."
							/>
						</div>
						<Button variant="secondary" onclick={() => skillInput?.click()}>
							<Upload size={15} />Load .md
						</Button>
					</div>
					<CodeEditor
						label="Skill markdown"
						language="markdown"
						bind:value={skillContent}
						rows={10}
						hint="Merged into the agent's system prompt after the global skills. It takes effect on the next turn."
					/>
					{#if attachError}
						<Alert tone="error">
							{attachError}
							{#if adoptable === 'skill'}
								<div class="mt-2 space-y-1.5">
									<p class="text-xs">
										Taking it over replaces its stored content with the text above, and scopes
										it to {bundleFor?.name} so it stops applying to other conversations.
										<a class="underline" href="/admin/skills" target="_blank" rel="noreferrer">
											Read the current one first
										</a> if you did not write it.
									</p>
									<Button variant="secondary" loading={attaching} onclick={() => attachSkill(true)}>
										Replace it and attach to {bundleFor?.name}
									</Button>
								</div>
							{/if}
						</Alert>
					{/if}
					{#if attachOk}<Alert tone="success">{attachOk}</Alert>{/if}

					<div class="flex justify-end">
						<Button variant="primary" loading={attaching} onclick={() => attachSkill()}>
							Attach skill
						</Button>
					</div>
				</fieldset>
			{/if}
		</div>
	{/if}

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (bundleOpen = false)}>Close</Button>
	{/snippet}
</Modal>
