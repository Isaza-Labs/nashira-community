// The single source of truth for navigation.
//
// Four surfaces used to describe the same map by hand — the sidebar, the command
// palette, the admin hub and the `uiPath` field in the docs — and they had already
// drifted: `/api/python-modules` shipped with a full CRUD API and no screen, while
// the docs pointed its `uiPath` at a page that never had it. Declaring a
// destination once and deriving all four removes the class of bug rather than the
// instance.
//
// Shape of the model:
//   PAGES     — every navigable destination, with the role that may reach it.
//   SECTIONS  — tabbed surfaces: several pages that are edited together and should
//               therefore be navigated together (a spec IS the integration, a skill
//               is how to operate it; they belong side by side, not two clicks apart).
//   SIDEBAR   — the tree, referencing pages and sections by id.
//
// Everything else in this file is derivation.

import {
	MessageSquare,
	LayoutDashboard,
	Server,
	Boxes,
	Workflow,
	FileText,
	Download,
	Puzzle,
	Layers,
	Terminal,
	Braces,
	Sparkles,
	ScrollText,
	FileCode,
	Brain,
	UserCog,
	UploadCloud,
	ShieldCheck,
	Plug,
	Blocks,
	Megaphone,
	Mail,
	KeyRound,
	Lock,
	ShieldAlert,
	FileClock,
	MessagesSquare,
	Users,
	BookOpen,
	GitBranch,
	BookMarked,
	Palette,
	CircleUser,
	Activity,
	CalendarClock,
	Gauge,
	Radar,
	SlidersHorizontal,
	PanelLeft
} from 'lucide-svelte';

import type { ModuleAvailability, ModuleId } from '$lib/modules/manifest';

export type NavIcon = typeof MessageSquare;

export type NavRole = 'viewer' | 'operator' | 'admin';
export type NavigationVisibility = Readonly<Record<string, boolean>>;

/** Cumulative, mirroring the API policies: admin ⊃ operator ⊃ viewer. */
const RANK: Record<NavRole, number> = { viewer: 0, operator: 1, admin: 2 };

export function canSee(userRole: string | undefined, minRole: NavRole): boolean {
	const role = (userRole ?? '') as NavRole;
	return (RANK[role] ?? -1) >= RANK[minRole];
}

export type NavGroupId =
	| 'main'
	| 'operate'
	| 'build'
	| 'agent'
	| 'connect'
	| 'govern'
	| 'resources'
	| 'personal';

export type NavSectionId = 'ai-studio' | 'integrations' | 'artifacts';

export interface NavPage {
	href: string;
	label: string;
	icon: NavIcon;
	group: NavGroupId;
	minRole: NavRole;
	/**
	 * The capability this destination belongs to. Required, so a page cannot be added
	 * without answering the question — an omission would be a compile error rather
	 * than a screen that quietly appears in deployments that never bought it.
	 */
	module: ModuleId;
	/** One line for the hub card and the section tab title attribute. */
	desc: string;
	/** Extra terms the palette should match on. */
	keywords?: string;
	/** The tabbed surface this page belongs to, if any. */
	section?: NavSectionId;
	/**
	 * A create action the palette can offer. The page opens with `?new=1`, which
	 * each listing interprets by opening its create modal.
	 */
	action?: { label: string; keywords?: string; minRole?: NavRole };
	/** Excluded from the hub index (already reachable, or not configuration). */
	hub?: false;
}

const NAVIGATION_PERMISSIONS_PATH = '/admin/navigation-permissions';

/**
 * Effective visibility: module ∩ role ∩ stored navigation permission.
 *
 * The module comes first and is not negotiable. A permission is a preference about a
 * capability this deployment has; a disabled module means the capability is not here
 * at all, so no stored setting — not even the exception that keeps the navigation
 * screen reachable — can put its page back on the menu.
 */
export function canViewPage(
	page: NavPage,
	userRole: string | undefined,
	availability: ModuleAvailability,
	visibility: NavigationVisibility = {}
): boolean {
	if (!availability.isEnabled(page.module)) return false;
	if (!canSee(userRole, page.minRole)) return false;
	// The management screen cannot hide itself and strand every administrator.
	if (page.href === NAVIGATION_PERMISSIONS_PATH) return true;
	return visibility[page.href] !== false;
}

