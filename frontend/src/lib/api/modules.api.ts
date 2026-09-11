import { api } from '$lib/api/client';
import { parseManifest, type ModuleManifest } from '$lib/modules/manifest';

// GET /api/modules — the deployment's own capability manifest, authenticated and read
// once per session. Part of core, so it answers in every deployment; if this call
// fails there is no second source to fall back to, and the store treats that as
// "core only" rather than as "everything".
export async function getModuleManifest(): Promise<ModuleManifest> {
	return parseManifest(await api<unknown>('/modules'));
}
