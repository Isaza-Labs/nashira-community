// Mirror of the server's PasswordPolicy (Services/Auth/PasswordPolicy.cs), whose own
// comment asks for exactly this: "Keep in sync with the frontend form validator."
//
// The server is still the authority — this exists so the rules are visible while the
// password is being typed, instead of arriving as a rejection after the form is
// submitted. Same rules, same order, same wording, so a client-side message and a
// server-side one never disagree.
//
// Values track Configuration/AuthOptions.cs → PasswordPolicyOptions defaults.

export const PASSWORD_MIN_LENGTH = 12;

export interface PasswordCheck {
	label: string;
	ok: boolean;
}

// Unicode-aware to match .NET's char.IsUpper / IsLower / IsDigit / IsLetterOrDigit:
// the server treats any character that is neither a letter nor a number as a symbol.
const UPPER = /\p{Lu}/u;
const LOWER = /\p{Ll}/u;
const DIGIT = /\p{Nd}/u;
const SYMBOL = /[^\p{L}\p{N}]/u;

// Every rule with its current state, for the live checklist under the field.
export function passwordChecks(password: string, username?: string): PasswordCheck[] {
	const checks: PasswordCheck[] = [
		{ label: `At least ${PASSWORD_MIN_LENGTH} characters`, ok: password.length >= PASSWORD_MIN_LENGTH },
		{ label: 'An uppercase letter', ok: UPPER.test(password) },
		{ label: 'A lowercase letter', ok: LOWER.test(password) },
		{ label: 'A digit', ok: DIGIT.test(password) },
		{ label: 'A symbol', ok: SYMBOL.test(password) }
	];

	// Only worth showing once there is a username to collide with.
	const name = username?.trim();
	if (name) {
		checks.push({
			label: 'Does not contain the username',
			ok: !password.toLowerCase().includes(name.toLowerCase())
		});
	}

	return checks;
}

// The first unmet rule, worded as the server words it, or null when the password passes.
export function passwordPolicyError(password: string, username?: string): string | null {
	if (password.length < PASSWORD_MIN_LENGTH)
		return `Password must be at least ${PASSWORD_MIN_LENGTH} characters.`;
	if (!UPPER.test(password)) return 'Password must contain an uppercase letter.';
	if (!LOWER.test(password)) return 'Password must contain a lowercase letter.';
	if (!DIGIT.test(password)) return 'Password must contain a digit.';
	if (!SYMBOL.test(password)) return 'Password must contain a symbol.';

	const name = username?.trim();
	if (name && password.toLowerCase().includes(name.toLowerCase()))
		return 'Password must not contain the username.';

	return null;
}

export function passwordMeetsPolicy(password: string, username?: string): boolean {
	return passwordPolicyError(password, username) === null;
}
