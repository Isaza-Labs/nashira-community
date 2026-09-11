// Audit trail (`/api/audit`, Admin). The trail itself is append-only and
// hash-chained; /verify recomputes the chain to prove it hasn't been tampered
// with. The one write — restoreFromAudit — undoes the soft delete an event points
// at, and appends its own `restore` event to the trail.

import { api, type ListResponse } from '$lib/api/client';

export type AuditEvent = {
	id: string;
	sequence: number;
	at: string;
	userId: string | null;
	// Resolved server-side. A trail whose actor column is a GUID gets read once.
	username: string | null;
	entityType: string;
	entityId: string | null;
	action: string;
	// Who acted, as text. The username for a signed-in change; the automation identity
	// ("workflow-runner", "scheduler") when there is no user at all — on those rows it is
	// the only answer to "who did this".
	actor: string | null;
	ip: string | null;
	requestId: string | null;
	hash: string;
	// Which canonical form the hash was taken over. v1 predates the actor field.
	hashVersion: number;
};

export type AuditEventDetail = AuditEvent & {
	prevHash: string | null;
	userAgent: string | null;
	before: unknown;
	after: unknown;
	// True on a delete event whose record the server knows how to bring back.
	restorable: boolean;
};

export interface AuditRestoreResult {
	entityType: string;
	entityId: string;
	name: string | null;
}

export interface AuditVerify {
	valid: boolean;
	count: number;
	brokenAtSequence: number | null;
	reason: string | null;
}

interface SummaryShape {
	audit_event_id: string;
	sequence: number;
	at: string;
	user_id: string | null;
	username: string | null;
	entity_type: string;
	entity_id: string | null;
	action: string;
	actor: string | null;
	ip: string | null;
	request_id: string | null;
	hash: string;
	hash_version: number;
}

interface DetailShape extends SummaryShape {
	prev_hash: string | null;
	user_agent: string | null;
	before: unknown;
	after: unknown;
	restorable: boolean;
}

function toEvent(a: SummaryShape): AuditEvent {
	return {
		id: a.audit_event_id,
		sequence: a.sequence,
		at: a.at,
		userId: a.user_id,
		username: a.username ?? null,
		entityType: a.entity_type,
		entityId: a.entity_id,
		action: a.action,
		actor: a.actor ?? null,
		ip: a.ip,
		requestId: a.request_id,
		hash: a.hash,
		hashVersion: a.hash_version
	};
}

export interface AuditFilter {
	entityType?: string;
	/** A category: several entity types at once, which is what the quick filters are. */
	entityTypes?: string[];
	entityId?: string;
	action?: string;
	/** Matches a family of verbs — `credential` catches create, update and delete. */
	actionPrefix?: string;
	/** Joins to trace_events, which stores the same id. */
	requestId?: string;
	userId?: string;
	/** Text actor, including the automation identities that have no user id. */
	actor?: string;
	from?: string;
	to?: string;
	limit?: number;
	offset?: number;
}

export async function listAudit(f: AuditFilter = {}): Promise<ListResponse<AuditEvent>> {
	const qs = new URLSearchParams({ limit: String(f.limit ?? 50), offset: String(f.offset ?? 0) });
	if (f.entityType) qs.set('entityType', f.entityType);
	if (f.entityTypes?.length) qs.set('entityTypes', f.entityTypes.join(','));
	if (f.entityId) qs.set('entityId', f.entityId);
	if (f.action) qs.set('action', f.action);
	if (f.actionPrefix) qs.set('actionPrefix', f.actionPrefix);
	if (f.requestId) qs.set('requestId', f.requestId);
	if (f.userId) qs.set('userId', f.userId);
	if (f.actor) qs.set('actor', f.actor);
	if (f.from) qs.set('from', f.from);
	if (f.to) qs.set('to', f.to);
	const res = await api<ListResponse<SummaryShape>>(`/audit?${qs.toString()}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toEvent) };
}

// ── The authentication trail ──────────────────────────────────────────────
//
// A separate table and a separate endpoint, because it answers a different question:
// an audit row is about an entity that changed, while the rows that matter most here
// changed nothing at all. A failed sign-in has no entity and no diff.

export type AuthEventKind =
	| 'login_success'
	| 'login_failure'
	| 'logout'
	| 'password_change'
	| 'lockout'
	| 'refresh'
	| 'token_revoked';

export const AUTH_EVENT_KINDS: AuthEventKind[] = [
	'login_success',
	'login_failure',
	'logout',
	'password_change',
	'lockout',
	'refresh',
	'token_revoked'
];

export type AuthEvent = {
	id: string;
	at: string;
	userId: string | null;
	/** Null on an unattributed attempt — a sign-in for a username that does not exist. */
	username: string | null;
	event: string;
	ip: string;
	userAgent: string;
	/** Why it failed, which username was attempted, how long a lockout lasts. */
	metadata: unknown;
};

interface AuthEventShape {
	auth_event_id: string;
	at: string;
	user_id: string | null;
	username: string | null;
	event: string;
	ip: string;
	user_agent: string;
	metadata: unknown;
}

export interface AuthEventFilter {
	userId?: string;
	event?: string;
	from?: string;
	to?: string;
	/** Attempts that resolve to no account. Off by default — see the endpoint. */
	includeUnattributed?: boolean;
	limit?: number;
	offset?: number;
}

export async function listAuthEvents(f: AuthEventFilter = {}): Promise<ListResponse<AuthEvent>> {
	const qs = new URLSearchParams({ limit: String(f.limit ?? 50), offset: String(f.offset ?? 0) });
	if (f.userId) qs.set('userId', f.userId);
	if (f.event) qs.set('event', f.event);
	if (f.from) qs.set('from', f.from);
	if (f.to) qs.set('to', f.to);
	if (f.includeUnattributed) qs.set('includeUnattributed', 'true');
	const res = await api<ListResponse<AuthEventShape>>(`/auth/events?${qs.toString()}`);
	return {
		total: res.total,
		limit: res.limit,
		offset: res.offset,
		items: res.items.map((e) => ({
			id: e.auth_event_id,
			at: e.at,
			userId: e.user_id,
			username: e.username ?? null,
			event: e.event,
			ip: e.ip,
			userAgent: e.user_agent,
			metadata: e.metadata
		}))
	};
}

export async function getAuditEvent(id: string): Promise<AuditEventDetail> {
	const a = await api<DetailShape>(`/audit/${id}`);
	return {
		...toEvent(a),
		prevHash: a.prev_hash,
		userAgent: a.user_agent,
		before: a.before,
		after: a.after,
		restorable: a.restorable ?? false
	};
}

// Undo the soft delete the given delete event recorded. The server appends a
// `restore` audit event attributed to the signed-in admin.
export async function restoreFromAudit(auditEventId: string): Promise<AuditRestoreResult> {
	const r = await api<{ entity_type: string; entity_id: string; name: string | null }>(
		`/audit/${auditEventId}/restore`,
		{ method: 'POST' }
	);
	return { entityType: r.entity_type, entityId: r.entity_id, name: r.name };
}

export async function verifyAudit(): Promise<AuditVerify> {
	const r = await api<{ valid: boolean; count: number; broken_at_sequence: number | null; reason: string | null }>(
		'/audit/verify'
	);
	return { valid: r.valid, count: r.count, brokenAtSequence: r.broken_at_sequence, reason: r.reason };
}
