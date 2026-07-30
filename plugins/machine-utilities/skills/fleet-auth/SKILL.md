---
name: fleet-auth
description: Audit and deliberately reconcile configured authentication artifacts across machines without exposing secrets. Use for Codex auth.json, xurl auth.yml, GitHub or tool sessions, matching credential hashes, native auth health, reauthentication, or encrypted credential installation.
---

# Fleet Authentication

Set `SKILL_DIR` to the absolute directory containing this loaded `SKILL.md` and
`CLI="$SKILL_DIR/../../scripts/machine-utilities"`; the shell working directory
is not the skill directory. Run the `auth` inventory section first. Show
configured artifact name, path, strategy, owner, mode, size, mtime, SHA-256,
link status, and native verification result. Do not read credential contents
into the conversation, logs, JSONL, command arguments, or task prompts.

Honor each configured strategy:

- `chezmoi`: delegate declarative state to `fleet-chezmoi`.
- `reauth`: run the tool's native login on that machine.
- `encrypted-install`: resolve the configured secret reference only after the
  user approves exact source, target hosts, and destination path.
- `ignore`: report state and make no change.

Before mutation, seal the exact artifact operation with
`"$CLI" seal-plan DRAFT SNAPSHOT PLAN`, verify live host identity, recapture
auth inventory, and require
`"$CLI" verify-preconditions PLAN CURRENT-SNAPSHOT` to succeed. Obtain
separate user approval for the exact sealed operation. Reject symlink
destinations and capture current metadata for rollback. Fetch the secret
directly into a mode-0600 temporary file on the target, validate type/size,
then atomically rename it into the user-owned parent directory. Never copy
through a world-readable location.
Run the configured native verification command; on failure restore the prior
file or remove the new file.

Matching SHA-256 proves identical bytes, not valid authentication. Prefer
per-machine least-privilege credentials for unattended work. For Windows,
Codex uses a visible saved-project task as described in
`"$SKILL_DIR/../../references/codex-remote-control.md"`; Claude reports
unsupported. Never route secrets through WSL or another machine as a bridge.