export interface NavSection {
	id: NavSectionId;
	label: string;
	icon: NavIcon;
	/** Shown under the section title on every page of the surface. */
	blurb: string;
}

// ── pages ───────────────────────────────────────────────────────────────

export const PAGES: NavPage[] = [
	// Main — the dashboard sits alone at the top, mirroring Flow Weaver.
	{
		href: '/overview',
		label: 'Overview',
		icon: LayoutDashboard,
		group: 'main',
		minRole: 'viewer',
		module: 'core',
		desc: 'Fleet status at a glance',
		keywords: 'dashboard home status summary'
	},

	// Intelligence — the agent itself, and everything that decides what it
	// knows and may do. Sits above Build on purpose: the agent is the entry
	// point to most of what follows.
	{
		href: '/chat',
		label: 'Chat',
		icon: MessageSquare,
		group: 'agent',
		minRole: 'viewer',
		module: 'chat',
		desc: 'Ask the agent',
		keywords: 'agent ask assistant conversation',
		hub: false
	},

	// Operate — the day to day.
	{
		href: '/devices',
		label: 'Devices',
		icon: Server,
		group: 'operate',
		minRole: 'viewer',
		module: 'fleet',
		desc: 'The inventory every run targets',
		keywords: 'inventory switch router host node ssh',
		action: { label: 'Add a device', keywords: 'create new device', minRole: 'operator' }
	},
	{
		href: '/inventory',
		label: 'Inventory sources',
		icon: Boxes,
		group: 'operate',
		minRole: 'viewer',
		module: 'fleet',
		desc: 'Sync devices from NetBox',
		keywords: 'netbox sources sync cmdb import'
	},
	{
		href: '/runs',
		label: 'Runs',
		icon: Activity,
		group: 'operate',
		minRole: 'viewer',
		module: 'automation',
		desc: 'Every execution across every workflow, and how each one ended',
		keywords: 'runs executions history failed rolled back duration'
	},
	{
		href: '/schedules',
		label: 'Schedules',
		icon: CalendarClock,
		group: 'operate',
		minRole: 'viewer',
		module: 'automation',
		desc: 'What is going to run, and what should have',
		keywords: 'schedules triggers cron webhook next run overdue'
	},
	{
		href: '/workflows',
		label: 'Workflows',
		icon: Workflow,
		group: 'build',
		minRole: 'viewer',
		module: 'automation',
		desc: 'Automation, versioned and promoted',
		keywords: 'automation runs dag yaml promote simulate',
		// Workflows are authored by the agent or imported as YAML; there is no blank
		// create form to offer, so the action is the one that exists.
		action: { label: 'Import a workflow', keywords: 'create workflow yaml upload', minRole: 'operator' }
	},
	{
		href: '/reports',
		label: 'Reports',
		icon: FileText,
		group: 'operate',
		minRole: 'viewer',
		module: 'artifacts',
		section: 'artifacts',
		desc: 'Stored artifacts with retention',
		keywords: 'artifacts markdown csv retention download evidence',
		action: { label: 'Store a report', keywords: 'create report', minRole: 'operator' }
	},
	{
		href: '/exports',
		label: 'Exports',
		icon: Download,
		group: 'operate',
		minRole: 'viewer',
		module: 'artifacts',
		section: 'artifacts',
		desc: 'Generated spreadsheets and CSVs',
		keywords: 'csv xlsx spreadsheet download excel'
	},

	// Build — the material workflows are made of. Operator-owned by the API, so it
	// does not belong behind an "Admin" label.
	{
		href: '/admin/snippets',
		label: 'Snippets',
		icon: Puzzle,
		group: 'build',
		minRole: 'viewer',
		module: 'automation',
		desc: 'Reusable workflow steps',
		keywords: 'steps handlers ssh rest transform python mcp ping',
		action: { label: 'New snippet', keywords: 'create snippet', minRole: 'operator' }
	},
	{
		href: '/admin/pools',
		label: 'Device pools',
		icon: Layers,
		group: 'operate',
		minRole: 'viewer',
		module: 'fleet',
		desc: 'Grouped run targets, static or by rule',
		keywords: 'groups targets rules fleet membership',
		action: { label: 'New device pool', keywords: 'create pool', minRole: 'operator' }
	},
	{
		href: '/admin/vendor-commands',
		label: 'Vendor commands',
		icon: Terminal,
		group: 'connect',
		minRole: 'viewer',
		module: 'fleet',
		desc: 'One intent, the right CLI per platform',
		keywords: 'cli intent platform multivendor ios nxos eos junos',
		action: { label: 'New vendor command', keywords: 'create command', minRole: 'operator' }
	},
	{
		href: '/admin/python-modules',
		label: 'Python modules',
		icon: Braces,
		group: 'govern',
		minRole: 'admin',
		module: 'automation',
		desc: 'The allowlist a python_snippet may import',
		keywords: 'sandbox allowlist import security stdlib script',
		action: { label: 'Allow a Python module', keywords: 'create python module', minRole: 'admin' }
	},

	// Intelligence — the agent's configuration surface.
	{
		href: '/admin/skills',
		label: 'Prompt skills',
		icon: ScrollText,
		group: 'agent',
		minRole: 'admin',
		module: 'ai-studio',
		section: 'ai-studio',
		desc: 'Knowledge always in the agent context',
		keywords: 'system prompt persona instructions base.md',
		action: { label: 'New prompt skill', keywords: 'create skill', minRole: 'admin' }
	},
	{
		href: '/admin/specs',
		label: 'API specs',
		icon: FileCode,
		group: 'agent',
		minRole: 'admin',
		module: 'ai-studio',
		section: 'ai-studio',
		desc: 'OpenAPI documents the agent can call',
		keywords: 'openapi swagger yaml json operations dynamic',
		action: { label: 'New API spec', keywords: 'create spec upload openapi', minRole: 'admin' }
	},
	{
		href: '/admin/providers',
		label: 'AI providers',
		icon: Sparkles,
		group: 'agent',
		minRole: 'admin',
		module: 'ai-studio',
		section: 'ai-studio',
		desc: 'Which model answers, and where it runs',
		keywords: 'llm openai anthropic gemini deepseek kimi ollama custom model inference',
		action: { label: 'New AI provider', keywords: 'create provider', minRole: 'admin' }
	},
	{
		href: '/admin/learnings',
		label: 'Learnings',
		icon: Brain,
		group: 'agent',
		minRole: 'admin',
		module: 'ai-studio',
		section: 'ai-studio',
		desc: 'Recorded fixes for known failures',
		keywords: 'self correction fixes error pattern confidence'
	},
	{
		href: '/admin/profiles',
		label: 'Profiles',
		icon: UserCog,
		group: 'agent',
		minRole: 'admin',
		module: 'ai-studio',
		section: 'ai-studio',
		desc: 'Which skills and tone a user gets',
		keywords: 'persona assistant response style assignment'
	},
	{
		href: '/admin/loader',
		label: 'Validation',
		icon: UploadCloud,
		group: 'agent',
		minRole: 'admin',
		module: 'ai-studio',
		section: 'ai-studio',
		desc: 'Validate skills and specs before they go live',
		keywords: 'loader template security validation upload'
	},
	{
		href: '/admin/permissions',
		label: 'Tool permissions',
		icon: ShieldCheck,
		group: 'govern',
		minRole: 'admin',
		module: 'governance',
		desc: 'Per-user tool domains for the agent',
		keywords: 'access domains granular restrict netbox ssh'
	},
	{
		href: '/admin/navigation-permissions',
		label: 'Navigation access',
		icon: PanelLeft,
		group: 'govern',
		minRole: 'admin',
		module: 'governance',
		desc: 'Visible Nashira areas by role and user',
		keywords: 'navigation sidebar sections visibility roles users access'
	},

	// Connect — systems outside Nashira, and the keys to them.
	{
		href: '/admin/integrations',
		label: 'Integrations',
		icon: Plug,
		group: 'connect',
		minRole: 'viewer',
		module: 'integrations',
		section: 'integrations',
		desc: 'External systems and their action catalogue',
		keywords: 'netbox servicenow external rest base url auth health',
		action: { label: 'New integration', keywords: 'create integration', minRole: 'admin' }
	},
	{
		href: '/admin/mcp',
		label: 'MCP servers',
		icon: Blocks,
		group: 'connect',
		minRole: 'viewer',
		module: 'integrations',
		section: 'integrations',
		desc: 'Model Context Protocol tool servers',
		keywords: 'model context protocol tools streamable http',
		action: { label: 'New MCP server', keywords: 'create mcp server', minRole: 'admin' }
	},
	{
		href: '/admin/notifications',
		label: 'Notifications',
		icon: Megaphone,
		group: 'connect',
		minRole: 'admin',
		module: 'communications',
		section: 'integrations',
		desc: 'Outbound Slack, Teams and webhook channels',
		keywords: 'slack teams webhook notifications alerts delivery outbound',
		action: { label: 'New notification channel', keywords: 'create channel slack', minRole: 'admin' }
	},
	{
		href: '/admin/messaging-channels',
		label: 'Messaging channels',
		icon: MessageSquare,
		group: 'connect',
		minRole: 'admin',
		module: 'communications',
		section: 'integrations',
		desc: 'Two-way Slack, Teams, WhatsApp and Telegram chat with the agent',
		keywords:
			'slack teams whatsapp telegram bot chat inbound bidirectional webhook socket mode azure relay identity link',
		action: {
			label: 'New messaging channel',
			keywords: 'create channel slack telegram whatsapp teams bot',
			minRole: 'admin'
		}
	},
	{
		href: '/admin/email',
		label: 'Email channels',
		icon: Mail,
		group: 'connect',
		minRole: 'admin',
		module: 'communications',
		section: 'integrations',
		desc: 'Named SMTP relays for workflow mail',
		keywords: 'smtp relay starttls ssl mail sender',
		action: { label: 'New email channel', keywords: 'create smtp channel', minRole: 'admin' }
	},
	// Credentials and secrets live under Operate, next to the devices and runs
	// that consume them — same placement as Flow Weaver.
	{
		href: '/admin/credentials',
		label: 'Credentials',
		icon: KeyRound,
		group: 'operate',
		minRole: 'admin',
		module: 'secrets',
		desc: 'SSH, token, API key and OAuth2 logins',
		keywords: 'ssh key password token api key oauth2 pat',
		action: { label: 'New credential', keywords: 'create credential', minRole: 'admin' }
	},
	{
		href: '/admin/secrets',
		label: 'Secrets',
		icon: Lock,
		group: 'operate',
		minRole: 'admin',
		module: 'secrets',
		desc: 'Named values other configuration points at',
		keywords: 'vault encrypted settings name reference rotation',
		action: { label: 'New secret', keywords: 'create secret', minRole: 'admin' }
	},

	// Govern — who may do what, and the record of what was done.
	{
		href: '/admin/policies',
		label: 'Policies',
		icon: ShieldAlert,
		group: 'govern',
		minRole: 'admin',
		module: 'governance',
		desc: 'Guardrails on runs and promotions',
		keywords: 'guardrails deny gate rules compliance change window',
		action: { label: 'New policy', keywords: 'create policy guardrail', minRole: 'admin' }
	},
	{
		href: '/admin/slo',
		label: 'SLOs',
		icon: Gauge,
		group: 'govern',
		minRole: 'admin',
		module: 'core',
		desc: 'Latency, error rate, throughput and how failures were contained',
		keywords: 'slo objectives targets latency p95 error rate throughput promotion breach'
	},
	{
		href: '/admin/audit',
		label: 'Audit',
		icon: FileClock,
		group: 'govern',
		minRole: 'admin',
		module: 'governance',
		desc: 'Tamper-evident trail of every mutation, plus the sign-in log',
		keywords: 'trail hash chain events who changed history login signin lockout auth'
	},
	{
		href: '/admin/traces',
		label: 'Traces',
		icon: Radar,
		group: 'govern',
		minRole: 'admin',
		module: 'observability',
		desc: 'What the platform is doing, live — including everything that changed nothing',
		keywords: 'trace live tail debug worker scheduler stuck slow duration request id forensics'
	},
	// Sibling of Audit, and the one people reach for first: Audit proves what changed,
	// Sessions shows what happened — including the turns that changed nothing.
	{
		href: '/admin/sessions',
		label: 'Sessions',
		icon: MessagesSquare,
		group: 'govern',
		minRole: 'admin',
		module: 'observability',
		desc: 'Every agent conversation and the tool calls behind it',
		keywords: 'chat history prompt logs telemetry conversations tool calls forensics'
	},
	{
		href: '/admin/settings',
		label: 'Settings',
		icon: SlidersHorizontal,
		group: 'govern',
		minRole: 'admin',
		module: 'core',
		desc: 'Platform behaviour you can change without a redeploy',
		keywords: 'settings toggles flags configuration ssh destructive pip timeout git size'
	},
	{
		href: '/admin/users',
		label: 'Users',
		icon: Users,
		group: 'govern',
		minRole: 'admin',
		module: 'core',
		desc: 'Accounts, roles and lockout',
		keywords: 'accounts roles password lock identity',
		action: { label: 'New user', keywords: 'create user account', minRole: 'admin' }
	},

	// Help — reference material, read far more often than written.
	{
		href: '/knowledge',
		label: 'Knowledge',
		icon: BookOpen,
		group: 'resources',
		minRole: 'viewer',
		module: 'knowledge',
		desc: 'Runbooks and notes the agent can search',
		keywords: 'articles docs runbook wiki procedures',
		action: { label: 'New knowledge article', keywords: 'create article', minRole: 'operator' }
	},
	{
		href: '/git',
		label: 'Git',
		icon: GitBranch,
		group: 'build',
		minRole: 'viewer',
		module: 'git',
		desc: 'Configuration in version control',
		keywords: 'repository commit branch push pull diff backup'
	},
	{
		href: '/docs',
		label: 'Docs',
		icon: BookMarked,
		group: 'resources',
		minRole: 'viewer',
		module: 'core',
		desc: 'What every part of Nashira is for',
		keywords: 'documentation help manual reference guide parameters',
		hub: false
	},

	// Personal — pinned to the footer; personalisation is not work.
	{
		href: '/account',
		label: 'Account',
		icon: CircleUser,
		group: 'personal',
		minRole: 'viewer',
		module: 'core',
		desc: 'Your profile and password',
		keywords: 'profile password me settings',
		hub: false
	},
	{
		href: '/themes',
		label: 'Themes',
		icon: Palette,
		group: 'personal',
		minRole: 'viewer',
		module: 'core',
		desc: 'Recolour the console',
		keywords: 'appearance colors palette look branding dark mode',
		hub: false
	}
];

