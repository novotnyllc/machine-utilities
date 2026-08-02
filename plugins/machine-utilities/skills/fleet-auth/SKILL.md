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
Read `"$SKILL_DIR/../../references/agent-settings-and-auth.md"` for the distinct
Claude CLI/Desktop and Codex credential boundaries.

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
For a local `encrypted-install`, use
`"$CLI" apply-plan PLAN PLAN-ID OUTPUT`; it resolves the configured `op://`
reference directly into a private same-directory temporary file, replaces
atomically, verifies, and rolls back on failure. Remote targets over SSH use
`"$CLI" apply-ssh-plan PLAN PLAN-ID OUTPUT`. Apply recaptures trusted preflight
itself and preserves authoritative partial output after a failed operation or
postcondition when post-inventory remains available. SSH uses bounded
connection/keepalive timeouts and verifies the configured native hostname/user.
Native Windows auth mutation is not supported by this release; reauthenticate
interactively on that host rather than copying credentials through a task.
SSH reauthentication is also rejected because login needs a visible interactive
terminal or browser. Complete the one-time login on the target, then re-run
inventory; `apply-ssh-plan` remains available for noninteractive encrypted
installs.

Pathless `per-machine` or `native-store` artifacts are status-only sessions;
inventory does not infer their credential backend. Their status comes only from
the configured native verification command and an unhealthy login is marked
as `reauth_required` with a manual host action. A full Claude login is required
once per Remote Control host; do not substitute a setup token or copy Claude
state. Codex file-backed `auth.json` is portable only when the user explicitly
chooses `encrypted-install`; otherwise keep Codex authentication per-machine.

Matching SHA-256 proves identical bytes, not valid authentication. Prefer
per-machine least-privilege credentials for unattended work. For Windows,
Codex uses a visible saved-project task as described in
`"$SKILL_DIR/../../references/codex-remote-control.md"`; Claude reports
unsupported. Never route secrets through WSL or another machine as a bridge.
