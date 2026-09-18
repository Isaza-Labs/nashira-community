import type { DocSection } from '../types';

export const governance: DocSection[] = [
	{
		slug: 'policies',
		title: 'Policies',
		group: 'Governance',
		module: 'governance',
		tagline: 'Default-allow guardrails: deny at run time, gate at promotion.',
		uiPath: '/admin/policies',
		apiBase: '/api/policies',
		role: 'Admin',
		purpose:
			'Encode the rules a team already has — "core routers only inside a change window", "production needs clean qa runs first" — so they are enforced by the engine instead of remembered by whoever is on shift.',
		keywords: 'guardrails deny gate rules compliance change window governance dry run',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Govern → Policies**.' },
			{
				kind: 'note',
				tone: 'primary',
				title: 'Default allow, opt-in deny',
				text: 'The model people already hold for firewalls, and it has the property that a policy nobody wrote cannot accidentally block work. There is no allow action, because mixing allow and deny needs precedence rules — and precedence is where guardrails start being misread.'
			},
			{ kind: 'heading', text: 'Two shapes' },
			{
				kind: 'code',
				caption: 'deny — evaluated before a run',
				text: `{
  "action": "deny",
  "reason": "core routers need a change window",
  "when": {
    "environment": ["production", "qa"],
    "device_role": ["core"],
    "device_pool": ["core-routers"],
    "snippet_type": ["ssh"],
    "description_contains": ["bgp"]
  }
}`
			},
			{
				kind: 'code',
				caption: 'gate — evaluated before a promotion',
				text: `{
  "action": "gate",
  "reason": "three clean qa runs in the last two weeks, one of them this week",
  "on": "promote",
  "from": "qa",
  "to": "production",
  "require": [
    { "type": "successful_runs", "min": 3, "within_days": 14, "scope": "this_workflow" },
    { "type": "last_successful_run_within", "days": 7 }
  ]
}`
			},
			{
				kind: 'prose',
				text: 'A **deny** asks "is this forbidden?"; a **gate** asks "has this earned it yet?" — the difference matters because a gate is satisfiable by doing the work, so it is a precondition rather than a prohibition. A gate refusal names every unmet requirement at once, so you do not discover them one promotion at a time.'
			},
			{
				kind: 'list',
				items: [
					'`successful_runs` counts runs whose status is `completed` — a failed or rolled-back run never counts. `within_days` is optional and narrows that count to a horizon; without it, three runs from a year ago satisfy it.',
					'`scope` is `this_workflow` (the row being promoted — the qa row for qa → production, not the whole lineage) or `any_workflow` (anything in the source environment). Defaults to `this_workflow`.',
					'`on`, `from` and `to` are optional; an absent one matches any transition, and each one present narrows the gate.',
					'`last_successful_run_within` takes `days` and the same `scope`.'
				]
			},
			{ kind: 'heading', text: 'Building a rule' },
			{
				kind: 'prose',
				text: 'The editor has a **Visual** tab and a **JSON** tab over the same document, so either can be used at any point without the two drifting. The visual builder offers only clauses this build can evaluate, and picks device roles, pools and snippet types from what actually exists — a rule naming a role nobody uses never matches, and a policy that never matches is indistinguishable from no policy. A rule containing something the builder cannot represent says so and leaves it alone until you edit on the visual tab.'
			},
			{ kind: 'heading', text: 'How `when` matches' },
			{
				kind: 'list',
				items: [
					'Every clause present must match (**AND**); a clause matches if **any** of its values does (**OR**).',
					'Adding a clause therefore always narrows a rule — a policy cannot become broader by being made more specific.',
					'`device_pool` matches by pool name and resolves membership through the pool resolver, so rule-based pools count, not just static lists.',
					'`snippet_type` is drawn from the workflow\'s graph, so a rule about `ssh` fires on any workflow containing an SSH step.'
				]
			},
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Named in refusals, so make it the sentence you want an operator to read.' },
					{ name: 'description', type: 'string | null', desc: 'Background for whoever inherits it.' },
					{ name: 'rule', type: 'object', required: true, desc: 'The document above. Kept as a document so its shape can grow without a migration.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Suspend a guardrail during an incident without losing its definition. Distinct from deletion. The API arms a policy when `enabled` is omitted; the console\'s New policy form starts unarmed.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'A broken rule blocks; a rule that does not match does not',
				text: 'A rule whose JSON does not parse blocks every run and every promotion, and names itself — a guardrail that silently stops guarding is worse than one that stops work. Anything else that does not match simply does not fire: a role, pool or snippet type nobody uses never matches. In a gate, an unknown requirement `type`, an unknown `scope` or a malformed `min`/`days` counts as unmet. And a `deny` with no `when` denies **every** run — that is how a freeze is written.'
			},
			{
				kind: 'values',
				title: 'What a refusal looks like',
				rows: [
					{ value: 'run denied', desc: '403 `blocked by policy \'<name>\': <reason>`. The refusal is kept as a run with `final_state: refused` and the message in `error`, so it shows up in Runs (best effort — if that row cannot be written, the 403 still stands).' },
					{ value: 'promotion gated', desc: '412 `policy_blocked`, naming the policy and its reason.' },
					{ value: 'bad write', desc: '400 when `rule` is not a JSON object or its `action` is neither `deny` nor `gate`; 409 `policy_name_taken` when an active policy already has the name.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/policies', role: 'Admin', desc: 'List.' },
					{ method: 'GET', path: '/api/policies/{id}', role: 'Admin', desc: 'One policy.' },
					{ method: 'POST', path: '/api/policies', role: 'Admin', desc: 'Create. **Armed at once unless the body says `enabled: false`** — pass it to stage a rule and dry-run it with `/evaluate` first. The console form starts unarmed on purpose: a guardrail first used in production is one nobody read carefully.' },
					{ method: 'PUT', path: '/api/policies/{id}', role: 'Admin', desc: 'Update, including arming and disarming it.' },
					{ method: 'DELETE', path: '/api/policies/{id}', role: 'Admin', desc: 'Soft delete.' },
					{ method: 'POST', path: '/api/policies/evaluate', role: 'Admin', desc: 'Dry run: "would this rule block that run?" before arming it. Body accepts `rule`, `environment`, `workflow_description`, `device_roles`, `device_pools`, `snippet_types`; omit `rule` to test the enabled policies against that context instead. Only `deny` rules are evaluated here — a `gate` always answers not denied, since gates are checked at promotion. Returns `{ denied, policy, reason }`.' }
				]
			}
		]
	}
];
