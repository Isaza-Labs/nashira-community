// The kinds of entry the Vault holds, and how each maps onto the two stores behind
// it. Five kinds are credentials (a structured login, told apart by its auth
// method); the sixth is a secret (one named value referenced from configuration).
// The stores and their APIs are unchanged — this is only the vocabulary the Vault
// screen uses, so that "what am I storing?" is one question with six answers
// instead of two menu entries and a method dropdown.

import type { AuthMethod, Credential } from '$lib/api/credentials.api';
import { FileKey, Fingerprint, KeyRound, Lock, ShieldCheck, UserRound } from 'lucide-svelte';

export type VaultKind = 'login' | 'ssh_key' | 'token' | 'api_key' | 'oauth2' | 'secret';

export interface VaultKindInfo {
	kind: VaultKind;
	/** Singular, as it appears on a badge and in the type picker. */
	label: string;
	/** What it is for, in one line — shown in the type picker. */
	desc: string;
	icon: typeof Lock;
	/** The credential auth method behind it; absent for a secret value. */
	authMethod?: AuthMethod;
	/**
	 * The tag a new credential of this kind starts with, shown in the form so the
	 * admin sees what will be stored. The server tags an untagged credential "ssh",
	 * which is right for a login or key and wrong for a token or an API key.
	 */
	defaultTag?: string;
}

export const VAULT_KINDS: VaultKindInfo[] = [
	{
		kind: 'login',
		label: 'Login',
		desc: 'A username and password — devices over SSH, basic-auth APIs.',
		icon: UserRound,
		authMethod: 'password',
		defaultTag: 'ssh'
	},
	{
		kind: 'ssh_key',
		label: 'SSH key',
		desc: 'A username with a private key, and its passphrase if it has one.',
		icon: FileKey,
		authMethod: 'key',
		defaultTag: 'ssh'
	},
	{
		kind: 'token',
		label: 'Access token',
		desc: 'A bearer or personal access token — git hosts, token-auth APIs.',
		icon: Fingerprint,
		authMethod: 'token',
		defaultTag: 'token'
	},
	{
		kind: 'api_key',
		label: 'API key',
		desc: 'A key sent in a request header you name.',
		icon: KeyRound,
		authMethod: 'api_key',
		defaultTag: 'api_key'
	},
	{
		kind: 'oauth2',
		label: 'OAuth2 client',
		desc: 'Client ID, client secret and token URL (client credentials).',
		icon: ShieldCheck,
		authMethod: 'oauth2',
		defaultTag: 'oauth2'
	},
	{
		kind: 'secret',
		label: 'Secret value',
		desc: 'One named value that configuration points at with a ${secret:…} reference.',
		icon: Lock
	}
];

const BY_KIND = new Map(VAULT_KINDS.map((k) => [k.kind, k]));
const BY_METHOD = new Map(VAULT_KINDS.filter((k) => k.authMethod).map((k) => [k.authMethod, k.kind]));

export function kindInfo(kind: VaultKind): VaultKindInfo {
	return BY_KIND.get(kind)!;
}

/**
 * The Vault kind of a stored credential.
 *
 * The method is trimmed and lowercased first, exactly as the server reads it
 * (CredentialRules.Normalize): rows written through older paths hold "KEY",
 * " token " or "OAuth2", and reading those as a login would send
 * auth_method "password" back on the next save and break their authentication.
 * Only an empty or genuinely unknown method reads as a login — which is also
 * what the server does with it.
 */
export function kindOfCredential(c: Pick<Credential, 'authMethod'>): VaultKind {
	const method = (c.authMethod ?? '').trim().toLowerCase();
	return BY_METHOD.get(method as AuthMethod) ?? 'login';
}

/**
 * The kind's label as it reads mid-sentence ("New login", "New API key"): only a
 * leading capital that starts an ordinary word is lowered, so the acronyms SSH,
 * API and OAuth2 keep their capitals.
 */
export function kindNoun(kind: VaultKind): string {
	const label = kindInfo(kind).label;
	return /^[A-Z][a-z]/.test(label) ? label[0].toLowerCase() + label.slice(1) : label;
}

export function isVaultKind(value: string | null | undefined): value is VaultKind {
	return !!value && BY_KIND.has(value as VaultKind);
}
