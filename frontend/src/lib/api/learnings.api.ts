// Agent learning CRUD (`/api/learnings`, Admin). Learnings are error->fix hints
// the agent applies. System learnings are built-in (not user-editable).

import { api, type ListResponse } from '$lib/api/client';

export type Learning = {
	id: string;
	errorPattern: string;
	errorCategory: string;
	serviceType: string;
	toolName: string;
	fixStrategy: string;
	category: string;
	confidence: number;
	successCount: number;
	failureCount: number;
	isSystem: boolean;
	isActive: boolean;
	createdAt: string;
	updatedAt: string;
};

export interface NewLearning {
	errorPattern: string;
	errorCategory: string;
	serviceType: string;
	toolName: string;
	fixStrategy: string;
}

export interface EditLearning extends NewLearning {
	isActive: boolean;
}

interface LearningShape {
	agent_learning_id: string;
	error_pattern: string;
	error_category: string;
	service_type: string;
	tool_name: string;
	fix_strategy: string;
	category: string;
	confidence: number;
	success_count: number;
	failure_count: number;
	is_system: boolean;
	is_active: boolean;
	created_at: string;
	updated_at: string;
}

function toLearning(l: LearningShape): Learning {
	return {
		id: l.agent_learning_id,
		errorPattern: l.error_pattern,
		errorCategory: l.error_category,
		serviceType: l.service_type,
		toolName: l.tool_name,
		fixStrategy: l.fix_strategy,
		category: l.category,
		confidence: l.confidence,
		successCount: l.success_count,
		failureCount: l.failure_count,
		isSystem: l.is_system,
		isActive: l.is_active,
		createdAt: l.created_at,
		updatedAt: l.updated_at
	};
}

function body(p: NewLearning) {
	return {
		error_pattern: p.errorPattern,
		error_category: p.errorCategory || null,
		service_type: p.serviceType || null,
		tool_name: p.toolName || null,
		fix_strategy: p.fixStrategy || 'parameter_adjust'
	};
}

export async function listLearnings(limit = 100, offset = 0): Promise<ListResponse<Learning>> {
	const res = await api<ListResponse<LearningShape>>(`/learnings?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toLearning) };
}

export async function createLearning(p: NewLearning): Promise<Learning> {
	return toLearning(await api<LearningShape>('/learnings', { method: 'POST', body: JSON.stringify(body(p)) }));
}

export async function updateLearning(id: string, p: EditLearning): Promise<Learning> {
	return toLearning(
		await api<LearningShape>(`/learnings/${id}`, {
			method: 'PUT',
			body: JSON.stringify({ ...body(p), is_active: p.isActive })
		})
	);
}

export async function deleteLearning(id: string): Promise<void> {
	await api<LearningShape>(`/learnings/${id}`, { method: 'DELETE' });
}
