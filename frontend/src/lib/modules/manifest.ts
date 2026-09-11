// What this deployment can do, as the backend reports it.
//
// Deliberately free of imports: this file is the whole decision — which modules are
// on, and what to believe when the answer never arrives — and keeping it dependency-
// free is what lets `npm run check:modules` run it under plain node and assert the
// fail-closed case, which is precisely the one nobody exercises by hand.
//
// The SPA never learns NASHIRA_MODULES at build time. One frontend image serves every
// combination, and the backend stays the source of truth.

// Public module keys, kebab-case, identical to the backend catalog. check:modules
// compares this list against ModuleCatalog.cs, so the two cannot drift.
export const MODULE_IDS = [
	'core',
	'chat',
	'ai-studio',
	'automation',
	'fleet',
	'integrations',
	'communications',
	'secrets',
	'git',
	'knowledge',
	'artifacts',
	'governance',
	'observability'
] as const;

export type ModuleId = (typeof MODULE_IDS)[number];

export type ModuleConfigurationMode = 'default_all' | 'explicit';

export interface ModuleState {
	id: ModuleId;
	enabled: boolean;
	configurable: boolean;
	dependencies: ModuleId[];
}

export interface ModuleManifest {
	configurationMode: ModuleConfigurationMode;
	modules: ModuleState[];
}

// The wire shape of GET /api/modules.
interface ModuleStateShape {
	id: string;
	enabled: boolean;
	configurable: boolean;
	dependencies: string[];
}

interface ModuleManifestShape {
	configuration_mode: string;
	modules: ModuleStateShape[];
}

export function isModuleId(value: unknown): value is ModuleId {
	return typeof value === 'string' && (MODULE_IDS as readonly string[]).includes(value);
}

// What the UI believes about the deployment. Immutable for the life of a session:
// changing the selection requires recreating the containers, so a manifest that
// changed under a running tab is not a case worth modelling.
export class ModuleAvailability {
	readonly configurationMode: ModuleConfigurationMode;
	readonly modules: readonly ModuleState[];
	private readonly enabled: ReadonlySet<ModuleId>;

	private constructor(mode: ModuleConfigurationMode, modules: ModuleState[]) {
		this.configurationMode = mode;
		this.modules = modules;
		this.enabled = new Set(modules.filter((m) => m.enabled).map((m) => m.id));
	}

	isEnabled(id: ModuleId): boolean {
		return this.enabled.has(id);
	}

	// A destination is available when every capability it needs is on. The "all"
	// rule, not "any": a page that needs two capabilities and has one is a page that
	// half works, which is worse than one that is absent.
	areEnabled(ids: readonly ModuleId[]): boolean {
		return ids.every((id) => this.isEnabled(id));
	}

	get enabledIds(): ModuleId[] {
		return this.modules.filter((m) => m.enabled).map((m) => m.id);
	}

	static from(manifest: ModuleManifest): ModuleAvailability {
		return new ModuleAvailability(manifest.configurationMode, manifest.modules);
	}

	// What the UI believes when it cannot ask, or cannot understand the answer: core
	// and nothing else. The opposite default — assume everything works — turns one
	// failed request into a menu full of pages that 503, and into requests fired at
	// capabilities this deployment never had.
	static failClosed(): ModuleAvailability {
		return new ModuleAvailability(
			'explicit',
			MODULE_IDS.map((id) => ({
				id,
				enabled: id === 'core',
				configurable: id !== 'core',
				dependencies: []
			}))
		);
	}
}

// Defensive on purpose: a malformed manifest is a failure to answer, not a licence to
// guess. Unknown module keys are dropped rather than passed through — a frontend that
// does not know a capability cannot navigate to it either way, and inventing an id
// here would put an unroutable entry in every derived surface.
export function parseManifest(raw: unknown): ModuleManifest {
	const shape = raw as ModuleManifestShape | null | undefined;
	if (!shape || !Array.isArray(shape.modules)) throw new Error('modules: malformed manifest');

	if (shape.configuration_mode !== 'default_all' && shape.configuration_mode !== 'explicit')
                throw new Error('modules: invalid configuration mode');
        const mode: ModuleConfigurationMode = shape.configuration_mode;

	const modules: ModuleState[] = [];
	for (const entry of shape.modules) {
		if (!entry || !isModuleId(entry.id)) continue;
		modules.push({
			id: entry.id,
			enabled: entry.enabled === true,
			configurable: entry.configurable === true,
			dependencies: Array.isArray(entry.dependencies)
				? entry.dependencies.filter(isModuleId)
				: []
		});
	}

	// core is always enabled server-side; a payload that says otherwise is not a
	// deployment without core, it is an answer that cannot be trusted.
	if (!modules.some((m) => m.id === 'core' && m.enabled))
		throw new Error('modules: manifest does not report core as enabled');

	return { configurationMode: mode, modules };
}
