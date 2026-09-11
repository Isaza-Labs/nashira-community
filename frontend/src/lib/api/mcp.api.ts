// MCP server registry (`/api/mcp/servers`, Admin for writes). Nashira is the MCP
// client: it connects to registered servers, caches their tool catalogs, and calls
// them.
//
// `authConfig` is write-only. On update, omitting it keeps the stored credentials —
// the API never returns them, so the form has nothing to resubmit; `clear_auth_config`
// is the explicit way to remove them.

import { api, type ListResponse } from '$lib/api/client';
import { toMcpAuthConfig, type AuthDraft } from '$lib/auth/draft';

// `needs_authorization` is specific to oauth_authorization_code servers: the
// admin has to press Authorize (again) — a different call to action than a
// server that is down.
export type McpStatus = 'unknown' | 'healthy' | 'degraded' | 'unreachable' | 'needs_authorization';

export const MCP_AUTH_TYPES = [
	'none',
	'api_key',
	'bearer',
	'basic',
	'headers',
	'oauth_client_credentials',
	'oauth_authorization_code'
] as const;

export type McpServer = {
	id: string;
	name: string;
	description: string | null;
	url: string;
	transport: string;
	authType: string;
	hasCredentials: boolean;
	authCredentialId: string | null;
	authCredentialName: string | null;
	tlsSkipVerify: boolean;
	allowPrivateNetwork: boolean;
	// Lets this server's readOnlyHint annotations skip the confirmation gate. Off by
	// default: the server is the party that gains from calling a destructive tool safe.
	trustToolHints: boolean;
	status: McpStatus;
	lastCheckError: string | null;
	lastCheckedAt: string | null;
	toolsSyncedAt: string | null;
	toolCount: number;
	enabled: boolean;
	createdAt: string;
	updatedAt: string;
};

export interface McpServerPayload {
	name: string;
	description: string;
	url: string;

	// The form edits a scheme plus typed fields; the JSON object the backend wants is
	// built here.
	authType: string;
	authDraft: AuthDraft;

	// '' means "no stored credential" — the material comes from authDraft instead.
	authCredentialId: string;

	// Escape hatch for the advanced JSON editor.
	authConfigOverride?: string | null;

	clearAuthConfig: boolean;
	headers: string;
	tlsSkipVerify: boolean;
	allowPrivateNetwork: boolean;
	trustToolHints: boolean;
	enabled: boolean;
}

export type McpTool = {
	id: string;
	serverId: string;
	name: string;
	title: string | null;
	description: string | null;
	inputSchema: unknown;
	// The server's own claim that this tool changes nothing. A hint, shown so an admin
	// can see what they would be trusting.
	readOnlyHint: boolean;
	disappearedAt: string | null;
	enabled: boolean;
	updatedAt: string;
};

export type McpSync = {
	discovered: number;
	created: number;
	updated: number;
	disappeared: number;
	reappeared: number;
};

export type McpHealth = {
	status: McpStatus;
	elapsedMs: number;
	error: string | null;
	toolCount: number;
};

interface ServerShape {
	mcp_server_id: string;
	name: string;
	description: string | null;
	url: string;
	transport: string;
	auth_type: string;
	has_credentials: boolean;
	auth_credential_id: string | null;
	auth_credential_name: string | null;
	tls_skip_verify: boolean;
	allow_private_network: boolean;
	trust_tool_hints?: boolean;
	status: McpStatus;
	last_check_error: string | null;
	last_checked_at: string | null;
	tools_synced_at: string | null;
	tool_count: number;
	enabled: boolean;
	created_at: string;
	updated_at: string;
}

interface ToolShape {
	mcp_tool_id: string;
	mcp_server_id: string;
	name: string;
	title: string | null;
	description: string | null;
	input_schema: unknown;
	read_only_hint?: boolean;
	disappeared_at: string | null;
	enabled: boolean;
	updated_at: string;
}

function toServer(s: ServerShape): McpServer {
	return {
		id: s.mcp_server_id,
		name: s.name,
		description: s.description,
		url: s.url,
		transport: s.transport,
		authType: s.auth_type,
		hasCredentials: s.has_credentials,
		authCredentialId: s.auth_credential_id ?? null,
		authCredentialName: s.auth_credential_name ?? null,
		tlsSkipVerify: s.tls_skip_verify,
		allowPrivateNetwork: s.allow_private_network,
		trustToolHints: s.trust_tool_hints ?? false,
		status: s.status,
		lastCheckError: s.last_check_error,
		lastCheckedAt: s.last_checked_at,
		toolsSyncedAt: s.tools_synced_at,
		toolCount: s.tool_count,
		enabled: s.enabled,
		createdAt: s.created_at,
		updatedAt: s.updated_at
	};
}

