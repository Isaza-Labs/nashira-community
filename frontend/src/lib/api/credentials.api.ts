// Credential CRUD (`/api/credential`, Admin). Secret material (password, private
// key, token, client secret) is write-only — never returned, only has_* flags.
// auth_method: password | key | token | api_key | oauth2.
//
// allowed_hosts / operator_assignable: an Admin may limit the hosts device SSH sends
// a credential to and, once limited, let Operators link it. Operators read only
// `/api/credential/assignable` (listAssignableCredentials).

import { api, type ListResponse } from '$lib/api/client';

export type AuthMethod = 'password' | 'key' | 'token' | 'api_key' | 'oauth2';

export const AUTH_METHODS: { value: AuthMethod; label: string }[] = [
	{ value: 'password', label: 'Password' },
	{ value: 'key', label: 'SSH private key' },
	{ value: 'token', label: 'Token (bearer / PAT)' },
	{ value: 'api_key', label: 'API key' },
	{ value: 'oauth2', label: 'OAuth2 (client credentials)' }
];

export type Credential = {
	id: string;
	name: string;
	type: string;
	username: string | null;
	authMethod: string;
	hasPassword: boolean;
	hasPrivateKey: boolean;
	hasToken: boolean;
	hasClientSecret: boolean;
	apiKeyHeader: string | null;
	clientId: string | null;
	tokenUrl: string | null;
	scopes: string | null;
	/** One entry per line (IP, CIDR network or exact hostname); null = not restricted. */
	allowedHosts: string | null;
	operatorAssignable: boolean;
	createdAt: string;
	updatedAt: string;
};

/** What a non-Admin may know about a credential it can link to a device. */
export type AssignableCredential = {
	id: string;
	name: string;
	authMethod: string;
	allowedHosts: string;
};

export interface CredentialPayload {
	name: string;
	type: string;
	username: string;
	authMethod: string;
	password: string;
	privateKey: string;
	keyPassphrase: string;
	token: string;
	apiKeyHeader: string;
	clientId: string;
	clientSecret: string;
	tokenUrl: string;
	scopes: string;
	/** Newline/comma separated; blank = not restricted (clears it on update). */
	allowedHosts: string;
	operatorAssignable: boolean;
}

interface CredShape {
	credential_id: string;
	name: string;
	type: string;
	username: string | null;
	auth_method: string;
	has_password: boolean;
	has_private_key: boolean;
	has_token: boolean;
	has_client_secret: boolean;
	api_key_header: string | null;
	client_id: string | null;
	token_url: string | null;
	scopes: string | null;
	allowed_hosts: string | null;
	operator_assignable: boolean;
	created_at: string;
	updated_at: string;
}

interface AssignableShape {
	credential_id: string;
	name: string;
	auth_method: string;
	allowed_hosts: string;
}

function toCredential(c: CredShape): Credential {
	return {
		id: c.credential_id,
		name: c.name,
		type: c.type,
		username: c.username,
		authMethod: c.auth_method,
		hasPassword: c.has_password,
		hasPrivateKey: c.has_private_key,
		hasToken: c.has_token,
		hasClientSecret: c.has_client_secret,
		apiKeyHeader: c.api_key_header,
		clientId: c.client_id,
		tokenUrl: c.token_url,
		scopes: c.scopes,
		allowedHosts: c.allowed_hosts ?? null,
		operatorAssignable: !!c.operator_assignable,
		createdAt: c.created_at,
		updatedAt: c.updated_at
	};
}

export async function listCredentials(limit = 100, offset = 0): Promise<ListResponse<Credential>> {
	const res = await api<ListResponse<CredShape>>(`/credential?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toCredential) };
}

// Active credentials an Admin made assignable and scoped: the device form's picker
// for Operators. Id, name, method and scope only.
export async function listAssignableCredentials(): Promise<AssignableCredential[]> {
	const rows = await api<AssignableShape[]>('/credential/assignable');
	return rows.map((c) => ({
		id: c.credential_id,
		name: c.name,
		authMethod: c.auth_method,
		allowedHosts: c.allowed_hosts
	}));
}

export async function createCredential(p: CredentialPayload): Promise<Credential> {
	return toCredential(
		await api<CredShape>('/credential', {
			method: 'POST',
			body: JSON.stringify({
				name: p.name,
				type: p.type,
				username: p.username || null,
				auth_method: p.authMethod || null,
				password: p.password || null,
				private_key: p.privateKey || null,
				key_passphrase: p.keyPassphrase || null,
				token: p.token || null,
				api_key_header: p.apiKeyHeader || null,
				client_id: p.clientId || null,
				client_secret: p.clientSecret || null,
				token_url: p.tokenUrl || null,
				scopes: p.scopes || null,
				allowed_hosts: p.allowedHosts || null,
				operator_assignable: p.operatorAssignable
			})
		})
	);
}

export async function updateCredential(id: string, p: CredentialPayload): Promise<Credential> {
	const body: Record<string, unknown> = {
		name: p.name,
		type: p.type,
		username: p.username || null,
		auth_method: p.authMethod || null,
		// Non-secret fields are WYSIWYG: an empty string clears the stored value
		// (null would mean "keep", which surprises when a field is blanked).
		api_key_header: p.apiKeyHeader,
		client_id: p.clientId,
		token_url: p.tokenUrl,
		scopes: p.scopes,
		// Same WYSIWYG rule: a blank scope clears it (the server then refuses
		// operator_assignable without one, so the form unticks it first).
		allowed_hosts: p.allowedHosts,
		operator_assignable: p.operatorAssignable
	};
	// Secret fields: only sent when provided; blank keeps what's stored.
	if (p.password) body.password = p.password;
	if (p.privateKey) body.private_key = p.privateKey;
	if (p.keyPassphrase) body.key_passphrase = p.keyPassphrase;
	if (p.token) body.token = p.token;
	if (p.clientSecret) body.client_secret = p.clientSecret;
	return toCredential(await api<CredShape>(`/credential/${id}`, { method: 'PUT', body: JSON.stringify(body) }));
}

export async function deleteCredential(id: string): Promise<void> {
	await api<CredShape>(`/credential/${id}`, { method: 'DELETE' });
}
