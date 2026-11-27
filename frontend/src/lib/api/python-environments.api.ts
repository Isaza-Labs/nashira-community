// Python environments built with uv (`/api/python-environments`).
//
// Each environment is a named set of pinned packages the backend builds at runtime:
// `uv venv` + `uv pip install`, swapped into place when it finishes. Devices (for the
// SSH runner) and python_snippet / ansible_playbook snippets can pick one; null means
// the system Python, which is what everything used before environments existed.
//
// Why: paramiko 5 removed the `ssh-rsa` host key algorithm, so a device that offers
// nothing else fails to connect. The seeded `ssh-legacy` environment pins paramiko < 5
// and is the SSH fallback — a connection failing that way is retried once with it.
//
// Reads are Operator (the device and snippet pickers need them); writes are Admin,
// because building an environment installs and later runs third-party code. Index URL
// credentials come back masked (`https://***@host`); sending the masked text back
// unchanged keeps the stored credentials.
//
// `included_packages` are the pip specs the Python packages allowlist adds to an
// environment (rows whose install targets include it). They are part of what is built —
// the effective requirements are `requirements` plus these, deduplicated — and are
// edited on the allowlist row, not here.

import { api, type ListResponse } from '$lib/api/client';

export type PythonEnvironmentStatus = 'pending' | 'building' | 'ready' | 'failed';

export type PythonEnvironment = {
	id: string;
	name: string;
	description: string | null;
	pythonVersion: string | null;
	requirements: string;
	isSshFallback: boolean;
	status: PythonEnvironmentStatus;
	lastError: string | null;
	resolvedPackages: string | null;
	requirementsHash: string | null;
	/** Pip specs the packages allowlist installs here too; read-only on this side. */
	includedPackages: string[];
	builtAt: string | null;
	createdAt: string;
	updatedAt: string;
};

export interface PythonEnvironmentPayload {
	name: string;
	description: string;
	/** Blank means the image's python3. */
	pythonVersion: string;
	requirements: string;
	isSshFallback: boolean;
}

interface EnvironmentShape {
	python_environment_id: string;
	name: string;
	description: string | null;
	python_version: string | null;
	requirements: string;
	is_ssh_fallback: boolean;
	status: PythonEnvironmentStatus;
	last_error: string | null;
	resolved_packages: string | null;
	requirements_hash: string | null;
	included_packages: string[] | null;
	built_at: string | null;
	created_at: string;
	updated_at: string;
}

function toEnvironment(e: EnvironmentShape): PythonEnvironment {
	return {
		id: e.python_environment_id,
		name: e.name,
		description: e.description,
		pythonVersion: e.python_version,
		requirements: e.requirements,
		isSshFallback: e.is_ssh_fallback,
		status: e.status,
		lastError: e.last_error,
		resolvedPackages: e.resolved_packages,
		requirementsHash: e.requirements_hash,
		includedPackages: e.included_packages ?? [],
		builtAt: e.built_at,
		createdAt: e.created_at,
		updatedAt: e.updated_at
	};
}

function toBody(p: PythonEnvironmentPayload) {
	return {
		name: p.name.trim(),
		description: p.description.trim(),
		// "" (not null) so an edit can clear it back to the image's python3.
		python_version: p.pythonVersion.trim(),
		requirements: p.requirements,
		is_ssh_fallback: p.isSshFallback
	};
}

export async function listPythonEnvironments(): Promise<ListResponse<PythonEnvironment>> {
	const res = await api<ListResponse<EnvironmentShape>>('/python-environments');
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toEnvironment) };
}

export async function createPythonEnvironment(p: PythonEnvironmentPayload): Promise<PythonEnvironment> {
	return toEnvironment(
		await api<EnvironmentShape>('/python-environments', { method: 'POST', body: JSON.stringify(toBody(p)) })
	);
}

export async function updatePythonEnvironment(
	id: string,
	p: PythonEnvironmentPayload
): Promise<PythonEnvironment> {
	return toEnvironment(
		await api<EnvironmentShape>(`/python-environments/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p))
		})
	);
}

/** Queue another build — after fixing a failure, or to pick up newer releases in range. */
export async function rebuildPythonEnvironment(id: string): Promise<PythonEnvironment> {
	return toEnvironment(
		await api<EnvironmentShape>(`/python-environments/${id}/rebuild`, { method: 'POST' })
	);
}

export async function deletePythonEnvironment(id: string): Promise<void> {
	await api<unknown>(`/python-environments/${id}`, { method: 'DELETE' });
}

/**
 * Options for an environment picker: "System default" (value '') first, then every
 * environment with its status when it is not ready, so picking one still building is
 * possible but visibly so.
 */
export function environmentOptions(items: PythonEnvironment[]): { value: string; label: string }[] {
	return [
		{ value: '', label: 'System default' },
		...items.map((e) => ({
			value: e.id,
			label:
				e.name +
				(e.isSshFallback ? ' (SSH fallback)' : '') +
				(e.status === 'ready' ? '' : ` — ${e.status}`)
		}))
	];
}
