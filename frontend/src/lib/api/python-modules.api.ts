// The `python_snippet` import allowlist (`/api/python-modules`, Admin).
//
// Every row here widens what arbitrary snippet code can reach, which is why the
// API is admin-only and why `requires_network` is surfaced so prominently: a
// network-capable module is only usable by a snippet that also has
// `network_enabled`, and both switches are deliberate acts.
//
// A row is one of two things. A `stdlib` row only records permission — the module
// is already on the interpreter, so it is `ready` the moment it is created. A `pip`
// row also carries an INSTALL: it starts `pending`, the backend provisioner runs
// `pip install` into the shared package directory, and only then does it become
// `ready`. Until it does, the module is on the allowlist and still not importable —
// which is why `status` belongs in the table rather than behind a detail view.

import { api, type ListResponse } from '$lib/api/client';

export type PythonModuleSource = 'stdlib' | 'pip';
export type PythonModuleStatus = 'pending' | 'installing' | 'ready' | 'failed';

export type PythonModule = {
	id: string;
	module: string;
	description: string | null;
	requiresNetwork: boolean;
	source: PythonModuleSource;
	pipSpec: string | null;
	status: PythonModuleStatus;
	installedVersion: string | null;
	error: string | null;
	updatedAt: string;
};

export interface PythonModulePayload {
	module: string;
	description: string;
	requiresNetwork: boolean;
	source: PythonModuleSource;
	/** Only meaningful for `pip`; blank means "same as the module name". */
	pipSpec: string;
}

interface ModuleShape {
	allowed_python_module_id: string;
	module: string;
	description: string | null;
	requires_network: boolean;
	source: PythonModuleSource;
	pip_spec: string | null;
	status: PythonModuleStatus;
	installed_version: string | null;
	error: string | null;
	updated_at: string;
}

function toModule(m: ModuleShape): PythonModule {
	return {
		id: m.allowed_python_module_id,
		module: m.module,
		description: m.description,
		requiresNetwork: m.requires_network,
		source: m.source,
		pipSpec: m.pip_spec,
		status: m.status,
		installedVersion: m.installed_version,
		error: m.error,
		updatedAt: m.updated_at
	};
}

function toBody(p: PythonModulePayload) {
	return {
		module: p.module,
		description: p.description || null,
		requires_network: p.requiresNetwork,
		source: p.source,
		// Omitted for stdlib: sending a spec for a module nothing installs would read
		// as though something were going to happen.
		pip_spec: p.source === 'pip' ? p.pipSpec.trim() || null : null
	};
}

export async function listPythonModules(limit = 200): Promise<ListResponse<PythonModule>> {
	const res = await api<ListResponse<ModuleShape>>(`/python-modules?limit=${limit}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toModule) };
}

export async function createPythonModule(p: PythonModulePayload): Promise<PythonModule> {
	return toModule(
		await api<ModuleShape>('/python-modules', { method: 'POST', body: JSON.stringify(toBody(p)) })
	);
}

export async function updatePythonModule(
	id: string,
	p: PythonModulePayload
): Promise<PythonModule> {
	return toModule(
		await api<ModuleShape>(`/python-modules/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p))
		})
	);
}

/** Re-queue a failed install, once the admin has fixed whatever pip complained about. */
export async function retryPythonModule(id: string): Promise<PythonModule> {
	return toModule(await api<ModuleShape>(`/python-modules/${id}/retry`, { method: 'POST' }));
}

export async function deletePythonModule(id: string): Promise<void> {
	await api<unknown>(`/python-modules/${id}`, { method: 'DELETE' });
}
