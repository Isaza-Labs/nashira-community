// One editable shape behind every authentication form, plus the serializers that turn
// it into what each backend expects.
//
// There is no single auth format in Nashira. Integrations nest a `method` inside a JSON
// *string*; MCP servers put `auth_type` beside a JSON *object* and spell the same
// concepts differently (`api_key`/`api_key_header` where an integration says
// `token`/`header`). The draft below is the superset the UI edits; each serializer picks
// the fields its dialect actually uses, so a stray value from a previously-selected
// method never reaches the wire.

export type AuthDialect = 'integration' | 'mcp';

export interface AuthDraft {
	token: string;
	username: string;
	password: string;
	prefix: string;
	header: string;
	tokenUrl: string;
	clientId: string;
	clientSecret: string;
	scope: string;
	secretHeaders: string;
	// oauth_authorization_code (MCP only). Both optional: discovery (RFC 8414) can
	// resolve the endpoints and the backend supplies its own callback as redirect.
	authorizationEndpoint: string;
	redirectUri: string;
}

export function emptyDraft(): AuthDraft {
	return {
		token: '',
		username: '',
		password: '',
		prefix: '',
		header: '',
		tokenUrl: '',
		clientId: '',
		clientSecret: '',
		scope: '',
		secretHeaders: '',
		authorizationEndpoint: '',
		redirectUri: ''
	};
}

export interface AuthMethodOption {
	value: string;
	label: string;
	hint?: string;
}

// Integration methods. Note the spelling: `oauth2_client_credentials` here,
// `oauth_client_credentials` on MCP, `oauth2` on a stored credential. All three differ.
// Both `bearer` and `token` carry a token, so naming one of them "Bearer token" and
// the other "a custom scheme" sorts them by how exotic they sound rather than by what
// they do. Somebody holding a NetBox API token reads "Bearer token" as the obvious
// match, picks it, and NetBox answers "authentication credentials were not provided" —
// it only recognises the word `Token`, so an unknown scheme reads to it as no
// credential at all. The scheme is the only difference between these two, so the label
// leads with the scheme and names who uses it.
export const INTEGRATION_AUTH_METHODS: AuthMethodOption[] = [
	{ value: 'none', label: 'No authentication' },
	{
		value: 'token',
		label: 'Token — Authorization: Token <token>',
		hint: 'NetBox and other Django REST Framework APIs. The scheme word can be changed.'
	},
	{
		value: 'bearer',
		label: 'Bearer — Authorization: Bearer <token>',
		hint: 'The OAuth-style scheme. Most modern APIs.'
	},
	{ value: 'basic', label: 'Username and password', hint: 'HTTP Basic' },
	{ value: 'api_key', label: 'API key in a header', hint: 'A custom header carries the key' },
	{ value: 'oauth2_client_credentials', label: 'OAuth2 (client credentials)', hint: 'Nashira fetches and refreshes the token' }
];

export const MCP_AUTH_METHODS: AuthMethodOption[] = [
	{ value: 'none', label: 'No authentication' },
	{ value: 'bearer', label: 'Bearer token', hint: 'Authorization: Bearer <token>' },
	{ value: 'basic', label: 'Username and password', hint: 'HTTP Basic' },
	{ value: 'api_key', label: 'API key in a header', hint: 'A custom header carries the key' },
	{ value: 'headers', label: 'Custom secret headers', hint: 'For servers whose scheme is none of the above' },
	{ value: 'oauth_client_credentials', label: 'OAuth2 (client credentials)', hint: 'Nashira fetches and refreshes the token' },
	{
		value: 'oauth_authorization_code',
		label: 'OAuth2 (authorization code)',
		hint: 'Browser consent: save, then press Authorize on the server row. PKCE, endpoint discovery and dynamic client registration are handled for you.'
	}
];

