// Loader: template security validation (dry-run) + validation history
// (`/api/loader`, Admin).

import { api, type ListResponse } from '$lib/api/client';

export interface TemplateIssue {
	severity: string;
	message: string;
}

export interface ValidationResult {
	ok: boolean;
	issues: TemplateIssue[];
}

export interface ValidationRecord {
	id: string;
	kind: string;
	targetName: string;
	ok: boolean;
	issues: TemplateIssue[];
	userId: string | null;
	at: string;
}

interface RecordShape {
	validation_record_id: string;
	kind: string;
	target_name: string;
	ok: boolean;
	issues: TemplateIssue[];
	user_id: string | null;
	at: string;
}

function toRecord(r: RecordShape): ValidationRecord {
	return {
		id: r.validation_record_id,
		kind: r.kind,
		targetName: r.target_name,
		ok: r.ok,
		issues: r.issues ?? [],
		userId: r.user_id,
		at: r.at
	};
}

export async function validateTemplate(kind: string, name: string, content: string): Promise<ValidationResult> {
	return api<ValidationResult>('/loader/validate', {
		method: 'POST',
		body: JSON.stringify({ kind, name, content })
	});
}

export async function listValidations(kind = ''): Promise<ValidationRecord[]> {
	const qs = new URLSearchParams({ limit: '50' });
	if (kind) qs.set('kind', kind);
	const res = await api<ListResponse<RecordShape>>(`/loader/validations?${qs.toString()}`);
	return res.items.map(toRecord);
}