// ── sections ────────────────────────────────────────────────────────────

export const SECTIONS: NavSection[] = [
	{
		id: 'ai-studio',
		label: 'AI Studio',
		icon: Sparkles,
		blurb: 'What the agent knows, what it can call, and who gets which of it.'
	},
	{
		id: 'integrations',
		label: 'Integrations',
		icon: Plug,
		blurb: 'Every system Nashira talks to, inbound catalogue or outbound message.'
	},
	{
		id: 'artifacts',
		label: 'Artifacts',
		icon: FileText,
		blurb: 'Files Nashira produced — kept with a retention policy, or taken away once.'
	}
];

// ── the sidebar tree ────────────────────────────────────────────────────

export interface SidebarItemRef {
	/** A section renders as one entry that opens its first page. */
	kind: 'page' | 'section';
	id: string;
}

export interface SidebarGroup {
	id: NavGroupId;
	label?: string;
	items: SidebarItemRef[];
}

// Group order and labels mirror Flow Weaver's sidebar — dashboard alone at the
// top, then Intelligence, Build, Integrate, Operate, Govern, Help — so moving
// between the two consoles never means relearning the map.
export const SIDEBAR: SidebarGroup[] = [
	{ id: 'main', items: [{ kind: 'page', id: '/overview' }] },
	{
		id: 'agent',
		label: 'Intelligence',
		items: [
			{ kind: 'page', id: '/chat' },
			{ kind: 'section', id: 'ai-studio' }
		]
	},
	{
		id: 'build',
		label: 'Build',
		items: [
			{ kind: 'page', id: '/workflows' },
			{ kind: 'page', id: '/admin/snippets' },
			{ kind: 'page', id: '/git' }
		]
	},
	{
		id: 'connect',
		label: 'Integrate',
		items: [
			{ kind: 'section', id: 'integrations' },
			{ kind: 'page', id: '/admin/vendor-commands' }
		]
	},
	{
		id: 'operate',
		label: 'Operate',
		items: [
			{ kind: 'page', id: '/runs' },
			{ kind: 'page', id: '/schedules' },
			{ kind: 'page', id: '/devices' },
			{ kind: 'page', id: '/inventory' },
			{ kind: 'page', id: '/admin/pools' },
			{ kind: 'page', id: '/admin/credentials' },
			{ kind: 'page', id: '/admin/secrets' },
			{ kind: 'section', id: 'artifacts' }
		]
	},
	{
		id: 'govern',
		label: 'Govern',
		items: [
			{ kind: 'page', id: '/admin/policies' },
			{ kind: 'page', id: '/admin/permissions' },
			{ kind: 'page', id: '/admin/navigation-permissions' },
			{ kind: 'page', id: '/admin/python-modules' },
			{ kind: 'page', id: '/admin/slo' },
			{ kind: 'page', id: '/admin/audit' },
			{ kind: 'page', id: '/admin/traces' },
			{ kind: 'page', id: '/admin/sessions' },
			{ kind: 'page', id: '/admin/users' },
			{ kind: 'page', id: '/admin/settings' }
		]
	},
	{
		id: 'resources',
		label: 'Help',
		items: [
			{ kind: 'page', id: '/knowledge' },
			{ kind: 'page', id: '/docs' }
		]
	}
];

