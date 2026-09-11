// nashira API client. The backend is mounted at /api (snake_case JSON, JWT bearer).
// In dev the Vite proxy forwards /api -> http://localhost:5280; in prod the SPA is
// served same-origin from the backend, so the relative path works unchanged.
//
// This is the auth-slice core lifted/adapted from flow-weaver: the fetch wrapper
// (bearer + rotating-refresh interceptor), typed errors, and the list envelope.
// Per-domain entity clients are added in their own slices. nashira surfaces errors
// as RFC 7807 problem+json, so the message is read from `detail`/`title`.

import { browser } from '$app/environment';
import { goto } from '$app/navigation';
import { authStore, sessionFromResponse, type LoginResponseShape } from '$lib/stores/auth.svelte';

const BASE = '/api';

// nashira list endpoints return this envelope.
export interface ListResponse<T> {
	items: T[];
	total: number;
	limit: number;
	offset: number;
}

export type ApiErrorKind =
	| 'network'
	| 'timeout'
	| 'cancelled'
	| 'unauthorized'
	| 'forbidden'
	| 'not_found'
	| 'conflict'
	| 'validation'
	| 'rate_limited'
	| 'server'
	| 'unknown';

export class ApiError extends Error {
	kind: ApiErrorKind;
	status: number;
	body: unknown;
	userMessage: string;

	constructor(opts: {
		kind: ApiErrorKind;
		status: number;
		message: string;
		userMessage: string;
		body?: unknown;
	}) {
		super(opts.message);
		this.name = 'ApiError';
		this.kind = opts.kind;
		this.status = opts.status;
		this.body = opts.body;
		this.userMessage = opts.userMessage;
	}
}

function userMessageFor(kind: ApiErrorKind, status: number): string {
	switch (kind) {
		case 'network':
			return 'Could not reach the server. Check your connection or try again in a few minutes.';
		case 'timeout':
			return 'The request took too long. Please try again.';
		case 'cancelled':
			return 'Request cancelled.';
		case 'unauthorized':
			return 'Your session has expired. Please sign in again.';
		case 'forbidden':
			return 'You don’t have permission to perform this action.';
		case 'not_found':
			return 'We couldn’t find what you’re looking for. It may have been deleted.';
		case 'conflict':
			return 'There’s a conflict with the current state. Refresh the page and try again.';
		case 'validation':
			return 'The submitted data is invalid. Please review the form.';
		case 'rate_limited':
			return 'Too many requests. Wait a few seconds and try again.';
		case 'server':
			return 'The server had a problem processing your request. Please try again in a few minutes.';
		default:
			return `Something went wrong (code ${status || 'unknown'}). Please try again.`;
	}
}

// Flattens ASP.NET's `errors` map into one sentence. Field names are kept: `$.headers`
// or `password` is the difference between "the data is invalid" and knowing which box
// to go back to. Capped, because a bulk endpoint can reject fifty rows at once and a
// wall of text is read as no message at all.
function fieldErrors(errors: Record<string, string[] | string> | undefined): string | null {
	if (!errors || typeof errors !== 'object') return null;

	const parts: string[] = [];
	for (const [field, value] of Object.entries(errors)) {
		const messages = (Array.isArray(value) ? value : [value]).filter(
			(m): m is string => typeof m === 'string' && m.length > 0
		);
		if (messages.length === 0) continue;
		// A field named "" (or the JSON-path root) is about the body as a whole, so
		// naming it would be noise.
		const name = field.replace(/^\$\./, '').trim();
		parts.push(name && name !== '$' ? `${name}: ${messages.join(' ')}` : messages.join(' '));
	}

	if (parts.length === 0) return null;
	const shown = parts.slice(0, 3).join(' · ');
	return parts.length > 3 ? `${shown} (+${parts.length - 3} more)` : shown;
}

function kindForStatus(status: number): ApiErrorKind {
	if (status === 401) return 'unauthorized';
	if (status === 403) return 'forbidden';
	if (status === 404) return 'not_found';
	if (status === 409) return 'conflict';
	if (status === 400 || status === 422) return 'validation';
	if (status === 429) return 'rate_limited';
	if (status >= 500 && status <= 599) return 'server';
	return 'unknown';
}

const DEFAULT_TIMEOUT_MS = 30_000;

