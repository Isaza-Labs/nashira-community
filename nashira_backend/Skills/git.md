# Skill: git — repositories, branches, and GitHub

Nashira keeps a server-side working copy of each registered repository. The git tools
operate on that copy, not on a user's machine, so a checkout in one turn is what every
later read and write in that turn sees.

Reads: `git_list_repositories`, `git_list_files`, `git_read_file`, `git_status`,
`git_diff`, `git_list_branches`, `git_list_webhooks`. Writes: `git_pull`,
`git_checkout`, `git_write_file`, `git_commit_push`, `git_create_webhook`. GitHub API:
`github_create_repo`, `github_create_pr`, `github_merge_pr`. Spec: `na_git`.

## Two surfaces, and picking the wrong one is the common mistake

| Surface | Runs | Use when |
|---|---|---|
| the `git_*` tools below | in this turn, driven by you | the user is asking for a read, an edit or a repo **right now**, in the conversation |
| the `git` workflow node | inside a workflow run, on the worker | the read or write has to be part of something repeatable, scheduled, or triggered by a push |

Same engine underneath — same repositories, same credentials, same semantics — so a
workflow that needs Git does **not** come back to the chat. Drop a `git` node in the
DAG. Telling the user "the workflow can't touch Git, I'll do the commit myself" is
wrong, and it turns automation they asked for into a chore you have to be present for.
The `python_snippet` sandbox blocking `open` and `requests` is about the snippet
runtime; it is not a statement about what a workflow can reach.

## Always start with the repository id

`git_list_repositories` returns `id`, `name`, `url`, `default_branch`. Every other git
tool needs that id. A repository name the user typed is not an id, and guessing wastes
the turn.

The same applies to credentials: `list_credentials` returns `credential_id` alongside
the name, and that id is what the `github_*` tools take. Filter it with
`auth_method: "token"` or `name_contains` when the user refers to a credential by name.
Those tools also accept `credential_name` directly — use it only when you have a name
and no id; passing the id is unambiguous and cannot match the wrong row.

## The order that avoids surprises

1. `git_status` — current branch, clean or not, staged / modified / untracked / deleted
   paths, and how far ahead or behind upstream. **Check this before committing**: the
   commit stages everything unless you give explicit paths, so an unrelated modified file
   in the working copy lands in your commit.
2. `git_checkout` if you need a different branch. It is a write and is confirmed,
   because it changes what everything after it sees.
3. `git_pull` — fetch and fast-forward. Do it before writing on a branch that is behind;
   committing on a stale copy is how you get a conflict later that nobody expected.
4. `git_read_file` / `git_write_file`. **`git_write_file` overwrites the whole file with
   the content you supply.** Read it first and send the full modified text — there is no
   patch mode, and a partial body silently truncates the file.
5. `git_diff` — with no `from`/`to`, HEAD vs the working tree, i.e. exactly what is about
   to be committed. Show it to the user before a commit they will not be able to inspect
   afterwards.
6. `git_commit_push`.

`git_read_file` returns UTF-8 text, or base64 with `is_binary: true`. Do not paste binary
content into the conversation.

## GitHub

These reach outside the system:

- `github_create_repo` uses a stored token credential, creates the repository, and
  registers it here in the same call (`register: true` by default). It returns
  `repository_id` — that is what you pass to `git_write_file` next, so "create a repo
  and put a file in it" is two tool calls, not a dead end. If the registration fails
  (a name already taken locally, for instance) the GitHub repo still exists: the result
  carries `registered: false` and `register_error`, and the user registers it by hand
  from **Git → New repository**.
- `github_create_pr` needs the head branch to exist **on the remote** — push first
  (`git_commit_push` with push enabled), otherwise the PR call fails on a branch GitHub
  has never seen.
- `github_merge_pr` lands code on the base branch. It is the one git operation that
  cannot be undone by deleting an object, so it needs elevated confirmation. Before
  calling it, confirm the PR is reviewed and its checks pass — and say what you checked.

## Recipe: "create a repo called X and add a README"

The most common git request in chat. Do not ask the user for a URL or an owner — the
repo does not exist yet, GitHub returns the URL, and the owner defaults to the token's
own account.

1. `list_credentials` with `auth_method: "token"` — find the credential and read its
   `credential_id`. Autonomous, so it can run before you propose anything.
2. Propose one plan covering both writes, then on "yes":
   - `github_create_repo(name, credential_id)` — private, `auto_init: true`, registered
     here. Returns `repository_id`.
   - `git_write_file(repository_id, path, content, commit_message, push: true)`.
3. Report `html_url` and `repository_id` back.

If the create fails because the name is taken on that account, say so verbatim and ask
whether to pick another name or use the existing repository.

## The `git` workflow node

`git` snippets take their configuration from the node's `config_overrides`, resolved
before the step runs, so `{{ steps.x.output.y }}` is already a value by the time git
sees it. `operation` and `repository_id` are always required.

| operation | needs | gives back |
|---|---|---|
| `read_file` | `path`, optional `ref` | `content`, `size`, `is_binary` |
| `write_file` | `path`, `content`, `commit_message`, optional `branch` / `push` | `commit_sha`, `branch` |
| `commit` | `commit_message`, optional `paths` / `push` | `commit_sha`, `branch` |
| `pull` / `push` | optional `branch` | `commit_sha`, `branch` |
| `list_files` | optional `path`, `ref` | `entries`, `count` |
| `status` | — | `clean`, `staged`, `modified`, `untracked`, `ahead`, `behind` |
| `diff` | optional `from`, `to`, `path` | `patch`, `has_changes` |

`has_changes` on `diff` is there so a condition edge can ask "did anything change"
without parsing a patch. Baseline snippets exist for the three common shapes —
`baseline-git-read-file`, `baseline-git-write-file`, `baseline-git-pull` — so a node
has something legal to point at; `create_snippet` covers the rest.

A `git` node is `requires_compensation`, not `non_reversible`: the compensation for a
push is a revert commit, and the workflow author wires it on the failure edge. Say that
when you propose a workflow that pushes — a rollback plan that nobody wrote is not a
rollback plan.

## Webhooks: making a push start something

`git_create_webhook` registers an inbound receiver on a repository. A verified push
pulls the working copy (`auto_pull`, on by default) and, if `on_push_workflow_id` is
set, enqueues a run of that workflow with `input` = `{ repository_id, branch,
commit_sha, provider, git_webhook_id }`.

What the tool cannot do is the half that happens on GitHub. It returns `ingest_path`,
`signature_header` and `secret`; the user pastes the URL (this server's public origin
plus that path) and the secret into the repository's webhook settings. **The secret is
returned once and nothing can read it back** — put it in the same reply, and say it
cannot be retrieved later, only rotated.

Two things to get right when you propose one:

- `on_push_branches` is exact, literal branch names, and empty means every branch. A
  tag push carries no branch at all, so a filtered webhook ignores tags.
- Without a secret the receiver refuses every delivery. That is deliberate: an unsigned
  route is an unauthenticated way to run a workflow. `allow_unsigned` exists for an
  emitter that genuinely cannot sign, and it is a decision the user makes out loud.

When a user says a push "did nothing", read `git_list_webhooks` before agreeing.
`fires_for` and `last_delivery_status` answer it almost every time, and the usual
answer is a branch filter rather than a broken hook.

## Things worth saying out loud

- Name the repository, the branch and the paths before any write. "Committing to the
  repo" is not enough for someone to catch your mistake in time.
- A push is visible to everyone with access to that remote. Treat it as publishing.
- Never commit a secret, a token or a credential body into a repository, even when the
  user asks — say so and offer the secret store instead.
