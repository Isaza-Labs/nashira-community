// Integration CRUD (`/api/integrations`). Reads are Viewer; writes are Admin —
// an integration holds credentials and an SSRF opt-out, so the API gates it and
// the UI only hides the affordances.
//
// `authConfig` is write-only: the API never returns it, because it carries
// ${secret:secret:<name>:value} references that describe the secret store's layout.
// The
// read model gets `authMethod` + `hasCredentials` instead.

import { api, type ListResponse } from '$lib/api/client';
import { toIntegrationAuthConfig, type AuthDraft } from '$lib/auth/draft';

export type IntegrationStatus = 'unknown' | 'healthy' | 'degraded' | 'unreachable';

export type IntegrationAuthShape = {
	method: string;
	prefix: string | null;
	header: string | null;
	username: string | null;
	tokenUrl: string | null;
	clientId: string | null;
	scope: string | null;
};

interface AuthShapeShape {
	method: string;
	prefix: string | null;
	header: string | null;
	username: string | null;
	token_url: string | null;
	client_id: string | null;
	scope: string | null;
}

export type Integration = {
	id: string;
	name: string;
	slug: string;
	type: string;
	description: string | null;
	baseUrl: string;
	authMethod: string;
	hasCredentials: boolean;

	// auth_config carries its own secret, which wins over the linked credential. Shown
	// in the list because otherwise an integration that ignores its credential is
	// indistinguishable from one that uses it.
	hasInlineCredentials: boolean;

	// The non-secret half of auth_config — scheme, header name, prefix. Returned so an
	// edit form can show and preserve what is configured instead of re-deriving it.
	authShape: IntegrationAuthShape | null;

	// The static headers as stored (declared non-secret by the field's own contract).
	headers: string;

	authCredentialId: string | null;
	authCredentialName: string | null;
	verifySsl: boolean;
	allowPrivateNetwork: boolean;
	healthCheckPath: string | null;
	status: IntegrationStatus;
	lastCheckError: string | null;
	lastCheckedAt: string | null;
	enabled: boolean;
	specCount: number;
	actionCount: number;
	createdAt: string;
	updatedAt: string;
};

export interface IntegrationPayload {
	name: string;
	type: string;
	description: string;
	baseUrl: string;

	// The form edits a method plus typed fields; the JSON the backend wants is built
	// here, so no screen has to know the wire format.
	authMethod: string;
	authDraft: AuthDraft;

	// '' means "no stored credential" — the material comes from authDraft instead.
	authCredentialId: string;

	// Escape hatch for the advanced JSON editor: when set it replaces whatever the
	// typed fields would have produced.
	authConfigOverride?: string | null;

	headers: string;
	verifySsl: boolean;
	allowPrivateNetwork: boolean;
	healthCheckPath: string;
	enabled: boolean;
}

export type IntegrationHealth = {
	status: IntegrationStatus;
	statusCode: number | null;
	elapsedMs: number;
	error: string | null;
};

export type ActionSync = {
	created: number;
	updated: number;
	unchanged: number;
	disappeared: number;
	specCount: number;
};

export type IntegrationAction = {
	id: string;
	integrationId: string;
	operationId: string | null;
	name: string;
	description: string | null;
	method: string;
	path: string;
	category: string;
	readOnly: boolean;
	enabled: boolean;
	isActive: boolean;
	updatedAt: string;
};

interface IntegrationShape {
	integration_id: string;
	name: string;
	slug: string;
	type: string;
	description: string | null;
	base_url: string;
	auth_method: string;
	has_credentials: boolean;
	has_inline_credentials?: boolean;
	auth_shape?: AuthShapeShape | null;
	headers?: string | null;
	auth_credential_id: string | null;
	auth_credential_name: string | null;
	verify_ssl: boolean;
	allow_private_network: boolean;
	health_check_path: string | null;
	status: IntegrationStatus;
	last_check_error: string | null;
	last_checked_at: string | null;
	enabled: boolean;
	spec_count: number;
	action_count: number;
	created_at: string;
	updated_at: string;
}

interface ActionShape {
	integration_action_id: string;
	integration_id: string;
	operation_id: string | null;
	name: string;
	description: string | null;
	method: string;
	path: string;
	category: string;
	read_only: boolean;
	enabled: boolean;
	is_active: boolean;
	updated_at: string;
}