interface ApiOptions extends Omit<RequestInit, 'signal'> {
	signal?: AbortSignal;
	timeoutMs?: number;
}

// Endpoints that must bypass the auth interceptor: hitting them with a stale token
// and then refreshing inside would either loop or leak the refresh token.
const UNAUTHENTICATED_PATHS = ['/auth/login', '/auth/refresh', '/auth/bootstrap'];

function isUnauthenticatedPath(path: string): boolean {
	return UNAUTHENTICATED_PATHS.some((p) => path.startsWith(p));
}

// Single-flight refresh WITHIN a tab (also the fallback when Web Locks is
// unavailable): concurrent 401s share one round-trip.
let refreshInFlight: Promise<boolean> | null = null;

// A token with more than this much life left is "fresh enough". Inside the refresh
// lock we treat a token this fresh as "another tab already rotated it".
const REFRESH_FRESH_MARGIN_MS = 30_000;

export async function tryRefresh(): Promise<boolean> {
	// Cross-tab single-flight. All tabs share ONE rotating refresh token; if two
	// refresh at once the loser presents a just-rotated token, the backend flags it
	// as reuse and revokes the whole chain — logging every tab out. Web Locks
	// serializes the refresh to one tab; when unavailable we fall back to the
	// per-tab single-flight below.
	if (browser && typeof navigator !== 'undefined' && navigator.locks) {
		return navigator.locks.request('nashira:auth-refresh', () => refreshCore());
	}
	return refreshCore();
}

function refreshCore(): Promise<boolean> {
	refreshInFlight ??= (async () => {
		try {
			// We may have waited on the cross-tab lock while another tab rotated the
			// token. Re-read the persisted session; if its access token is still
			// fresh, adopt it and skip the network refresh.
			const persisted = authStore.syncFromStorage();
			if (persisted && persisted.expiresAt - Date.now() > REFRESH_FRESH_MARGIN_MS) {
				return true;
			}

			const token = persisted?.refreshToken;
			if (!token) return false;

			const res = await fetch(`${BASE}/auth/refresh`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ refresh_token: token })
			});
			if (!res.ok) return false;
			const body = (await res.json()) as LoginResponseShape;
			authStore.set(sessionFromResponse(body));
			return true;
		} catch {
			return false;
		} finally {
			refreshInFlight = null;
		}
	})();
	return refreshInFlight;
}

async function doFetch(path: string, options: ApiOptions, signal: AbortSignal): Promise<Response> {
	const headers: Record<string, string> = {
		'Content-Type': 'application/json',
		...(options.headers as Record<string, string> | undefined)
	};
	const token = authStore.accessToken;
	if (token && !isUnauthenticatedPath(path)) {
		headers['Authorization'] = `Bearer ${token}`;
	}
	return fetch(`${BASE}${path}`, { ...options, headers, signal });
}