export function authMethods(dialect: AuthDialect): AuthMethodOption[] {
	return dialect === 'mcp' ? MCP_AUTH_METHODS : INTEGRATION_AUTH_METHODS;
}

// Which stored credential kinds can authenticate an HTTP request. `key` is an SSH
// private key: there is no scheme that carries one, and the backend refuses it.
export const HTTP_CREDENTIAL_METHODS = ['password', 'token', 'api_key', 'oauth2'];

// The method a stored credential implies, so picking one can preselect the scheme.
export function methodForCredential(dialect: AuthDialect, credentialAuthMethod: string): string {
	switch (credentialAuthMethod) {
		case 'password':
			return 'basic';
		case 'token':
			return 'bearer';
		case 'api_key':
			return 'api_key';
		case 'oauth2':
			return dialect === 'mcp' ? 'oauth_client_credentials' : 'oauth2_client_credentials';
		default:
			return 'none';
	}
}

// Which draft fields a method uses. Drives both the rendered inputs and the serializers,
// so the two can never disagree about what belongs to a method.
export function fieldsFor(dialect: AuthDialect, method: string): (keyof AuthDraft)[] {
	switch (method) {
		case 'bearer':
			return ['token'];
		case 'token':
			return ['token', 'prefix'];
		case 'basic':
			return ['username', 'password'];
		case 'api_key':
			return ['token', 'header'];
		case 'headers':
			return ['secretHeaders'];
		case 'oauth2_client_credentials':
		case 'oauth_client_credentials':
			return ['tokenUrl', 'clientId', 'clientSecret', 'scope'];
		case 'oauth_authorization_code':
			return ['clientId', 'clientSecret', 'tokenUrl', 'authorizationEndpoint', 'redirectUri', 'scope'];
		default:
			return [];
	}
}

const SECRET_FIELDS: (keyof AuthDraft)[] = ['token', 'password', 'clientSecret', 'secretHeaders'];

export function isSecretField(field: keyof AuthDraft): boolean {
	return SECRET_FIELDS.includes(field);
}

// True when the user typed nothing the backend would treat as credentials. On edit that
// means "keep what is stored", because the API never returns the material to resubmit.
export function draftIsEmpty(dialect: AuthDialect, method: string, draft: AuthDraft): boolean {
	return fieldsFor(dialect, method).every((f) => !draft[f].trim());
}

// Mirrors the server's own required-field rules so the failure is named here instead of
// arriving as a 400 — or worse, as an anonymous request the upstream answers with 401.
export function missingMaterial(
	dialect: AuthDialect,
	method: string,
	draft: AuthDraft,
	usingStoredCredential: boolean,
	editing: boolean
): string | null {
	// A stored credential supplies the material; nothing has to be typed.
	if (usingStoredCredential || method === 'none') return null;
	// Blank on edit means "keep the stored credentials".
	if (editing && draftIsEmpty(dialect, method, draft)) return null;

	switch (method) {
		case 'bearer':
		case 'token':
			return draft.token.trim() ? null : 'A token is required.';
		case 'basic':
			return draft.username.trim() ? null : 'A username is required.';
		case 'api_key':
			return draft.token.trim() ? null : 'An API key is required.';
		case 'headers':
			return draft.secretHeaders.trim() ? null : 'At least one header is required.';
		case 'oauth2_client_credentials':
		case 'oauth_client_credentials':
			if (!draft.tokenUrl.trim()) return 'A token URL is required.';
			if (!draft.clientId.trim()) return 'A client ID is required.';
			if (!draft.clientSecret.trim() && !editing) return 'A client secret is required.';
			return null;
		// Everything is optional: endpoints can be discovered (RFC 8414 / 9728) and a
		// missing client_id goes through dynamic client registration at Authorize time.
		case 'oauth_authorization_code':
			return null;
		default:
			return null;
	}
}

