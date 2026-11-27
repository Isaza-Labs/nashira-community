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
//
// A pip row also has INSTALL TARGETS: the system Python (`install_in_system`, the
// default and what every row did before targets existed) and any number of uv
// environments (`environment_ids`). An environment gets the package through its
// effective requirements — it rebuilds with the spec added — so changing a row's
// targets or pip spec, or revoking it, rebuilds every environment involved. Turning
// the system target off does not uninstall anything already in the system site dir.
// `status` and `installed_version` describe the system install only.

import { api, type ListResponse } from '$lib/api/client';

export type PythonModuleSource = 'stdlib' | 'pip';
export type PythonModuleStatus = 'pending' | 'installing' | 'ready' | 'failed';

/** A uv environment a pip row is installed in, with that environment's build status. */
export type PythonModuleEnvironmentTarget = {
	id: string;
	name: string;
	status: string;
};

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
	/** Pip rows: whether the system Python is one of the install targets. */
	installInSystem: boolean;
	/** Pip rows: the uv environments it is installed in. Always empty for stdlib. */
	environments: PythonModuleEnvironmentTarget[];
	updatedAt: string;
};

export interface PythonModulePayload {
	module: string;
	description: string;
	requiresNetwork: boolean;
	source: PythonModuleSource;
	/** Only meaningful for `pip`; blank means "same as the module name". */
	pipSpec: string;
	/** Only meaningful for `pip`. */
	installInSystem: boolean;
	/** Only meaningful for `pip`; the full set — an empty list removes every environment. */
	environmentIds: string[];
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
	install_in_system: boolean;
	environments: { python_environment_id: string; name: string; status: string }[] | null;
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
		installInSystem: m.install_in_system ?? true,
		environments: (m.environments ?? []).map((e) => ({
			id: e.python_environment_id,
			name: e.name,
			status: e.status
		})),
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
		pip_spec: p.source === 'pip' ? p.pipSpec.trim() || null : null,
		// Likewise: a stdlib row has nothing to install and the API refuses targets for it.
		install_in_system: p.source === 'pip' ? p.installInSystem : null,
		environment_ids: p.source === 'pip' ? p.environmentIds : null
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
