// Prompt-skill CRUD (`/api/skills`, Admin). Skills compose the agent's system
// prompt; higher priority is included first.

import { api, type ListResponse } from '$lib/api/client';

export type Skill = {
	id: string;
	name: string;
	content: string;
	priority: number;
	// Null = global skill. When set, the agent treats the skill as scoped to that
	// integration's base URL and credentials.
	integrationId: string | null;
	// The admin who last wrote this row.
	createdBy: string | null;
	isActive: boolean;
	createdAt: string;
	updatedAt: string;
};

export interface NewSkill {
	name: string;
	content: string;
	priority: number;
	integrationId: string;
}

export interface EditSkill extends NewSkill {
	isActive: boolean;
}

interface SkillShape {
	ai_prompt_skill_id: string;
	name: string;
	content: string;
	priority: number;
	integration_id: string | null;
	created_by: string | null;
	is_active: boolean;
	created_at: string;
	updated_at: string;
}

function toSkill(s: SkillShape): Skill {
	return {
		id: s.ai_prompt_skill_id,
		name: s.name,
		content: s.content,
		priority: s.priority,
		integrationId: s.integration_id,
		createdBy: s.created_by,
		isActive: s.is_active,
		createdAt: s.created_at,
		updatedAt: s.updated_at
	};
}

// Built-in file skills (Skills/*.md shipped with the backend). Read-only; they
// prefix every tenant's system prompt in the order returned (base.md first).
export interface BuiltinSkill {
	name: string;
	content: string;
}

export async function listBuiltinSkills(): Promise<BuiltinSkill[]> {
	return api<BuiltinSkill[]>('/skills/builtin');
}

// Overwrites a built-in file skill (admin). The name is a relative path
// ("vendors/cisco.md") — encode each segment, keep the slashes.
export async function saveBuiltinSkill(name: string, content: string): Promise<BuiltinSkill> {
	const path = name.split('/').map(encodeURIComponent).join('/');
	return api<BuiltinSkill>(`/skills/builtin/${path}`, {
		method: 'PUT',
		body: JSON.stringify({ content })
	});
}

// Which rows a catalog page asks for.
//
// A skill or spec bound to an integration belongs to that integration's screen, not
// here: mixed into the global list it is noise, and worse, it invites editing the
// scoped copy from a page that says nothing about which system it applies to. So the
// default is `global` and the scoped rows are reached from the integration itself
// (or via the ?integration= deep link, which is what that page's filter chip uses).
export type CatalogScope = { integrationId?: string | null };

function scopeQuery(scope?: CatalogScope): string {
	return scope?.integrationId
		? `&integrationId=${encodeURIComponent(scope.integrationId)}`
		: '&globalOnly=true';
}

export async function listSkills(
	limit = 100,
	offset = 0,
	scope?: CatalogScope
): Promise<ListResponse<Skill>> {
	const res = await api<ListResponse<SkillShape>>(
		`/skills?limit=${limit}&offset=${offset}${scopeQuery(scope)}`
	);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toSkill) };
}

export async function createSkill(p: NewSkill): Promise<Skill> {
	return toSkill(
		await api<SkillShape>('/skills', {
			method: 'POST',
			body: JSON.stringify({
				name: p.name,
				content: p.content,
				priority: p.priority,
				integration_id: p.integrationId || null
			})
		})
	);
}

export async function updateSkill(id: string, p: EditSkill): Promise<Skill> {
	return toSkill(
		await api<SkillShape>(`/skills/${id}`, {
			method: 'PUT',
			body: JSON.stringify({
				name: p.name,
				content: p.content,
				priority: p.priority,
				// An empty box means "unlink". A null integration_id reads as "field
				// absent" on the API's partial-update DTO, so the intent needs its
				// own flag — otherwise clearing the scope would silently no-op.
				integration_id: p.integrationId || null,
				clear_integration: !p.integrationId,
				is_active: p.isActive
			})
		})
	);
}

export async function deleteSkill(id: string): Promise<void> {
	await api<SkillShape>(`/skills/${id}`, { method: 'DELETE' });
}

// Bulk import of markdown skills. One call, one verdict per file: a directory of
// prompts written elsewhere is how skills actually arrive, and importing them one at a
// time means the first security rejection strands the rest half-loaded.
export interface ImportSkillFile {
	name: string;
	content: string;
}

export interface ImportSkillResult {
	name: string;
	status: 'created' | 'updated' | 'skipped' | 'failed';
	error: string | null;
}

export interface ImportSkillsSummary {
	created: number;
	updated: number;
	skipped: number;
	failed: number;
	results: ImportSkillResult[];
}

export async function importSkills(
	files: ImportSkillFile[],
	opts: { overwrite?: boolean; priority?: number } = {}
): Promise<ImportSkillsSummary> {
	const res = await api<{
		created: number;
		updated: number;
		skipped: number;
		failed: number;
		results: { name: string; status: string; error: string | null }[];
	}>('/skills/import', {
		method: 'POST',
		body: JSON.stringify({
			files,
			overwrite: opts.overwrite ?? false,
			priority: opts.priority
		})
	});
	return {
		created: res.created,
		updated: res.updated,
		skipped: res.skipped,
		failed: res.failed,
		results: (res.results ?? []).map((r) => ({
			name: r.name,
			status: r.status as ImportSkillResult['status'],
			error: r.error
		}))
	};
}