// `X-Foo: bar` per line — the only free-form map in any of the dialects, and far kinder
// to type than the JSON object it becomes.
export function parseHeaderLines(text: string): Record<string, string> {
	const out: Record<string, string> = {};
	for (const line of text.split('\n')) {
		const trimmed = line.trim();
		if (!trimmed) continue;
		const colon = trimmed.indexOf(':');
		if (colon <= 0) continue;
		out[trimmed.slice(0, colon).trim()] = trimmed.slice(colon + 1).trim();
	}
	return out;
}

function compact(obj: Record<string, unknown>): Record<string, unknown> {
	return Object.fromEntries(Object.entries(obj).filter(([, v]) => v !== '' && v != null));
}

// Integration: a JSON *string* with the method nested inside it.
// Returns null to mean "leave the stored credentials alone" — the backend treats an
// absent auth_config as unchanged, which is the only way to edit an integration without
// retyping secrets the API will never show again.
export function toIntegrationAuthConfig(
	method: string,
	draft: AuthDraft,
	opts: { editing: boolean; usingStoredCredential: boolean }
): string | null {
	if (method === 'none') return JSON.stringify({ method: 'none' });

	// With a stored credential the config carries only the shape; the material comes
	// from the credential row at request time.
	const shapeOnly = opts.usingStoredCredential;
	if (!shapeOnly && opts.editing && draftIsEmpty('integration', method, draft)) return null;

	const cfg: Record<string, unknown> = { method };
	for (const field of fieldsFor('integration', method)) {
		if (shapeOnly && isSecretField(field)) continue;
		const value = draft[field].trim();
		if (!value) continue;
		switch (field) {
			case 'token':
				cfg.token = value;
				break;
			case 'username':
				cfg.username = value;
				break;
			case 'password':
				cfg.password = value;
				break;
			case 'prefix':
				cfg.prefix = value;
				break;
			case 'header':
				cfg.header = value;
				break;
			case 'tokenUrl':
				cfg.token_url = value;
				break;
			case 'clientId':
				cfg.client_id = value;
				break;
			case 'clientSecret':
				cfg.client_secret = value;
				break;
			case 'scope':
				cfg.scope = value;
				break;
		}
	}
	return JSON.stringify(compact(cfg));
}

// MCP: a JSON *object* beside a top-level auth_type, with its own field names.
// Returns undefined to mean "unchanged" (the backend's absent-vs-clear_auth_config split).
export function toMcpAuthConfig(
	method: string,
	draft: AuthDraft,
	opts: { editing: boolean; usingStoredCredential: boolean }
): Record<string, unknown> | undefined {
	if (method === 'none') return undefined;

	const shapeOnly = opts.usingStoredCredential;
	if (!shapeOnly && opts.editing && draftIsEmpty('mcp', method, draft)) return undefined;

	const cfg: Record<string, unknown> = {};
	for (const field of fieldsFor('mcp', method)) {
		if (shapeOnly && isSecretField(field)) continue;
		const value = draft[field].trim();
		if (!value) continue;
		switch (field) {
			// MCP calls the key `api_key`; only the bearer scheme uses `token`.
			case 'token':
				if (method === 'api_key') cfg.api_key = value;
				else cfg.token = value;
				break;
			case 'header':
				cfg.api_key_header = value;
				break;
			case 'username':
				cfg.username = value;
				break;
			case 'password':
				cfg.password = value;
				break;
			case 'secretHeaders':
				cfg.secret_headers = parseHeaderLines(value);
				break;
			case 'tokenUrl':
				cfg.token_url = value;
				break;
			case 'clientId':
				cfg.client_id = value;
				break;
			case 'clientSecret':
				cfg.client_secret = value;
				break;
			case 'scope':
				cfg.scope = value;
				break;
			case 'authorizationEndpoint':
				cfg.authorization_endpoint = value;
				break;
			case 'redirectUri':
				cfg.redirect_uri = value;
				break;
		}
	}
	return Object.keys(cfg).length > 0 ? cfg : undefined;
}