// ── derivation ──────────────────────────────────────────────────────────

const BY_HREF = new Map(PAGES.map((p) => [p.href, p]));
const BY_SECTION = new Map(SECTIONS.map((s) => [s.id, s]));

export function pageByHref(href: string): NavPage | undefined {
	return BY_HREF.get(href);
}

export function section(id: NavSectionId): NavSection | undefined {
	return BY_SECTION.get(id);
}

/** The pages of a tabbed surface, in declaration order — that is the tab order. */
export function sectionPages(id: NavSectionId): NavPage[] {
	return PAGES.filter((p) => p.section === id);
}

/**
 * Which tabbed surface a path belongs to. Matches nested routes too, so
 * `/workflows/abc` still highlights Workflows and a future `/reports/x` stays
 * inside Artifacts.
 */
export function sectionForPath(pathname: string): NavSection | undefined {
	const page = PAGES.find((p) => isPathActive(pathname, p.href));
	return page?.section ? BY_SECTION.get(page.section) : undefined;
}

export function isPathActive(pathname: string, href: string): boolean {
	// Chat owns the root: the index route redirects there.
	if (href === '/chat') return pathname === '/' || pathname === '/chat' || pathname.startsWith('/chat/');
	return pathname === href || pathname.startsWith(`${href}/`);
}

