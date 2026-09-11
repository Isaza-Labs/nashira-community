// Guards the copy of the password policy the form needs in order to show the rules
// while they are being typed.
//
// The server is the authority (Services/Auth/PasswordPolicy.cs, configured by
// Configuration/AuthOptions.cs) and its own comment asks for a frontend mirror. A
// mirror that drifts is worse than none: the form would promise a password is
// acceptable and the API would reject it, with the rejection landing in a modal the
// user has already been told is fine.
//
// Run with `npm run check:password`. No dependencies — plain node.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const options = readFileSync(
	join(here, '..', '..', 'nashira_backend', 'Configuration', 'AuthOptions.cs'),
	'utf8'
);
const mirror = readFileSync(join(here, '..', 'src', 'lib', 'auth', 'password.ts'), 'utf8');

const problems = [];

// Each server rule and the token that must be present in the mirror for it.
const RULES = [
	{ csharp: 'RequireUpper', mirror: /const UPPER =/, label: 'uppercase check' },
	{ csharp: 'RequireLower', mirror: /const LOWER =/, label: 'lowercase check' },
	{ csharp: 'RequireDigit', mirror: /const DIGIT =/, label: 'digit check' },
	{ csharp: 'RequireSymbol', mirror: /const SYMBOL =/, label: 'symbol check' }
];

const serverMin = options.match(/int MinLength \{ get; set; \} = (\d+);/)?.[1];
const clientMin = mirror.match(/PASSWORD_MIN_LENGTH = (\d+);/)?.[1];

if (!serverMin) problems.push('could not read MinLength from AuthOptions.cs');
if (!clientMin) problems.push('could not read PASSWORD_MIN_LENGTH from password.ts');
if (serverMin && clientMin && serverMin !== clientMin) {
	problems.push(
		`minimum length differs: AuthOptions.cs says ${serverMin}, password.ts says ${clientMin}`
	);
}

for (const rule of RULES) {
	const enabled = new RegExp(`bool ${rule.csharp} \\{ get; set; \\} = (true|false);`).exec(options)?.[1];
	if (!enabled) {
		problems.push(`could not read ${rule.csharp} from AuthOptions.cs`);
		continue;
	}
	const mirrored = rule.mirror.test(mirror);
	if (enabled === 'true' && !mirrored) {
		problems.push(`${rule.csharp} is enabled on the server but password.ts has no ${rule.label}`);
	}
	if (enabled === 'false' && mirrored) {
		problems.push(`${rule.csharp} is disabled on the server but password.ts still enforces it`);
	}
}

// The server also refuses a password containing the username; the mirror shows it as a
// rule, so losing it would quietly make the form more permissive than the API.
if (!/must not contain the username/i.test(mirror)) {
	problems.push('password.ts no longer mirrors the "must not contain the username" rule');
}

if (problems.length > 0) {
	console.error('Password policy mirror is out of sync with the backend:\n');
	for (const p of problems) console.error(`  - ${p}`);
	console.error('\nUpdate frontend/src/lib/auth/password.ts to match.');
	process.exit(1);
}

const active = RULES.filter((r) =>
	new RegExp(`bool ${r.csharp} \\{ get; set; \\} = true;`).test(options)
).length;
console.log(`Password policy OK: min ${serverMin} chars, ${active} character-class rules, username rule.`);
