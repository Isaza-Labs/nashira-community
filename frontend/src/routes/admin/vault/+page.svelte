<script lang="ts">
	// The Vault: every stored login, key, token and secret value on one screen.
	//
	// It replaces the separate Credentials and Secrets pages. Those were two lists
	// for one question — "where do I keep this sensitive thing?" — and the answer
	// depended on knowing that a credential is a structured login other objects
	// link to, while a secret is a named value configuration points at. Here the
	// admin picks what they are storing (six kinds) and the page talks to the right
	// store. Both stores and both APIs are exactly what they were, so everything
	// that already links to a credential or references a secret keeps working.
	//
	// Nothing sensitive is ever shown: both APIs are write-only for secret material.

	import { onDestroy, onMount, tick, untrack } from 'svelte';
	import { afterNavigate, beforeNavigate, goto } from '$app/navigation';
	import { page } from '$app/state';
	import {
		listCredentials,
		createCredential,
		updateCredential,
		deleteCredential,
		type Credential
	} from '$lib/api/credentials.api';
	import {
		listSecrets,
		createSecret,
		updateSecret,
		deleteSecret,
		secretRef,
		type Secret
	} from '$lib/api/secrets.api';
	import { listDevices } from '$lib/api/devices.api';
	import { listRepos } from '$lib/api/git.api';
	import { listIntegrations } from '$lib/api/integrations.api';
	import { listMcpServers } from '$lib/api/mcp.api';
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
		Alert,
		ErrorState,
		SearchInput,
		Toolbar,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import { absolute, timeAgo } from '$lib/utils/time';
	import { copyText } from '$lib/utils/clipboard';
	import { errorMessage, isOfflineError } from '$lib/api/client';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';
	import { vaultStoreVisible } from '$lib/nav/registry';
	import { VAULT_KINDS, kindInfo, kindNoun, kindOfCredential, isVaultKind, type VaultKind } from '$lib/vault/kinds';
	import { Plus, Pencil, Trash2, RefreshCw, Copy, ChevronLeft } from 'lucide-svelte';

	// One row of the list, whichever store it came from.
	type Entry = {
		key: string;
		kind: VaultKind;
		name: string;
		tag: string;
		detail: string;
		hasValue: boolean;
		updatedAt: string;
		credential: Credential | null;
		secret: Secret | null;
	};

	// A store whose old page an administrator hid stays hidden inside the Vault
	// until the Vault is given a navigation setting of its own.
	const showCredentials = $derived(vaultStoreVisible('credentials', navigationVisibilityStore.visibility));
	const showSecrets = $derived(vaultStoreVisible('secrets', navigationVisibilityStore.visibility));
	const kinds = $derived(
		VAULT_KINDS.filter((k) => (k.kind === 'secret' ? showSecrets : showCredentials))
	);
	const kindAllowed = (k: VaultKind) => (k === 'secret' ? showSecrets : showCredentials);

	// Each store loads, fails and reports on its own: a Secrets outage must not
	// take credential administration down with it, a slow credential scan must not
	// hold back secrets that already arrived, and a store that failed must never
	// look empty.
	let credentials = $state<Credential[]>([]);
	let secrets = $state<Secret[]>([]);
	let credentialsLoading = $state(true);
	let secretsLoading = $state(true);
	let credentialsError = $state<unknown>(null);
	let secretsError = $state<unknown>(null);
	// True when the credential list could not be read as one consistent snapshot
	// (it changed while being paged, or is larger than the page bound). The rows
	// shown are real; "all of them" is not a claim the page can make.
	let credentialsIncomplete = $state(false);
	// Whether each store's inventory has been read successfully at least once.
	// Rows can exist before that (an entry created while the first read is still
	// out), so "known in full" cannot be judged from the rows alone.
	let credentialsLoaded = $state(false);
	let secretsLoaded = $state(false);

	let search = $state('');

	// The type filter lives in the URL (?type=<kind>), so a link, the browser's
	// Back button and the chips all agree. A missing, unknown or not-permitted type
	// reads as All.
	const filter = $derived.by<'all' | VaultKind>(() => {
		const type = page.url.searchParams.get('type');
		return isVaultKind(type) && kinds.some((k) => k.kind === type) ? type : 'all';
	});

	function vaultUrl(change: (query: URLSearchParams) => void): string {
		const query = new URLSearchParams(page.url.search);
		change(query);
		const rest = query.toString();
		return `${page.url.pathname}${rest ? `?${rest}` : ''}`;
	}

	const QUIET = { replaceState: true, keepFocus: true, noScroll: true } as const;

	function setFilter(next: 'all' | VaultKind) {
		if (next === filter) return;
		goto(vaultUrl((q) => (next === 'all' ? q.delete('type') : q.set('type', next))), QUIET);
	}

	// Whether the material this kind authenticates with is stored.
	function credentialHasValue(c: Credential): boolean {
		switch (kindOfCredential(c)) {
			case 'ssh_key':
				return c.hasPrivateKey;
			case 'token':
			case 'api_key':
				return c.hasToken;
			case 'oauth2':
				return c.hasClientSecret;
			default:
				return c.hasPassword;
		}
	}

	function detailOf(c: Credential): string {
		const parts: string[] = [];
		if (c.username) parts.push(c.username);
		const kind = kindOfCredential(c);
		// Integrations and MCP servers send an API key in X-API-Key when no header
		// is named (IntegrationAuth, McpConnectionFactory).
		if (kind === 'api_key') parts.push(c.apiKeyHeader ? `header ${c.apiKeyHeader}` : 'header X-API-Key (default)');
		if (kind === 'oauth2' && c.clientId) parts.push(`client ${c.clientId}`);
		return parts.join(' · ');
	}

	// Built only from the stores this user may see: a store that was hidden while
	// the page was open disappears from the list at once, rows and actions with it.
	const entries = $derived<Entry[]>([
		...(showCredentials ? credentials : []).map((c) => ({
			key: `credential:${c.id}`,
			kind: kindOfCredential(c),
			name: c.name,
			tag: c.type ?? '',
			detail: detailOf(c),
			hasValue: credentialHasValue(c),
			updatedAt: c.updatedAt,
			credential: c,
			secret: null
		})),
		...(showSecrets ? secrets : []).map((s) => ({
			key: `secret:${s.id}`,
			kind: 'secret' as VaultKind,
			name: s.name,
			tag: '',
			detail: s.description ?? '',
			hasValue: s.hasValue,
			updatedAt: s.updatedAt,
			credential: null,
			secret: s
		}))
	]);

	// What is known about a store: 'ok' (read in full), 'partial' (rows are real
	// but may not be all of them) or 'unknown' (failed or still loading with
	// nothing to show).
	type StoreState = 'ok' | 'partial' | 'unknown';
	const credentialsState = $derived<StoreState>(
		!showCredentials
			? 'ok'
			: credentialsLoaded && !credentialsError && !credentialsIncomplete
				? 'ok'
				: credentials.length
					? 'partial'
					: 'unknown'
	);
	const secretsState = $derived<StoreState>(
		!showSecrets ? 'ok' : secretsLoaded && !secretsError ? 'ok' : secrets.length ? 'partial' : 'unknown'
	);

	function stateFor(chip: 'all' | VaultKind): StoreState {
		if (chip === 'secret') return secretsState;
		if (chip !== 'all') return credentialsState;
		if (credentialsState === 'ok' && secretsState === 'ok') return 'ok';
		return credentialsState === 'unknown' && secretsState === 'unknown' ? 'unknown' : 'partial';
	}

	// A count is a claim about a whole store. When the store is not known in full
	// the count says so ("12+", "?") instead of looking exact.
	function countFor(chip: 'all' | VaultKind): string {
		const n = chip === 'all' ? entries.length : entries.filter((e) => e.kind === chip).length;
		const state = stateFor(chip);
		return state === 'ok' ? String(n) : state === 'partial' || n ? `${n}+` : '?';
	}

	const visible = $derived.by(() => {
		const q = search.trim().toLowerCase();
		return entries
			.filter((e) => filter === 'all' || e.kind === filter)
			.filter(
				(e) =>
					!q ||
					e.name.toLowerCase().includes(q) ||
					e.detail.toLowerCase().includes(q) ||
					e.tag.toLowerCase().includes(q)
			)
			.sort((a, b) => a.name.localeCompare(b.name));
	});

	// The stores the current filter looks at.
	const selected = $derived({
		credentials: showCredentials && filter !== 'secret',
		secrets: showSecrets && (filter === 'all' || filter === 'secret')
	});
	const selectedLoading = $derived(
		(selected.credentials && credentialsLoading && !credentialsLoaded) ||
			(selected.secrets && secretsLoading && !secretsLoaded)
	);

	// What an empty table means depends on why it is empty, and only for the
	// stores the filter is showing. Nothing definite is said while those stores
	// are not known in full: "nothing matches" and "the Vault is empty" are claims
	// about everything, made only when everything has been read.
	const emptyText = $derived.by(() => {
		const state = stateFor(filter);
		const searching = !!search.trim();
		const anyForFilter = entries.some((e) => filter === 'all' || e.kind === filter);
		if (state !== 'ok') {
			if (selectedLoading) return 'Still loading entries…';
			return searching || anyForFilter
				? 'No matches among the entries that loaded; some could not be read.'
				: 'These entries could not be loaded — see the message above.';
		}
		if (searching && anyForFilter) return 'Nothing matches this search.';
		if (filter !== 'all') return `No ${kindNoun(filter)} entries yet.`;
		return 'The Vault is empty — add the logins, keys, tokens and secret values that devices, repositories, integrations and workflows need.';
	});

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'kind', header: 'Type', sortValue: (e: Entry) => kindInfo(e.kind).label },
		{ key: 'tag', header: 'Tag' },
		// Sorts as the old pages did: credentials by username, secret values by
		// description, with rows that have neither at the end.
		{
			key: 'detail',
			header: 'Details',
			sortValue: (e: Entry) => (e.credential ? e.credential.username || null : e.secret?.description || null)
		},
		{ key: 'value', header: 'Value', sortValue: (e: Entry) => e.hasValue },
		{ key: 'links', header: 'Known links', sortable: false },
		{ key: 'updated', header: 'Updated', sortValue: (e: Entry) => e.updatedAt },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	// ── Loading ─────────────────────────────────────────────────────────────

	// The server caps a page at 200 rows and sorts by name, which can change
	// between requests. Search and the type counts are claims about the whole
	// store, so every page of credential metadata is read (the rows are small and
	// hold nothing secret) — and the read is checked: rows are keyed by id so a
	// row that moved between pages is never listed twice, and a read during which
	// the total changed, a row repeated or the page bound ran out is retried, then
	// reported as incomplete rather than passed off as the whole list.
	const PAGE = 200;
	const MAX_PAGES = 50;
	const READ_ATTEMPTS = 3;

	async function readAllCredentials(): Promise<{ items: Credential[]; complete: boolean }> {
		let last: Credential[] = [];
		for (let attempt = 0; attempt < READ_ATTEMPTS; attempt++) {
			const byId = new Map<string, Credential>();
			let offset = 0;
			let total = -1;
			let stable = true;
			let finished = false;
			for (let i = 0; i < MAX_PAGES; i++) {
				// Left mid-read: the remaining pages are not asked for.
				if (gone) return { items: last, complete: false };
				const res = await listCredentials(PAGE, offset);
				if (total === -1) total = res.total;
				else if (res.total !== total) stable = false;
				for (const c of res.items) {
					if (byId.has(c.id)) stable = false;
					byId.set(c.id, c);
				}
				offset += res.items.length;
				if (res.items.length === 0 || offset >= res.total) {
					finished = true;
					break;
				}
			}
			last = [...byId.values()];
			// More rows than the bound allows: reading again would not help.
			if (!finished) return { items: last, complete: false };
			if (stable && byId.size === total) return { items: last, complete: true };
		}
		return { items: last, complete: false };
	}

	// Each guards against an older, slower read overwriting a newer one.
	// Set when the page is torn down (the admin went elsewhere). From then on
	// nothing is read, a write that has not been sent yet is not sent, reads still
	// in the air are discarded, and anything the admin has to be told goes to a
	// toast, because the form it belonged to is no longer on screen.
	let gone = false;
	let credentialsGeneration = 0;
	let secretsGeneration = 0;

	async function loadCredentials() {
		if (gone) return;
		const generation = ++credentialsGeneration;
		if (!showCredentials) {
			credentials = [];
			credentialsError = null;
			credentialsIncomplete = false;
			credentialsLoaded = false;
			credentialsLoading = false;
			return;
		}
		credentialsLoading = true;
		try {
			const read = await readAllCredentials();
			if (generation !== credentialsGeneration) return;
			credentials = read.items;
			credentialsIncomplete = !read.complete;
			credentialsLoaded = true;
			credentialsError = null;
		} catch (e) {
			if (generation !== credentialsGeneration) return;
			credentialsError = e;
		} finally {
			if (generation === credentialsGeneration) credentialsLoading = false;
		}
	}

	async function loadSecrets() {
		if (gone) return;
		const generation = ++secretsGeneration;
		if (!showSecrets) {
			secrets = [];
			secretsError = null;
			secretsLoaded = false;
			secretsLoading = false;
			return;
		}
		secretsLoading = true;
		try {
			const rows = await listSecrets();
			if (generation !== secretsGeneration) return;
			secrets = rows;
			secretsLoaded = true;
			secretsError = null;
		} catch (e) {
			if (generation !== secretsGeneration) return;
			secretsError = e;
		} finally {
			if (generation === secretsGeneration) secretsLoading = false;
		}
	}

	// Credentials, then the known links that describe them. Every re-read of the
	// credential store goes through here, so a write's links are never left stale.
	function reloadCredentials() {
		return loadCredentials().then(loadLinks);
	}

	// Reads both stores side by side; neither waits for the other.
	function load() {
		void reloadCredentials();
		void loadSecrets();
	}

	// ── Known links ─────────────────────────────────────────────────────────
	// Where a credential is linked by id: devices, repositories, integrations and
	// MCP servers. It is a hint, not an inventory. Only the first page of each list
	// is read (reading whole inventories to decorate a column is not worth it),
	// any of the four may be off or out of reach, and nothing here sees a workflow
	// step that names a credential or a ${secret:…} reference inside a
	// configuration. So the column reports what was found, says when it looked,
	// and never says "unused".

	type LinkSource = { what: string; plural: string; state: 'complete' | 'partial' | 'failed' };
	let links = $state<Record<string, string[]>>({});
	let linkSources = $state<LinkSource[]>([]);
	let linksCheckedAt = $state<Date | null>(null);
	let linksGeneration = 0;

	const clock = (d: Date) => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

	async function loadLinks() {
		if (gone) return;
		const generation = ++linksGeneration;
		// Nothing to link to: do not read four lists to learn that.
		if (!showCredentials || credentials.length === 0) {
			links = {};
			linkSources = [];
			linksCheckedAt = null;
			return;
		}
		const [devices, repos, integrations, mcp] = await Promise.allSettled([
			listDevices(PAGE),
			listRepos(PAGE),
			listIntegrations(PAGE),
			listMcpServers(PAGE)
		]);
		if (generation !== linksGeneration) return;

		const found: Record<string, Record<string, number>> = {};
		const add = (id: string | null | undefined, what: string) => {
			if (!id) return;
			(found[id] ??= {})[what] = (found[id][what] ?? 0) + 1;
		};
		const sources: LinkSource[] = [];
		const read = <T,>(
			result: PromiseSettledResult<{ total: number; items: T[] }>,
			what: string,
			plural: string,
			idOf: (item: T) => string | null | undefined
		) => {
			if (result.status !== 'fulfilled') {
				sources.push({ what, plural, state: 'failed' });
				return;
			}
			for (const item of result.value.items) add(idOf(item), what);
			sources.push({
				what,
				plural,
				state: result.value.total > result.value.items.length ? 'partial' : 'complete'
			});
		};
		read(devices, 'device', 'devices', (d) => d.credentialId);
		read(repos, 'repository', 'repositories', (r) => r.authCredentialId);
		read(integrations, 'integration', 'integrations', (i) => i.authCredentialId);
		read(mcp, 'MCP server', 'MCP servers', (m) => m.authCredentialId);

		const pluralOf = Object.fromEntries(sources.map((src) => [src.what, src.plural]));
		const out: Record<string, string[]> = {};
		for (const [id, byKind] of Object.entries(found))
			out[id] = Object.entries(byKind).map(([what, n]) => `${n} ${n === 1 ? what : pluralOf[what]}`);
		links = out;
		linkSources = sources;
		linksCheckedAt = new Date();
	}

	// One sentence under the table saying what the column covers, what it does
	// not, and when it was read.
	const linksNote = $derived.by(() => {
		if (!linkSources.length || !linksCheckedAt) return '';
		const read = linkSources.filter((s) => s.state !== 'failed').map((s) => s.plural);
		const partial = linkSources.filter((s) => s.state === 'partial').map((s) => s.plural);
		const failed = linkSources.filter((s) => s.state === 'failed').map((s) => s.plural);
		const parts: string[] = [];
		parts.push(
			read.length
				? `Known links were read from ${read.join(', ')} at ${clock(linksCheckedAt)}; Refresh to update.`
				: 'Known links could not be read.'
		);
		if (partial.length) parts.push(`Only the first ${PAGE} ${partial.join(' and ')} were checked.`);
		if (failed.length) parts.push(`Not available here: ${failed.join(', ')}.`);
		parts.push('Workflow steps and ${secret:…} references inside configurations are not counted, so an empty cell does not mean unused.');
		return parts.join(' ');
	});

	// Loaded once, on arrival. Not an effect: the API client reads the auth token,
	// and an effect would re-run the whole load every time the token refreshes.
	onMount(() => {
		load();
		consumeUrl();
	});

	// A navigation setting can change while the Vault is open (an administrator
	// saves one in another tab, or a save made just before arriving lands late).
	// When a store stops being visible its data, errors and pending reads are
	// dropped and a form for it is closed; when one becomes visible it is loaded.
	let seenStores: string | null = null;
	$effect(() => {
		const stores = `${showCredentials}|${showSecrets}`;
		untrack(() => {
			if (seenStores === null || stores === seenStores) {
				seenStores = stores;
				return;
			}
			seenStores = stores;
			if (modalOpen && step === 'form' && !kindAllowed(kind)) closeForm();
			load();
			consumeUrl(true);
		});
	});

	// ── The entry form ──────────────────────────────────────────────────────
	// Step 1 picks the kind (create only); step 2 is that kind's fields.

	let modalOpen = $state(false);
	let step = $state<'pick' | 'form'>('pick');
	let kind = $state<VaultKind>('login');
	let editing = $state<Entry | null>(null);
	// The form whose save is in flight, if any. Scoped to that form: another form
	// opened meanwhile is not disabled by a request it never made.
	let savingForm = $state<number | null>(null);
	let formError = $state('');
	// Identifies the open form, so a late result is applied to the form it came
	// from — or reported as a toast if that form has since been closed or replaced.
	let formId = $state(0);
	const saving = $derived(savingForm !== null && savingForm === formId);

	let fName = $state('');
	let fTag = $state('');
	let fUsername = $state('');
	let fPassword = $state('');
	let fKey = $state('');
	let fPassphrase = $state('');
	let fToken = $state('');
	let fApiKeyHeader = $state('');
	let fClientId = $state('');
	let fClientSecret = $state('');
	let fTokenUrl = $state('');
	let fScopes = $state('');
	let fDescription = $state('');
	let fValue = $state('');

	// A stored credential can change from one login kind to another (password to
	// key, say), as it always could. It cannot become a secret value or the other
	// way round: those are different stores with different references.
	const credentialKindOptions = VAULT_KINDS.filter((k) => k.authMethod).map((k) => ({
		value: k.kind,
		label: k.label
	}));

	// Mirrors the backend rule for secret names so bad input is flagged before a round-trip.
	const SECRET_NAME_RE = /^[a-z0-9](?:[a-z0-9_-]{1,62}[a-z0-9])?$/;

	// Sensitive material typed into the form. Cleared whenever it is no longer
	// needed — the form closed, saved, or switched to a different kind — so nothing
	// typed for one kind is submitted with another or lingers in memory.
	function clearMaterial() {
		fPassword = '';
		fKey = '';
		fPassphrase = '';
		fToken = '';
		fClientSecret = '';
		fValue = '';
	}

	function resetFields() {
		clearMaterial();
		fName = '';
		fTag = '';
		fUsername = '';
		fApiKeyHeader = '';
		fClientId = '';
		fTokenUrl = '';
		fScopes = '';
		fDescription = '';
		formError = '';
	}

	// The tag a new credential of this kind starts with; '' for a secret value.
	const defaultTagOf = (k: VaultKind) => kindInfo(k).defaultTag ?? '';

	const FORM = '[data-vault-form]';

	async function focusFirstField() {
		await tick();
		document
			.querySelector<HTMLElement>(`${FORM} input:not([disabled]), ${FORM} textarea:not([disabled])`)
			?.focus();
	}

	function openCreate(preset?: VaultKind) {
		formId++;
		editing = null;
		resetFields();
		const allowed = preset && kindAllowed(preset) ? preset : undefined;
		if (allowed) {
			kind = allowed;
			formKind = allowed;
			fTag = defaultTagOf(allowed);
			step = 'form';
			focusFirstField();
		} else {
			step = 'pick';
		}
		modalOpen = true;
	}

	// Choosing a type from the picker. Coming back to the type that was already
	// being filled in keeps the draft; choosing a different one drops what was
	// typed for the old one.
	function pick(k: VaultKind) {
		if (k !== kind) clearMaterial();
		// The tag follows the type until the admin writes one of their own.
		if (!editing && (!fTag.trim() || fTag === defaultTagOf(kind))) fTag = defaultTagOf(k);
		kind = k;
		formKind = k;
		formError = '';
		step = 'form';
		focusFirstField();
	}

	async function backToTypes() {
		step = 'pick';
		await tick();
		document.querySelector<HTMLElement>(`${FORM} [data-kind="${kind}"]`)?.focus();
	}

	function openEdit(e: Entry) {
		formId++;
		editing = e;
		resetFields();
		kind = e.kind;
		formKind = e.kind;
		step = 'form';
		fName = e.name;
		if (e.credential) {
			const c = e.credential;
			fTag = c.type;
			fUsername = c.username ?? '';
			fApiKeyHeader = c.apiKeyHeader ?? '';
			fClientId = c.clientId ?? '';
			fTokenUrl = c.tokenUrl ?? '';
			fScopes = c.scopes ?? '';
		} else if (e.secret) {
			fDescription = e.secret.description ?? '';
		}
		modalOpen = true;
	}

	// The Type selector on the edit form changes `kind` directly. Whatever was
	// typed for the previous kind is dropped, so a password typed for a login is
	// not sent along when the entry is saved as an API key. Material already
	// stored on the server is untouched, as before.
	let formKind: VaultKind = 'login';
	$effect(() => {
		const current = kind;
		untrack(() => {
			if (current === formKind) return;
			formKind = current;
			clearMaterial();
		});
	});

	// Closing the form by any route — Cancel, X, Escape, the backdrop or a save —
	// drops what was typed. The Modal reports X, Escape and the backdrop through
	// `onclose`; Cancel and a completed save go through closeForm.
	function onFormClosed() {
		formId++;
		resetFields();
	}

	function closeForm() {
		modalOpen = false;
		onFormClosed();
	}

	// Client-side mirror of the server's per-kind material rules. On edit, material
	// already stored (the has_* flags) satisfies the requirement.
	function missingMaterial(): string | null {
		const c = editing?.credential;
		const has = {
			key: !!fKey || !!c?.hasPrivateKey,
			token: !!fToken || !!c?.hasToken,
			secret: !!fClientSecret || !!c?.hasClientSecret
		};
		if (kind === 'ssh_key' && !has.key) return 'An SSH key entry requires a private key.';
		if (kind === 'token' && !has.token) return 'A token is required.';
		if (kind === 'api_key' && !has.token) return 'The API key value is required.';
		if (kind === 'oauth2') {
			if (!fClientId.trim()) return 'OAuth2 requires a client ID.';
			if (!has.secret) return 'OAuth2 requires a client secret.';
			if (!fTokenUrl.trim()) return 'OAuth2 requires a token URL.';
		}
		return null;
	}

	function validate(): string {
		if (kind === 'secret') {
			if (!SECRET_NAME_RE.test(fName.trim()))
				return 'Name must be 2–64 chars, lowercase letters/digits/hyphens/underscores.';
			if (!editing && !fValue) return 'A value is required when creating a secret value.';
			return '';
		}
		if (!fName.trim()) return 'Name is required.';
		return missingMaterial() ?? '';
	}

	// What a successful write returned goes straight into the list: a refresh that
	// then fails must not bring back a deleted row or hide a change that was made.
	//
	// Writes go out one at a time, in the order they were made. That is what keeps
	// overlapping changes honest without any bookkeeping about which answer is
	// newer: an edit left waiting, then a delete of the same entry, reach the
	// server and are applied here in that order, so the delete has the last word;
	// a second edit that fails cannot erase a first that succeeded. Applying a
	// change also retires any read in flight for that store, since that read began
	// before the change and would overwrite it.
	let writes: Promise<unknown> = Promise.resolve();

	// A write runs only after the one before it has settled, whether that was a
	// success or a failure — never merely after a wait.
	//
	// So that one stalled reply cannot hold every later change forever, each write
	// has a deadline. The API client's own timeout covers a request until its
	// headers arrive; a body that then never finishes, or a session refresh that
	// hangs, is not covered by it. Past the deadline the write is settled as
	// failed and its answer, should it ever come, is ignored — which is what makes
	// this safe where "wait a while, then let the next one through" was not: a
	// late answer can no longer be applied on top of whatever came after it. The
	// store is then re-read, because the change may or may not have been made.
	//
	// `stillWanted` is asked at the moment the write's turn comes: while it waited
	// the form it came from may have been closed, or its store hidden from this
	// user. A write that is no longer wanted is not sent at all, which is different
	// from one already on the wire, which is always seen through.
	// `left` marks a write withdrawn because the page itself was left, which —
	// unlike a form the admin closed — is said out loud: they pressed Save or
	// Delete and it did not happen.
	class Withdrawn extends Error {
		left: boolean;
		constructor(left = false) {
			super();
			this.left = left;
		}
	}
	class Unanswered extends Error {
		constructor() {
			super('The server did not finish answering. The change may or may not have been saved — check the list.');
		}
	}
	const WRITE_DEADLINE_MS = 45_000;

	// A write the client itself gave up on — its timeout passed before any answer
	// began, or the connection failed — is in the same position as one past the
	// deadline: it was sent, and the server may have made the change before the
	// answer was lost. So it is reported the same way and the store is re-read,
	// rather than the admin being told it failed and invited to send it again.
	const NO_ANSWER = 'No answer came back from the server. The change may or may not have been saved — check the list.';
	const outcomeUnknown = (e: unknown) => e instanceof Unanswered || isOfflineError(e);
	const outcomeUnknownText = (e: unknown) => (e instanceof Unanswered ? e.message : NO_ANSWER);

	// `onLate` runs if the operation settles after its deadline has already
	// passed. Its answer is still ignored, but that it answered at all is news: the
	// change has now definitely been made or refused, so the store is worth reading
	// again — the re-read made at the deadline may itself have failed.
	function withDeadline<T>(operation: Promise<T>, onLate: () => void): Promise<T> {
		return new Promise<T>((resolve, reject) => {
			let expired = false;
			const timer = setTimeout(() => {
				expired = true;
				reject(new Unanswered());
			}, WRITE_DEADLINE_MS);
			operation.then(
				(value) => {
					clearTimeout(timer);
					if (expired) onLate();
					else resolve(value);
				},
				(error) => {
					clearTimeout(timer);
					if (expired) onLate();
					else reject(error);
				}
			);
		});
	}

	function inOrder<T>(run: () => Promise<T>, stillWanted: () => boolean, reread: () => void): Promise<T> {
		const turn = writes
			.catch(() => undefined)
			.then(() => {
				// Asked in this order: a write the admin had already called off (form
				// closed, store hidden) stays silent even if the page was then left.
				if (!stillWanted()) throw new Withdrawn();
				if (gone) throw new Withdrawn(true);
				return withDeadline(run(), reread);
			});
		writes = turn.catch(() => undefined);
		return turn;
	}

	function putCredential(c: Credential) {
		credentialsGeneration++;
		credentialsLoading = false;
		credentials = credentials.some((x) => x.id === c.id)
			? credentials.map((x) => (x.id === c.id ? c : x))
			: [...credentials, c];
	}

	function putSecret(x: Secret) {
		secretsGeneration++;
		secretsLoading = false;
		secrets = secrets.some((y) => y.id === x.id) ? secrets.map((y) => (y.id === x.id ? x : y)) : [...secrets, x];
	}

	async function save() {
		if (saving) return;
		// The store may have been hidden from this user since the form opened.
		if (!kindAllowed(kind)) {
			closeForm();
			return;
		}
		formError = validate();
		if (formError) return;

		// Everything the request needs is captured here, before the first await:
		// the form may be closed (and its fields cleared) while the request is out.
		const submitted = formId;
		const submittedKind = kind;
		const entry = editing;
		const label = kindInfo(submittedKind).label;
		// The write, and what to do with its answer. Reconciling happens here in
		// save(), in the same step that starts the refresh, never inside the request.
		let run: () => Promise<Credential | Secret>;

		if (submittedKind === 'secret') {
			const s = entry?.secret;
			const description = fDescription.trim() || undefined;
			const value = fValue;
			const name = fName.trim();
			if (s) {
				// Replacing a stored value swaps the ciphertext the moment it is saved, so
				// confirm first. Editing the description alone must not touch the value.
				if (value) {
					const ok = await confirm({
						tone: 'danger',
						title: `Rotate "${s.name}"?`,
						message: 'Rotating replaces the value immediately; in-flight requests will use the new one.',
						confirmLabel: 'Rotate'
					});
					if (!ok) return;
					// The confirmation stayed open while anything could happen: the form
					// may have been closed or replaced, or the store hidden from this user.
					if (!kindAllowed('secret') || submitted !== formId || !modalOpen) return;
				}
				run = () => updateSecret(s.id, { description, ...(value ? { value } : {}) });
			} else {
				run = () => createSecret({ name, description, value });
			}
		} else {
			const payload = {
				name: fName.trim(),
				// A cleared tag gets the kind's default, not the server's blanket "ssh".
				type: fTag.trim() || defaultTagOf(submittedKind) || 'ssh',
				username: fUsername.trim(),
				authMethod: kindInfo(submittedKind).authMethod ?? 'password',
				password: fPassword,
				privateKey: fKey,
				keyPassphrase: fPassphrase,
				token: fToken,
				apiKeyHeader: fApiKeyHeader.trim(),
				clientId: fClientId.trim(),
				clientSecret: fClientSecret,
				tokenUrl: fTokenUrl.trim(),
				scopes: fScopes.trim()
			};
			const c = entry?.credential;
			run = c ? () => updateCredential(c.id, payload) : () => createCredential(payload);
		}

		savingForm = submitted;
		try {
			// Not yet sent when its turn comes: only if the form it was submitted from
			// is still the open one and the store is still this user's to change.
			const reread = () => void (submittedKind === 'secret' ? loadSecrets() : reloadCredentials());
			const written = await inOrder(
				run,
				() => kindAllowed(submittedKind) && modalOpen && submitted === formId,
				reread
			);
			// Applied as it arrives (writes are in order, so nothing newer has been
			// applied before it), then the store is re-read in the background. The
			// write is done and the form is free whether or not the list comes back.
			if (submittedKind === 'secret') {
				putSecret(written as Secret);
				void loadSecrets();
			} else {
				putCredential(written as Credential);
				void reloadCredentials();
			}
			if (modalOpen && submitted === formId) closeForm();
			toast.success(entry ? `${label} updated` : `${label} added to the Vault`);
		} catch (e) {
			// Withdrawn before it was sent: nothing happened, so nothing is reported —
			// unless it was withdrawn because the Vault was left with it still waiting.
			if (e instanceof Withdrawn) {
				if (e.left)
					toast.info(`${label} not saved`, {
						description: 'The Vault was closed before this change could be sent.'
					});
				return;
			}
			// Never answered: the list is the only way to learn whether it was saved,
			// and the admin is told exactly that rather than "it failed".
			const unanswered = outcomeUnknown(e);
			if (unanswered) void (submittedKind === 'secret' ? loadSecrets() : reloadCredentials());
			// Shown in the form it came from. If that form is gone, the failure still
			// has to reach the admin, so it becomes a toast.
			if (!gone && modalOpen && submitted === formId) formError = unanswered ? outcomeUnknownText(e) : errorMessage(e);
			else if (unanswered) toast.error(outcomeUnknownText(e));
			else toast.fromError(e, `Failed to save the ${kindNoun(submittedKind)}`);
		} finally {
			if (savingForm === submitted) savingForm = null;
		}
	}

	async function remove(e: Entry) {
		if (!kindAllowed(e.kind)) return;
		const known = e.credential ? (links[e.credential.id] ?? []) : [];
		const ok = await confirm({
			title: `Delete "${e.name}"?`,
			message: e.credential
				? `"${e.name}" will be removed. Devices, repositories, integrations and MCP servers referencing it lose their credential.` +
					(known.length && linksCheckedAt
						? ` Known links when last checked (${clock(linksCheckedAt)}): ${known.join(', ')}.`
						: '')
				: 'Anything referencing it will break until a replacement is created.',
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		// The confirmation stayed open while the store may have been hidden from
		// this user; a delete confirmed after that must not go out.
		if (!kindAllowed(e.kind)) return;
		try {
			if (e.credential) {
				const id = e.credential.id;
				await inOrder(
					() => deleteCredential(id),
					() => kindAllowed(e.kind),
					() => void reloadCredentials()
				);
				credentialsGeneration++;
				credentialsLoading = false;
				credentials = credentials.filter((c) => c.id !== id);
				const { [id]: _gone, ...rest } = links;
				links = rest;
				void reloadCredentials();
			} else if (e.secret) {
				const id = e.secret.id;
				await inOrder(
					() => deleteSecret(id),
					() => kindAllowed(e.kind),
					() => void loadSecrets()
				);
				secretsGeneration++;
				secretsLoading = false;
				secrets = secrets.filter((x) => x.id !== id);
				void loadSecrets();
			}
			toast.success(`${kindInfo(e.kind).label} deleted`);
		} catch (err) {
			if (err instanceof Withdrawn) {
				if (err.left)
					toast.info(`The delete of "${e.name}" was not sent`, {
						description: 'The Vault was closed before the delete could be sent.'
					});
				return;
			}
			if (outcomeUnknown(err)) {
				void (e.secret ? loadSecrets() : reloadCredentials());
				toast.error(outcomeUnknownText(err));
				return;
			}
			toast.fromError(err, "Couldn't delete the entry");
		}
	}

	// Copies the ${secret:…} reference for a secret value, so it can be pasted into
	// an auth config without remembering the syntax. copyText falls back to the
	// legacy path on insecure origins; only when both fail is the reference shown.
	async function copyRef(s: Secret) {
		const ref = secretRef(s.name);
		if (await copyText(ref)) toast.success('Reference copied', { description: ref });
		else toast.info('Copy failed — reference:', { description: ref });
	}

	// ?new=1 opens the form (straight to a kind's fields when ?type names one).
	// The command palette's create action and the old /admin/credentials and
	// /admin/secrets URLs arrive this way.
	//
	// Handled after each navigation has finished (SvelteKit reuses this page when
	// only the query changes, so the palette action has to work while already
	// here). `new=1` is consumed: it is removed from the URL once acted on, so
	// cancelling the form does not reopen it and choosing the action again works a
	// second time. A ?type that is unknown, or names a kind this user may not see,
	// is removed the same way, so the URL never claims a filter that is not applied.
	//
	// The clean-up is its own navigation and is started only once the one that
	// brought us here is complete: started during it, it would replace it before
	// the layout had reset the scroll position for the new page.
	//
	// It runs on arrival as well as after navigations: the shell renders this page
	// only once it knows what the user may see, which is after the navigation that
	// loaded the app has already reported itself finished. `handledHref` keeps the
	// two triggers from acting on the same URL twice.
	//
	// While the user is on their way to another page nothing is cleaned up: the
	// address still reads as the Vault's until the new page is ready, and a
	// clean-up started then would replace that navigation and keep them here.
	let handledHref = '';
	let leaving = false;
	let cleanupTimer: ReturnType<typeof setTimeout> | undefined;

	beforeNavigate(({ to }) => {
		leaving = !!to && to.url.pathname !== page.url.pathname;
		if (leaving) clearTimeout(cleanupTimer);
	});
	onDestroy(() => {
		gone = true;
		clearTimeout(cleanupTimer);
		// Reads still in the air belong to a page that no longer exists.
		credentialsGeneration++;
		secretsGeneration++;
		linksGeneration++;
	});

	function consumeUrl(force = false) {
		if (leaving) return;
		const href = page.url.href;
		if (!force && href === handledHref) return;
		handledHref = href;
		const type = page.url.searchParams.get('type');
		const wantsNew = page.url.searchParams.get('new') === '1';
		const staleType = type !== null && filter === 'all';
		if (wantsNew) openCreate(filter !== 'all' ? filter : undefined);
		if (!wantsNew && !staleType) return;
		clearTimeout(cleanupTimer);
		cleanupTimer = setTimeout(() => {
			// Only if nothing else has navigated, or begun to, in the meantime.
			if (leaving || page.url.href !== href) return;
			goto(
				vaultUrl((q) => {
					q.delete('new');
					if (staleType) q.delete('type');
				}),
				QUIET
			);
		}, 0);
	}

	afterNavigate(() => {
		// Still here after a navigation settled, so it was one within the Vault.
		leaving = false;
		consumeUrl();
	});

	// And whenever the address changes by any other route (Back and Forward, or
	// history changed from outside the router): deferred, so it runs after
	// whatever changed the address has finished. `handledHref` makes this a no-op
	// when afterNavigate already dealt with the same URL.
	$effect(() => {
		page.url.href;
		const timer = setTimeout(() => consumeUrl(), 0);
		return () => clearTimeout(timer);
	});

	const filterChips = $derived([
		{ value: 'all' as const, label: 'All' },
		...kinds.map((k) => ({ value: k.kind, label: k.label }))
	]);

	// Nothing at all to show and at least one store failed: the whole page is an error.
	const nothingLoaded = $derived(
		entries.length === 0 &&
			!!(credentialsError || secretsError) &&
			(!showCredentials || !!credentialsError) &&
			(!showSecrets || !!secretsError)
	);
	// Skeleton rows while a store the filter is showing has never been read and
	// there is nothing to show yet — judged after the search, so looking for an
	// entry of a store that is still loading shows "loading", not "no match".
	const tableLoading = $derived(selectedLoading && visible.length === 0);
</script>

<svelte:head><title>Vault · Nashira</title></svelte:head>

<PageHeader
	title="Vault"
	description="Every login, key, token and secret value Nashira uses to reach other systems. Stored encrypted; values are never shown again."
>
	{#snippet actions()}
		<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
		<Button variant="primary" onclick={() => openCreate(filter !== 'all' ? filter : undefined)}>
			<Plus size={15} />New entry
		</Button>
	{/snippet}
</PageHeader>

{#if nothingLoaded}
	<ErrorState error={credentialsError ?? secretsError} onRetry={load} />
{:else}
	{#if credentialsError}
		<div class="mb-3">
			<Alert tone="error" title="Logins, keys and tokens could not be loaded">
				<div class="flex flex-wrap items-center gap-3 text-sm">
					<span>{errorMessage(credentialsError)} Secret values below are unaffected.</span>
					<Button variant="ghost" onclick={() => void reloadCredentials()}>Retry</Button>
				</div>
			</Alert>
		</div>
	{/if}
	{#if secretsError}
		<div class="mb-3">
			<Alert tone="error" title="Secret values could not be loaded">
				<div class="flex flex-wrap items-center gap-3 text-sm">
					<span>{errorMessage(secretsError)} The other entries below are unaffected.</span>
					<Button variant="ghost" onclick={() => void loadSecrets()}>Retry</Button>
				</div>
			</Alert>
		</div>
	{/if}

	{#if credentialsIncomplete && !credentialsError}
		<div class="mb-3">
			<Alert tone="warning" title="The list of logins, keys and tokens may be incomplete">
				<div class="flex flex-wrap items-center gap-3 text-sm">
					<span>
						It changed while it was being read, or holds more than {PAGE * MAX_PAGES} entries. The rows shown are
						real; counts and search may miss some.
					</span>
					<Button variant="ghost" onclick={() => void reloadCredentials()}>Read again</Button>
				</div>
			</Alert>
		</div>
	{/if}

	<Toolbar>
		{#snippet left()}
			<div class="flex flex-wrap gap-1.5" role="group" aria-label="Filter by type">
				{#each filterChips as chip (chip.value)}
					<button
						type="button"
						aria-pressed={filter === chip.value}
						class={`rounded-full border px-3 py-1 text-xs transition ${
							filter === chip.value
								? 'border-primary-500 bg-primary-500/10 text-surface-950-50'
								: 'border-surface-200-800 text-surface-600-400 hover:text-surface-950-50'
						}`}
						onclick={() => setFilter(chip.value)}
					>
						{chip.label}
						<span class="ml-1 tabular-nums opacity-70">{countFor(chip.value)}</span>
					</button>
				{/each}
			</div>
		{/snippet}
		{#snippet right()}
			<SearchInput bind:value={search} placeholder="Search the Vault…" />
		{/snippet}
	</Toolbar>

	<div class="mt-3">
		<DataTable
			loading={tableLoading}
			{columns}
			rows={visible}
			rowKey={(e) => e.key}
			empty={emptyText}
		>
			{#snippet cell(row, col)}
				{#if col.key === 'name'}
					{#if row.secret}
						<span class="block min-w-[9rem] max-w-[16rem] font-mono text-sm [overflow-wrap:anywhere]">{row.name}</span>
						<!-- w-0 + min-w-full: the reference takes the width the name gives the
							     column and truncates there, instead of widening the column itself. -->
						<button
							type="button"
							class="block w-0 min-w-full truncate text-left font-mono text-xs text-surface-500 hover:text-surface-950-50"
							title="Copy this reference to paste into a configuration"
							onclick={() => copyRef(row.secret!)}
						>
							{secretRef(row.name)}
						</button>
					{:else}
						<span class="block min-w-[9rem] max-w-[16rem] font-medium [overflow-wrap:anywhere]">{row.name}</span>
					{/if}
				{:else if col.key === 'kind'}
					{@const info = kindInfo(row.kind)}
					<Badge tone={row.kind === 'secret' ? 'neutral' : 'primary'}>
						<span class="inline-flex items-center gap-1 whitespace-nowrap"><info.icon size={12} />{info.label}</span>
					</Badge>
				{:else if col.key === 'tag'}
					<span class="text-surface-600-400">{row.tag || '—'}</span>
				{:else if col.key === 'detail'}
					<span class="block min-w-[8rem] text-surface-600-400 [overflow-wrap:anywhere]">{row.detail || '—'}</span>
				{:else if col.key === 'value'}
					{#if row.hasValue}<Badge tone="success">set</Badge>{:else}<Badge tone="error">empty</Badge>{/if}
				{:else if col.key === 'links'}
					{#if row.credential && links[row.credential.id]}
						<!-- Each link stays on one line ("1 repository"); lines break only between links. -->
						<span class="text-surface-600-400"
							>{#each links[row.credential.id] as link, i (link)}{#if i}, {/if}<span class="whitespace-nowrap">{link}</span
								>{/each}</span
						>
					{:else}
						<span
							class="text-surface-500"
							title={row.secret
								? 'Secret values are used by reference, which is not tracked here.'
								: 'No link found in the lists that were checked. That does not mean unused.'}>—</span
						>
					{/if}
				{:else if col.key === 'updated'}
					<span class="whitespace-nowrap tabular-nums text-surface-600-400" title={absolute(row.updatedAt)}>
						{timeAgo(row.updatedAt)}
					</span>
				{:else if col.key === 'actions'}
					<div class="flex justify-end gap-1">
						{#if row.secret}
							<IconButton label="Copy reference" onclick={() => copyRef(row.secret!)}><Copy size={14} /></IconButton>
						{/if}
						<IconButton label="Edit entry" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
						<IconButton label="Delete entry" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
					</div>
				{/if}
			{/snippet}
		</DataTable>
		{#if linksNote && showCredentials}
			<p class="mt-2 text-xs text-surface-500">{linksNote}</p>
		{/if}
	</div>
{/if}

<Modal
	bind:open={modalOpen}
	onclose={onFormClosed}
	title={editing ? `Edit ${kindNoun(kind)}` : step === 'pick' ? 'New Vault entry' : `New ${kindNoun(kind)}`}
>
	<div data-vault-form>
	{#if step === 'pick'}
		<p class="mb-3 text-sm text-surface-600-400">What are you storing?</p>
		<div class="grid gap-2 sm:grid-cols-2">
			{#each kinds as k (k.kind)}
				<button
					type="button"
					data-kind={k.kind}
					class="flex items-start gap-3 rounded-lg border border-surface-200-800 p-3 text-left transition hover:border-primary-500 hover:bg-primary-500/5"
					onclick={() => pick(k.kind)}
				>
					<k.icon size={18} class="mt-0.5 shrink-0 text-primary-500" />
					<span>
						<span class="block text-sm font-medium">{k.label}</span>
						<span class="block text-xs text-surface-600-400">{k.desc}</span>
					</span>
				</button>
			{/each}
		</div>
	{:else}
		<div class="space-y-3">
			{#if formError}<Alert tone="error">{formError}</Alert>{/if}

			{#if kind === 'secret'}
				<Input
					label="Name"
					placeholder="netbox-token"
					bind:value={fName}
					disabled={!!editing || saving}
					required
					hint={editing
						? 'Names are immutable — references stay stable.'
						: 'Lowercase letters, digits, hyphens and underscores. It becomes part of the reference.'}
				/>
				<Textarea
					label="Description"
					placeholder="What this value authorizes — shown in the Vault only."
					rows={2}
					bind:value={fDescription}
					disabled={saving}
				/>
				<Textarea
					label={editing ? 'New value (leave empty to keep current)' : 'Value'}
					placeholder="Paste the secret value here."
					rows={3}
					bind:value={fValue}
					disabled={saving}
					hint={editing
						? 'Leave blank to update only the description. Submitting a value rotates it.'
						: 'Stored encrypted at rest. The value cannot be retrieved after submission — rotate to replace it.'}
				/>
				{#if fName.trim() && SECRET_NAME_RE.test(fName.trim())}
					<p class="text-xs text-surface-600-400">
						Reference: <code class="font-mono">{secretRef(fName.trim())}</code>
					</p>
				{/if}
			{:else}
				<div class="grid gap-3 sm:grid-cols-2">
					<Input label="Name" bind:value={fName} disabled={saving} required />
					{#if editing}
						<Select label="Type" bind:value={kind} disabled={saving} options={credentialKindOptions} />
					{:else}
						<Input label="Tag" bind:value={fTag} disabled={saving} placeholder={defaultTagOf(kind)} hint="Optional label for grouping, e.g. ssh, git_token, netbox" />
					{/if}
				</div>
				{#if editing}
					<Input label="Tag" bind:value={fTag} disabled={saving} placeholder={defaultTagOf(kind)} hint="Optional label for grouping, e.g. ssh, git_token, netbox" />
				{/if}

				{#if kind === 'login'}
					<div class="grid gap-3 sm:grid-cols-2">
						<Input label="Username" bind:value={fUsername} disabled={saving} />
						<Input label="Password" bind:value={fPassword} disabled={saving} type="password" hint={editing ? 'Leave blank to keep' : ''} />
					</div>
				{:else if kind === 'ssh_key'}
					<Input label="Username" bind:value={fUsername} disabled={saving} />
					<Textarea label="Private key" bind:value={fKey} disabled={saving} rows={4} hint={editing ? 'Leave blank to keep' : 'PEM contents'} />
					<Input
						label="Key passphrase"
						bind:value={fPassphrase}
						disabled={saving}
						type="password"
						hint={editing ? 'Leave blank to keep' : 'Optional'}
					/>
				{:else if kind === 'token'}
					<div class="grid gap-3 sm:grid-cols-2">
						<Input label="Username" bind:value={fUsername} disabled={saving} hint="Optional; e.g. the PAT owner (git uses it)" />
						<Input
							label="Token"
							bind:value={fToken}
							disabled={saving}
							type="password"
							hint={editing ? 'Leave blank to keep' : 'Bearer token or personal access token'}
						/>
					</div>
				{:else if kind === 'api_key'}
					<div class="grid gap-3 sm:grid-cols-2">
						<Input label="API key value" bind:value={fToken} disabled={saving} type="password" hint={editing ? 'Leave blank to keep' : ''} />
						<Input
							label="Header name"
							bind:value={fApiKeyHeader}
							disabled={saving}
							placeholder="X-API-Key"
							hint="Blank → X-API-Key, unless the integration or MCP server using it names another"
						/>
					</div>
				{:else if kind === 'oauth2'}
					<div class="grid gap-3 sm:grid-cols-2">
						<Input label="Client ID" bind:value={fClientId} disabled={saving} required />
						<Input
							label="Client secret"
							bind:value={fClientSecret}
							disabled={saving}
							type="password"
							hint={editing ? 'Leave blank to keep' : ''}
						/>
					</div>
					<Input label="Token URL" bind:value={fTokenUrl} disabled={saving} placeholder="https://idp.example.com/oauth2/token" required />
					<Input label="Scopes" bind:value={fScopes} disabled={saving} hint="Space-separated, e.g. read:devices write:configs" />
				{/if}
			{/if}
		</div>
	{/if}
	</div>
	{#snippet footer()}
		{#if step === 'form' && !editing}
			<Button variant="ghost" onclick={backToTypes} disabled={saving}><ChevronLeft size={15} />Type</Button>
		{/if}
		<Button variant="ghost" onclick={closeForm}>Cancel</Button>
		{#if step === 'form'}
			<Button variant="primary" loading={saving} onclick={save}>{editing ? 'Save changes' : 'Add to Vault'}</Button>
		{/if}
	{/snippet}
</Modal>