/**
 * Routes that exist without being destinations: redirects, the hub, the public
 * entrances, and the demo surface. They are still routes a browser can be pointed at,
 * so they still have to say which capability they belong to — check:nav refuses a
 * route that is in neither table.
 *
 * Dynamic children are not listed: `/workflows/[id]` resolves through its parent, the
 * way isPathActive already treats it for the sidebar and the tab bar.
 */
const SPECIAL_ROUTES: Readonly<Record<string, ModuleId>> = {
	// Redirects to Chat or, without it, to Overview — both of which decide for
	// themselves, so the entrance itself belongs to core.
	'/': 'core',
	'/login': 'core',
	// The admin hub. It filters its own cards; the page itself is always reachable,
	// or an administrator would have no way back into a deployment's settings.
	'/admin': 'core',
	// The pre-move audit URL, kept alive for bookmarks and runbooks. It belongs with
	// what it redirects to: a governance-less deployment should not answer it.
	'/audit': 'governance',
	// Self-service linking of an external chat identity, opened from a bot message.
	'/link': 'communications',
	// The component gallery. Core: it renders the design system and calls no API.
	'/kitchen-sink': 'core'
};

/**
 * The capability a path belongs to. Registry pages answer for their own subtrees;
 * everything else comes from SPECIAL_ROUTES.
 *
 * An unrecognised path falls back to core rather than to "blocked": it is a 404, and
 * a 404 rendered inside the shell is preferable to a capability error that suggests
 * the deployment could have had this page. check:nav is what guarantees no real route
 * arrives here unclassified.
 */
