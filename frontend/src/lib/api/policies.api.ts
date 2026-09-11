// Governance policies (`/api/policies`, Admin). Default-allow, opt-in-deny.
//
// Two shapes: `deny` is evaluated before a run executes; `gate` is evaluated
// before a promotion, against the workflow's run history. A deny asks "is this
// forbidden?"; a gate asks "has this earned it yet?".

import { api, type ListResponse } from '$lib/api/client';

export const POLICY_ACTIONS = ['deny', 'gate'] as const;

export type Policy = {
	id: string;
	name: string;
	description: string | null;
	rule: unknown;
	enabled: boolean;
	createdAt: string;
	updatedAt: string;
};

export interface PolicyPayload {
	name: string;
	description: string;
	rule: string;
	enabled: boolean;
}

export type PolicyDecision = {
	denied: boolean;
	policy: string | null;
	reason: string | null;
};

export interface EvaluateRequest {
	// Omit to evaluate the stored, enabled policies; provide one to dry-run a rule
	// that is not saved yet.
	rule?: string;
	environment: string;
	workflowDescription: string;
	deviceRoles: string[];
	devicePools: string[];
	snippetTypes: string[];
}

interface PolicyShape {
	policy_id: string;
	name: string;
	description: string | null;
	rule: unknown;
	enabled: boolean;
	created_at: string;
	updated_at: string;
}

function toPolicy(p: PolicyShape): Policy {
	return {
		id: p.policy_id,
		name: p.name,
		description: p.description,
		rule: p.rule,
		enabled: p.enabled,
		createdAt: p.created_at,
		updatedAt: p.updated_at
	};
}

// Throws on malformed JSON so the form can show a field error.
function parseRule(raw: string): unknown {
	const parsed = JSON.parse(raw.trim() || '{}');
	if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
		throw new SyntaxError('the rule must be a JSON object');
	}
	return parsed;
}

export async function listPolicies(limit = 100): Promise<ListResponse<Policy>> {
	const res = await api<ListResponse<PolicyShape>>(`/policies?limit=${limit}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toPolicy) };
}

export async function createPolicy(p: PolicyPayload): Promise<Policy> {
	return toPolicy(
		await api<PolicyShape>('/policies', {
			method: 'POST',
			body: JSON.stringify({
				name: p.name,
				description: p.description || null,
				rule: parseRule(p.rule),
				enabled: p.enabled
			})
		})
	);
}

export async function updatePolicy(id: string, p: PolicyPayload): Promise<Policy> {
	return toPolicy(
		await api<PolicyShape>(`/policies/${id}`, {
			method: 'PUT',
			body: JSON.stringify({
				name: p.name,
				// "" rather than null when the field was cleared. The API skips null
				// fields so a partial update can touch one thing (see below), which means
				// a null here would silently leave the old description in place.
				description: p.description,
				rule: parseRule(p.rule),
				enabled: p.enabled
			})
		})
	);
}

// Arm or disarm without opening the editor. Sends only `enabled`, so it cannot
// carry a half-finished rule from the form along with it.
export async function setPolicyEnabled(id: string, enabled: boolean): Promise<Policy> {
	return toPolicy(
		await api<PolicyShape>(`/policies/${id}`, {
			method: 'PUT',
			body: JSON.stringify({ enabled })
		})
	);
}

export async function deletePolicy(id: string): Promise<void> {
	await api<PolicyShape>(`/policies/${id}`, { method: 'DELETE' });
}

// Dry run. A guardrail first exercised in production is a guardrail nobody has
// read carefully.
export async function evaluatePolicy(req: EvaluateRequest): Promise<PolicyDecision> {
	const r = await api<{ denied: boolean; policy: string | null; reason: string | null }>(
		'/policies/evaluate',
		{
			method: 'POST',
			body: JSON.stringify({
				rule: req.rule ? parseRule(req.rule) : undefined,
				environment: req.environment || null,
				workflow_description: req.workflowDescription || null,
				device_roles: req.deviceRoles,
				device_pools: req.devicePools,
				snippet_types: req.snippetTypes
			})
		}
	);
	return { denied: r.denied, policy: r.policy, reason: r.reason };
}
