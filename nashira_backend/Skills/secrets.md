# Skill: secrets and credentials — reusable sensitive material

Tools: `list_credentials`, `create_credential`, `update_credential`, `delete_credential`,
`list_secrets`, `set_secret`, `clear_secret`.

A **credential** is reusable authentication material such as a password, token, API key,
OAuth2 client, or SSH private key. A **secret** is one named value, referenced from
configuration as `${secret:secret:<name>:value}` and resolved at call time. Storing a token as a
secret does not connect it to another object by itself; that object's configuration must
reference it.

In the web UI both are entries in one screen, the **Vault** (Operate → Vault): a credential
is a Login, SSH key, Access token, API key or OAuth2 client entry, and a secret is a Secret
value entry. They remain two stores with the two sets of tools above; send a user who asks
where to add either one to the Vault.

Never echo sensitive values. Reads return presence flags instead of material precisely so a
secret never has to enter the transcript. Creating, updating or deleting one is a sensitive
mutation: explain what reference will change and obtain confirmation first.