function toIntegration(i: IntegrationShape): Integration {
	return {
		id: i.integration_id,
		name: i.name,
		slug: i.slug,
		type: i.type,
		description: i.description,
		baseUrl: i.base_url,
		authMethod: i.auth_method,
		hasCredentials: i.has_credentials,
		hasInlineCredentials: i.has_inline_credentials ?? false,
		authShape: i.auth_shape
			? {
					method: i.auth_shape.method,
					prefix: i.auth_shape.prefix,
					header: i.auth_shape.header,
					username: i.auth_shape.username,
					tokenUrl: i.auth_shape.token_url,
					clientId: i.auth_shape.client_id,
					scope: i.auth_shape.scope
				}
			: null,
		headers: i.headers ?? '',
		authCredentialId: i.auth_credential_id ?? null,
		authCredentialName: i.auth_credential_name ?? null,
		verifySsl: i.verify_ssl,
		allowPrivateNetwork: i.allow_private_network,
		healthCheckPath: i.health_check_path,
		status: i.status,
		lastCheckError: i.last_check_error,
		lastCheckedAt: i.last_checked_at,
		enabled: i.enabled,
		specCount: i.spec_count,
		actionCount: i.action_count,
		createdAt: i.created_at,
		updatedAt: i.updated_at
	};
}

function toAction(a: ActionShape): IntegrationAction {
	return {
		id: a.integration_action_id,
		integrationId: a.integration_id,
		operationId: a.operation_id,
		name: a.name,
		description: a.description,
		method: a.method,
		path: a.path,
		category: a.category,
		readOnly: a.read_only,
		enabled: a.enabled,
		isActive: a.is_active,
		updatedAt: a.updated_at
	};
}

function toBody(p: IntegrationPayload, editing: boolean) {
	const usingStoredCredential = !!p.authCredentialId;

	// null means "leave the stored credentials alone" on update — the API never
	// returns them, so the form cannot resubmit what it has.
	const authConfig =
		p.authConfigOverride !== undefined && p.authConfigOverride !== null
			? p.authConfigOverride.trim() || null
			: toIntegrationAuthConfig(p.authMethod, p.authDraft, { editing, usingStoredCredential });

	return {
		name: p.name,
		type: p.type || null,
		description: p.description || null,
		base_url: p.baseUrl,
		auth_config: authConfig,
		// Explicit null detaches the credential; the backend tells that apart from an
		// absent field, which is why update always sends the key.
		auth_credential_id: p.authCredentialId || null,
		// Always a string, never null. Null means "leave the stored value alone", which
		// was harmless while the form could not read the headers back — and would now
		// mean deleting every row saves nothing.
		headers: p.headers.trim(),
		verify_ssl: p.verifySsl,
		allow_private_network: p.allowPrivateNetwork,
		health_check_path: p.healthCheckPath.trim() || null,
		enabled: p.enabled
	};
}

/** One skill staged in the create modal, before anything is persisted. */
export type StagedSkill = { name: string; content: string; priority: number };

/** One spec staged in the create modal. */
export type StagedSpec = { api: string; content: string };

export type BundleCreateResult = {
	integration: Integration;
	skillsCreated: number;
	specsCreated: number;
	actionsCreated: number;
};

// Creates the integration and everything attached to it in one transaction. An
// integration is a base URL until a spec gives it operations, so creating those in
// three calls means any failure leaves a half-configured system for someone to find
// later. Here the whole thing lands or none of it does — including the actions
// materialised from the specs.
export async function createIntegrationBundle(
	p: IntegrationPayload,
	skills: StagedSkill[],
	specs: StagedSpec[]
): Promise<BundleCreateResult> {
	const r = await api<{
		integration: IntegrationShape;
		skills_created: number;
		specs_created: number;
		actions_created: number;
	}>('/integrations/bundle', {
		method: 'POST',
		body: JSON.stringify({
			integration: toBody(p, false),
			skills: skills.map((sk) => ({
				name: sk.name.trim(),
				content: sk.content,
				priority: sk.priority
			})),
			specs: specs.map((sp) => ({ api: sp.api.trim().toLowerCase(), content: sp.content }))
		})
	});
	return {
		integration: toIntegration(r.integration),
		skillsCreated: r.skills_created,
		specsCreated: r.specs_created,
		actionsCreated: r.actions_created
	};
}

export async function listIntegrations(limit = 100, offset = 0): Promise<ListResponse<Integration>> {
	const res = await api<ListResponse<IntegrationShape>>(
		`/integrations?limit=${limit}&offset=${offset}`
	);
	return {
		total: res.total,
		limit: res.limit,
		offset: res.offset,
		items: res.items.map(toIntegration)
	};
}

export async function createIntegration(p: IntegrationPayload): Promise<Integration> {
	return toIntegration(
		await api<IntegrationShape>('/integrations', {
			method: 'POST',
			body: JSON.stringify(toBody(p, false))
		})
	);
}