export function moduleForPath(pathname: string): ModuleId {
	// The explicit table wins over the page match. `/` is the case that makes it
	// matter: isPathActive deliberately lets Chat own the root so the sidebar lights
	// up there, but the root itself is an entrance that redirects — a deployment
	// without chat must reach it and be sent to Overview, not told the module is off.
	const special = SPECIAL_ROUTES[pathname];
	if (special) return special;

	const page = PAGES.find((candidate) => isPathActive(pathname, candidate.href));
	return page?.module ?? 'core';
}

/** Every route classified outside the page registry, for the static checker. */
export function specialRoutes(): Readonly<Record<string, ModuleId>> {
	return SPECIAL_ROUTES;
}

/**
 * The role a path requires — the registry is the single authority, so the route
 * guard and the nav surfaces can never disagree about who may open a page.
 * Undeclared paths under /admin fail closed to admin; everywhere else any
 * signed-in role passes, which is what every non-admin route requires today.
 */
export function minRoleForPath(pathname: string): NavRole {
	const page = PAGES.find((p) => isPathActive(pathname, p.href));
	if (page) return page.minRole;
	return pathname === '/admin' || pathname.startsWith('/admin/') ? 'admin' : 'viewer';
}

export function canViewPath(
	pathname: string,
	userRole: string | undefined,
	availability: ModuleAvailability,
	visibility: NavigationVisibility = {}
): boolean {
	return blockedReasonForPath(pathname, userRole, availability, visibility) === null;
}

