<script lang="ts">
	import { Input, Select, Textarea } from '$lib/components/ui';
	import { authMethods, fieldsFor, type AuthDialect, type AuthDraft } from '$lib/auth/draft';

	// The scheme select plus exactly the inputs that scheme uses — the pattern
	// /admin/credentials already applies to stored credentials, generalized over the two
	// wire formats. `dialect` matters because the same idea is spelled differently on
	// each backend, so a single field set would send the wrong keys to one of them.
	//
	// `shapeOnly` renders the non-secret knobs alone: when a stored credential supplies
	// the material, the only thing left to choose is how it is sent.
	let {
		dialect,
		method = $bindable('none'),
		draft = $bindable<AuthDraft>(),
		editing = false,
		shapeOnly = false
	}: {
		dialect: AuthDialect;
		method?: string;
		draft?: AuthDraft;
		editing?: boolean;
		shapeOnly?: boolean;
	} = $props();

	const methods = $derived(authMethods(dialect));
	const options = $derived(methods.map((m) => ({ value: m.value, label: m.label })));
	const selected = $derived(methods.find((m) => m.value === method));
	const fields = $derived(fieldsFor(dialect, method));

	// On edit the API cannot echo the stored material back, so a blank field means
	// "keep it" rather than "clear it".
	const keepHint = $derived(editing ? 'Leave blank to keep the stored value' : '');
</script>

<Select label="Authentication" bind:value={method} {options} hint={selected?.hint ?? ''} />

{#if draft}
	{#if fields.includes('token')}
		{#if !shapeOnly}
			<Input
				label={method === 'api_key' ? 'API key' : 'Token'}
				type="password"
				bind:value={draft.token}
				hint={keepHint ||
					'May be a ${secret:secret:<name>:value} reference resolved when the request is sent'}
			/>
		{/if}
	{/if}

	{#if fields.includes('prefix')}
		<Input
			label="Authorization scheme"
			bind:value={draft.prefix}
			placeholder="Token"
			hint="Sent as `Authorization: <scheme> <token>`. Blank means Token."
		/>
	{/if}

	{#if fields.includes('header')}
		<Input
			label="Header name"
			bind:value={draft.header}
			placeholder="X-API-Key"
			hint="Which header carries the key. Blank means X-API-Key."
		/>
	{/if}

	{#if fields.includes('username')}
		<Input label="Username" bind:value={draft.username} />
	{/if}

	{#if fields.includes('password') && !shapeOnly}
		<Input label="Password" type="password" bind:value={draft.password} hint={keepHint} />
	{/if}

	{#if fields.includes('secretHeaders') && !shapeOnly}
		<Textarea
			label="Secret headers"
			bind:value={draft.secretHeaders}
			rows={3}
			placeholder={'X-Api-Token: abc123\nX-Tenant: acme'}
			hint={keepHint || 'One `Name: value` per line'}
		/>
	{/if}

	{#if fields.includes('tokenUrl')}
		<Input
			label="Token URL"
			bind:value={draft.tokenUrl}
			placeholder="https://idp.example.com/oauth2/token"
			hint={method === 'oauth_authorization_code' ? 'Leave blank to auto-discover from the server.' : ''}
		/>
	{/if}

	{#if fields.includes('authorizationEndpoint')}
		<Input
			label="Authorization endpoint"
			bind:value={draft.authorizationEndpoint}
			hint="Leave blank to auto-discover."
		/>
	{/if}

	{#if fields.includes('redirectUri')}
		<Input
			label="Redirect URI"
			bind:value={draft.redirectUri}
			hint="Leave blank to use this backend's callback."
		/>
	{/if}

	{#if fields.includes('clientId')}
		<Input
			label="Client ID"
			bind:value={draft.clientId}
			hint={method === 'oauth_authorization_code'
				? 'Optional if the server supports dynamic client registration.'
				: ''}
		/>
	{/if}

	{#if fields.includes('clientSecret') && !shapeOnly}
		<Input
			label="Client secret"
			type="password"
			bind:value={draft.clientSecret}
			hint={keepHint ||
				(method === 'oauth_authorization_code'
					? 'Optional if the server supports dynamic client registration.'
					: '')}
		/>
	{/if}

	{#if fields.includes('scope')}
		<Input
			label="Scopes"
			bind:value={draft.scope}
			hint="Space-separated, e.g. read:devices write:configs"
		/>
	{/if}
{/if}