export async function updateIntegration(id: string, p: IntegrationPayload): Promise<Integration> {
	return toIntegration(
		await api<IntegrationShape>(`/integrations/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p, true))
		})
	);
}

export async function deleteIntegration(id: string): Promise<void> {
	await api<IntegrationShape>(`/integrations/${id}`, { method: 'DELETE' });
}

// Authenticated probe against the live system. Operator, not Admin.
export async function checkIntegration(id: string): Promise<IntegrationHealth> {
	const r = await api<{
		status: IntegrationStatus;
		status_code: number | null;
		elapsed_ms: number;
		error: string | null;
	}>(`/integrations/${id}/check`, { method: 'POST' });
	return { status: r.status, statusCode: r.status_code, elapsedMs: r.elapsed_ms, error: r.error };
}

// Rebuilds the action catalog from the OpenAPI specs linked to this integration.
export async function syncActions(id: string): Promise<ActionSync> {
	const r = await api<{
		created: number;
		updated: number;
		unchanged: number;
		disappeared: number;
		spec_count: number;
	}>(`/integrations/${id}/sync-actions`, { method: 'POST' });
	return {
		created: r.created,
		updated: r.updated,
		unchanged: r.unchanged,
		disappeared: r.disappeared,
		specCount: r.spec_count
	};
}

export async function listActions(
	integrationId: string,
	limit = 200,
	offset = 0
): Promise<ListResponse<IntegrationAction>> {
	const res = await api<ListResponse<ActionShape>>(
		`/integrations/${integrationId}/actions?limit=${limit}&offset=${offset}`
	);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toAction) };
}

// ── The integration's own skills and specs ────────────────────────────────
//
// Both were always linkable — AiPromptSkill and AiApiSpec each carry an
// integration_id — but only from the other end. Giving NetBox a spec meant going
// to Admin → Specs, uploading it, remembering to pick the integration, then coming
// back here to run the sync. These endpoints collapse that into one call, and
// attaching a spec re-materialises the action catalog as part of it.

export type IntegrationSkill = {
	id: string;
	name: string;
	content: string;
	priority: number;
	updatedAt: string;
};

export type IntegrationSpec = {
	id: string;
	api: string;
	operationCount: number;
	updatedAt: string;
};

export type IntegrationBundle = {
	skills: IntegrationSkill[];
	specs: IntegrationSpec[];
};

/** What attaching a spec did to the action catalog. */
export type SpecAttachResult = {
	spec: IntegrationSpec;
	created: number;
	updated: number;
	unchanged: number;
	disappeared: number;
};

interface BundleSkillShape {
	ai_prompt_skill_id: string;
	name: string;
	content: string;
	priority: number;
	updated_at: string;
}

interface BundleSpecShape {
	ai_api_spec_id: string;
	api: string;
	operation_count: number;
	updated_at: string;
}

function toBundleSkill(s: BundleSkillShape): IntegrationSkill {
	return {
		id: s.ai_prompt_skill_id,
		name: s.name,
		content: s.content,
		priority: s.priority,
		updatedAt: s.updated_at
	};
}

function toBundleSpec(s: BundleSpecShape): IntegrationSpec {
	return {
		id: s.ai_api_spec_id,
		api: s.api,
		operationCount: s.operation_count,
		updatedAt: s.updated_at
	};
}

export async function getIntegrationBundle(id: string): Promise<IntegrationBundle> {
	const r = await api<{ skills: BundleSkillShape[]; specs: BundleSpecShape[] }>(
		`/integrations/${id}/bundle`
	);
	return { skills: r.skills.map(toBundleSkill), specs: r.specs.map(toBundleSpec) };
}

// Upsert by `api`: re-uploading the same name replaces that spec, which is what
// "load a new version" means. A second row would leave the agent with two
// documents describing one API and no way to tell which is current.
export async function attachIntegrationSpec(
	id: string,
	apiName: string,
	content: string,
	/** Take over an api name that currently belongs to no integration. */
	adopt = false
): Promise<SpecAttachResult> {
	const r = await api<{
		spec: BundleSpecShape;
		actions_created: number;
		actions_updated: number;
		actions_unchanged: number;
		actions_disappeared: number;
	}>(`/integrations/${id}/specs`, {
		method: 'POST',
		body: JSON.stringify({ api: apiName, content, adopt })
	});
	return {
		spec: toBundleSpec(r.spec),
		created: r.actions_created,
		updated: r.actions_updated,
		unchanged: r.actions_unchanged,
		disappeared: r.actions_disappeared
	};
}

// Upsert by name. Takes effect on the next turn — the prompt loader is invalidated
// server-side, so there is no restart and no cache to wait out.
export async function attachIntegrationSkill(
	id: string,
	name: string,
	content: string,
	priority?: number,
	/** Take over a global skill of the same name instead of failing on it. */
	adopt = false
): Promise<IntegrationSkill> {
	return toBundleSkill(
		await api<BundleSkillShape>(`/integrations/${id}/skills`, {
			method: 'POST',
			body: JSON.stringify({ name, content, priority, adopt })
		})
	);
}