/**
 * Why a path cannot be opened, or null when it can.
 *
 * The two refusals look identical to the router and are opposite to the person
 * reading them: a permission is something an administrator can grant here, and a
 * disabled module is something no permission can reach. Telling them apart is the
 * difference between fixing the problem and searching a settings screen for a toggle
 * that this deployment does not have.
 */
export function blockedReasonForPath(
	pathname: string,
	userRole: string | undefined,
	availability: ModuleAvailability,
	visibility: NavigationVisibility = {}
): 'module' | 'permission' | null {
	if (!availability.isEnabled(moduleForPath(pathname))) return 'module';

	// A special route is answered by its own classification and never by the page that
	// happens to match it. `/` is again the case: Chat owns it for highlighting, but
	// the entrance itself is core and has to be reachable in order to redirect — being
	// told "not available" at the front door of a deployment without chat would leave
	// nowhere to go.
	if (pathname in SPECIAL_ROUTES)
		return canSee(userRole, minRoleForPath(pathname)) ? null : 'permission';

	const page = PAGES.find((candidate) => isPathActive(pathname, candidate.href));
	if (!page) return canSee(userRole, minRoleForPath(pathname)) ? null : 'permission';
	return canViewPage(page, userRole, availability, visibility) ? null : 'permission';
}

/** The lowest role that can reach any page of a section — what gates the tab bar. */
export function sectionMinRole(id: NavSectionId): NavRole {
	const roles = sectionPages(id).map((p) => RANK[p.minRole]);
	const lowest = roles.length > 0 ? Math.min(...roles) : RANK.admin;
	return (Object.keys(RANK) as NavRole[]).find((r) => RANK[r] === lowest) ?? 'admin';
}

export interface ResolvedSidebarItem {
	href: string;
	label: string;
	icon: NavIcon;
	/** Every href this item should light up for. */
	owns: string[];
}

export interface ResolvedSidebarGroup {
	id: NavGroupId;
	label?: string;
	items: ResolvedSidebarItem[];
}