// Core request: bearer + rotating-refresh interceptor, timeout/abort, and typed
// ApiError on !ok. Returns the raw Response so callers can decode JSON or text.
async function request(path: string, options?: ApiOptions): Promise<Response> {
	const { signal: userSignal, timeoutMs = DEFAULT_TIMEOUT_MS, ...rest } = options ?? {};

	const controller = new AbortController();
	const timeoutHandle = setTimeout(
		() => controller.abort(new DOMException('Timeout', 'TimeoutError')),
		timeoutMs
	);
	if (userSignal) {
		if (userSignal.aborted) controller.abort(userSignal.reason);
		else
			userSignal.addEventListener('abort', () => controller.abort(userSignal.reason), { once: true });
	}

	let res: Response;
	try {
		res = await doFetch(path, rest, controller.signal);

		// One retry on 401: refresh the token, then replay the request. Skip for
		// unauthenticated paths — they legitimately 401 on bad creds.
		if (res.status === 401 && !isUnauthenticatedPath(path) && authStore.refreshToken) {
			const refreshed = await tryRefresh();
			if (refreshed) {
				res = await doFetch(path, rest, controller.signal);
			} else if (browser) {
				// Refresh failed — session is dead. Clear + kick to login.
				authStore.clear();
				const current = window.location.pathname + window.location.search;
				if (!current.startsWith('/login')) {
					void goto(`/login?redirect=${encodeURIComponent(current)}`);
				}
			}
		}
	} catch (err) {
		if (err instanceof DOMException) {
			if (err.name === 'TimeoutError') {
				throw new ApiError({
					kind: 'timeout',
					status: 0,
					message: 'Request timed out',
					userMessage: userMessageFor('timeout', 0)
				});
			}
			if (err.name === 'AbortError') {
				const cancelledByUs = controller.signal.aborted && !userSignal?.aborted;
				const kind: ApiErrorKind = cancelledByUs ? 'timeout' : 'cancelled';
				throw new ApiError({
					kind,
					status: 0,
					message: err.message || 'Aborted',
					userMessage: userMessageFor(kind, 0)
				});
			}
		}
		throw new ApiError({
			kind: 'network',
			status: 0,
			message: err instanceof Error ? err.message : 'Network error',
			userMessage: userMessageFor('network', 0)
		});
	} finally {
		clearTimeout(timeoutHandle);
	}

	if (!res.ok) {
		const body = await res.json().catch(() => ({}));
		// nashira returns RFC 7807 problem+json: the actionable text is in `detail`
		// (or `title`); tolerate a bare `{ error }` too.
		const problem = body as {
			detail?: string;
			title?: string;
			error?: string;
			errors?: Record<string, string[] | string>;
		};
		// ASP.NET's own model-binding 400 carries no `detail` at all: the useful part
		// is the per-field `errors` map, under a `title` that only says "One or more
		// validation errors occurred." Reading just the title turns a precise
		// complaint about one field into a shrug, which is how a rejected form ends
		// up looking like a bug in the app.
		const serverMessage = problem.detail || problem.error || fieldErrors(problem.errors) || problem.title;
		const kind = kindForStatus(res.status);
		// Prefer the server's message for any 4xx (deliberately actionable); fall
		// back to the generic message for 5xx (may leak internals).
		const passThroughServerMessage =
			typeof serverMessage === 'string' &&
			serverMessage.length > 0 &&
			res.status >= 400 &&
			res.status < 500;
		throw new ApiError({
			kind,
			status: res.status,
			message: serverMessage || res.statusText || `HTTP ${res.status}`,
			userMessage: passThroughServerMessage ? serverMessage! : userMessageFor(kind, res.status),
			body
		});
	}
	return res;
}

export async function api<T>(path: string, options?: ApiOptions): Promise<T> {
	const res = await request(path, options);
	if (res.status === 204) return {} as T;
	return res.json();
}

// Text variant for non-JSON endpoints (e.g. the workflow YAML compiler output).
export async function apiText(path: string, options?: ApiOptions): Promise<string> {
	const res = await request(path, options);
	if (res.status === 204) return '';
	return res.text();
}

// Blob variant for authenticated binary downloads (e.g. export artifacts).
export async function apiBlob(path: string, options?: ApiOptions): Promise<Blob> {
	const res = await request(path, options);
	return res.blob();
}

// Same, but also recovers the name the server attached. Callers that reach a
// download through a link the agent wrote (chat) have no metadata of their own,
// so without this the file would be saved under a made-up name.
export async function apiFile(
	path: string,
	options?: ApiOptions
): Promise<{ blob: Blob; fileName: string | null }> {
	const res = await request(path, options);
	return {
		blob: await res.blob(),
		fileName: fileNameFromDisposition(res.headers.get('content-disposition'))
	};
}

// Content-Disposition is same-origin here, so the header is readable. RFC 5987's
// `filename*=UTF-8''…` wins when present — it is the one that survives accents.
function fileNameFromDisposition(header: string | null): string | null {
	if (!header) return null;

	const encoded = /filename\*\s*=\s*([^;]+)/i.exec(header);
	if (encoded) {
		const raw = encoded[1].trim().replace(/^utf-8''/i, '');
		try {
			return decodeURIComponent(raw) || null;
		} catch {
			return raw || null;
		}
	}

	const plain = /filename\s*=\s*(?:"([^"]*)"|([^;]*))/i.exec(header);
	const name = (plain?.[1] ?? plain?.[2] ?? '').trim();
	return name || null;
}

export function errorMessage(err: unknown): string {
	if (err instanceof ApiError) return err.userMessage;
	return 'Something went wrong. Please try again.';
}

export function isOfflineError(err: unknown): boolean {
	return err instanceof ApiError && (err.kind === 'network' || err.kind === 'timeout');
}
