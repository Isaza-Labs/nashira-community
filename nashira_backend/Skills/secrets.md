# Skill: secrets and credentials — reusable sensitive material

Tools: `list_credentials`, `create_credential`, `update_credential`, `delete_credential`,
`list_secrets`, `set_secret`, `clear_secret`.

A **credential** is reusable authentication material such as a password, token, API key,
OAuth2 client, or SSH private key. A **secret** is a `provider`/`key` value referenced from
configuration as `${secret:provider:key}` and resolved at call time. Storing a token as a
secret does not connect it to another object by itself; that object's configuration must
reference it.

Never echo sensitive values. Reads return presence flags instead of material precisely so a
secret never has to enter the transcript. Creating, updating or deleting one is a sensitive
mutation: explain what reference will change and obtain confirmation first.