function toTool(t: ToolShape): McpTool {
	return {
		id: t.mcp_tool_id,
		serverId: t.mcp_server_id,
		name: t.name,
		title: t.title,
		description: t.description,
		inputSchema: t.input_schema,
		readOnlyHint: t.read_only_hint ?? false,
		disappearedAt: t.disappeared_at,
		enabled: t.enabled,
		updatedAt: t.updated_at
	};
}

// Throws on malformed JSON so the caller can show a field error rather than
// letting the API reject a body the user cannot see.
function parseAuthConfig(raw: string): unknown | null {
	const text = raw.trim();
	if (!text) return null;
	const parsed = JSON.parse(text);
	if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
		throw new SyntaxError('auth_config must be a JSON object');
	}
	return parsed;
}

function toBody(p: McpServerPayload, isUpdate: boolean) {
	const body: Record<string, unknown> = {
		name: p.name,
		description: p.description || null,
		url: p.url,
		auth_type: p.authType,
		// Explicit null detaches the credential; the backend tells that apart from an
		// absent field.
		auth_credential_id: p.authCredentialId || null,
		headers: p.headers.trim() || null,
		tls_skip_verify: p.tlsSkipVerify,
		allow_private_network: p.allowPrivateNetwork,
		trust_tool_hints: p.trustToolHints,
		enabled: p.enabled
	};

	const auth =
		p.authConfigOverride !== undefined && p.authConfigOverride !== null
			? parseAuthConfig(p.authConfigOverride)
			: (toMcpAuthConfig(p.authType, p.authDraft, {
					editing: isUpdate,
					usingStoredCredential: !!p.authCredentialId
				}) ?? null);

	// An absent auth_config means "keep what is stored" — the API never returns the
	// material, so the form has nothing to resubmit.
	if (auth !== null) body.auth_config = auth;
	// Only meaningful on update: on create there is nothing stored to clear.
	if (isUpdate && p.clearAuthConfig) body.clear_auth_config = true;
	return body;
}

export async function listMcpServers(limit = 100, offset = 0): Promise<ListResponse<McpServer>> {
	const res = await api<ListResponse<ServerShape>>(`/mcp/servers?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toServer) };
}

export async function createMcpServer(p: McpServerPayload): Promise<McpServer> {
	return toServer(
		await api<ServerShape>('/mcp/servers', { method: 'POST', body: JSON.stringify(toBody(p, false)) })
	);
}

export async function updateMcpServer(id: string, p: McpServerPayload): Promise<McpServer> {
	return toServer(
		await api<ServerShape>(`/mcp/servers/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p, true))
		})
	);
}

export async function deleteMcpServer(id: string): Promise<void> {
	await api<ServerShape>(`/mcp/servers/${id}`, { method: 'DELETE' });
}

// Begin the OAuth authorization-code flow. The returned URL is where the
// browser must go to consent; the backend's callback completes the exchange and
// bounces back to /admin/mcp with ?mcp_authorized= or ?mcp_error=.
export async function oauthStartMcpServer(id: string): Promise<string> {
	const r = await api<{ authorization_url: string }>(`/mcp/servers/${id}/oauth/start`, {
		method: 'POST'
	});
	return r.authorization_url;
}

export async function syncMcpTools(id: string): Promise<McpSync> {
	const r = await api<{
		discovered: number;
		created: number;
		updated: number;
		disappeared: number;
		reappeared: number;
	}>(`/mcp/servers/${id}/sync`, { method: 'POST' });
	return {
		discovered: r.discovered,
		created: r.created,
		updated: r.updated,
		disappeared: r.disappeared,
		reappeared: r.reappeared
	};
}

export async function checkMcpServer(id: string): Promise<McpHealth> {
	const r = await api<{
		status: McpStatus;
		elapsed_ms: number;
		error: string | null;
		tool_count: number;
	}>(`/mcp/servers/${id}/check`, { method: 'POST' });
	return { status: r.status, elapsedMs: r.elapsed_ms, error: r.error, toolCount: r.tool_count };
}

export async function listMcpTools(
	serverId: string,
	includeRetired = false
): Promise<ListResponse<McpTool>> {
	const res = await api<ListResponse<ToolShape>>(
		`/mcp/servers/${serverId}/tools?includeRetired=${includeRetired}&limit=200`
	);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toTool) };
}

export async function setMcpToolEnabled(
	serverId: string,
	toolId: string,
	enabled: boolean
): Promise<McpTool> {
	return toTool(
		await api<ToolShape>(`/mcp/servers/${serverId}/tools/${toolId}`, {
			method: 'PUT',
			body: JSON.stringify({ enabled })
		})
	);
}
