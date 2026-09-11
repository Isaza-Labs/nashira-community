# Skill: git — direct repository operations

Use the Git tools for repository work requested in the current conversation.

Reads: `git_list_repositories`, `git_list_files`, `git_read_file`, `git_status`,
`git_diff`, `git_list_branches`. Writes: `git_pull`, `git_checkout`,
`git_write_file`, `git_commit_push`. GitHub operations are
`github_create_repo`, `github_create_pr` and `github_merge_pr`.

Start with `git_list_repositories`; every later operation needs the exact
`repository_id`. Before editing, read the file and check `git_status`. Before
committing, inspect `git_diff`. Keep the commit message specific and never put a
secret, token or credential body in repository content.

`git_checkout`, `git_pull`, `git_write_file` and `git_commit_push` mutate the
working copy and require confirmation. A pull refuses to discard local work.
`git_commit_push` stages the requested paths, creates one commit and optionally
pushes it; report the resulting commit SHA.

Only propose operations exposed by the current tool list. Capabilities owned by
other modules may be absent from this deployment.