/** The sidebar for one role: groups and items the user may actually reach. */
export function sidebarFor(
	role: string | undefined,
	availability: ModuleAvailability,
	visibility: NavigationVisibility = {}
): ResolvedSidebarGroup[] {
	const out: ResolvedSidebarGroup[] = [];

	for (const group of SIDEBAR) {
		const items: ResolvedSidebarItem[] = [];

		for (const ref of group.items) {
			if (ref.kind === 'page') {
				const page = BY_HREF.get(ref.id);
				if (!page || !canViewPage(page, role, availability, visibility)) continue;
				items.push({ href: page.href, label: page.label, icon: page.icon, owns: [page.href] });
				continue;
			}

			const sec = BY_SECTION.get(ref.id as NavSectionId);
			if (!sec) continue;
			const pages = sectionPages(sec.id).filter((p) =>
				canViewPage(p, role, availability, visibility)
			);
			if (pages.length === 0) continue;
			// The entry opens the first page the user can reach, and stays lit for
			// any page in the surface — the tab bar handles movement within it.
			items.push({
				href: pages[0].href,
				label: sec.label,
				icon: sec.icon,
				owns: pages.map((p) => p.href)
			});
		}

		if (items.length > 0) out.push({ id: group.id, label: group.label, items });
	}

	return out;
}

export function footerPages(
	role: string | undefined,
	availability: ModuleAvailability,
	visibility: NavigationVisibility = {}
): NavPage[] {
	return PAGES.filter(
		(p) => p.group === 'personal' && canViewPage(p, role, availability, visibility)
	);
}

// ── palette ─────────────────────────────────────────────────────────────

export interface PaletteEntry {
	href: string;
	label: string;
	group: string;
	keywords: string;
	/** Create actions land on the page with the new-item modal already open. */
	isAction?: boolean;
}

const GROUP_LABEL: Record<NavGroupId, string> = {
	main: 'Go to',
	operate: 'Operate',
	build: 'Build',
	agent: 'Intelligence',
	connect: 'Integrate',
	govern: 'Govern',
	resources: 'Help',
	personal: 'Personal'
};

export function paletteEntries(
	role: string | undefined,
	availability: ModuleAvailability,
	visibility: NavigationVisibility = {}
): PaletteEntry[] {
	const nav: PaletteEntry[] = [];
	const actions: PaletteEntry[] = [];

	for (const page of PAGES) {
		if (!canViewPage(page, role, availability, visibility)) continue;

		const sectionLabel = page.section ? BY_SECTION.get(page.section)?.label : undefined;
		nav.push({
			href: page.href,
			label: sectionLabel ? `${sectionLabel} · ${page.label}` : page.label,
			group: GROUP_LABEL[page.group],
			keywords: `${page.desc} ${page.keywords ?? ''} ${page.href}`
		});

		if (page.action && canSee(role, page.action.minRole ?? page.minRole)) {
			actions.push({
				href: `${page.href}?new=1`,
				label: page.action.label,
				group: 'Actions',
				keywords: `${page.action.keywords ?? ''} ${page.label} new create add`,
				isAction: true
			});
		}
	}

	// Actions first: someone who opens the palette to *do* something should not
	// scroll past thirty destinations to find it.
	return [...actions, ...nav];
}

// ── hub ─────────────────────────────────────────────────────────────────

export interface HubGroup {
	label: string;
	pages: NavPage[];
}

/**
 * The admin index. Same grouping as the sidebar on purpose — one mental map, not
 * two — minus what the sidebar footer and Chat already cover.
 */
export function hubGroups(
	role: string | undefined,
	availability: ModuleAvailability,
	visibility: NavigationVisibility = {}
): HubGroup[] {
	const order: NavGroupId[] = ['agent', 'build', 'connect', 'operate', 'govern', 'resources'];
	return order
		.map((id) => ({
			label: GROUP_LABEL[id],
			pages: PAGES.filter(
				(p) => p.group === id && p.hub !== false && canViewPage(p, role, availability, visibility)
			)
		}))
		.filter((g) => g.pages.length > 0);
}
