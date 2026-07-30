---
name: fleet-agents
description: Inventory and reconcile Codex, Claude Code, plugins, standalone skills, skills-cli provenance, JSM-managed skills, and logical capabilities across machines. Use when agent tooling or skills are missing, duplicated, stale, or inconsistent.
---

# Fleet Agents

Set `SKILL_DIR` to the absolute directory containing this loaded `SKILL.md` and
`CLI="$SKILL_DIR/../../scripts/machine-utilities"`; the shell working directory
is not the skill directory. Collect the `agents` section with `"$CLI"`. Compare
runtime versions, plugin name/version/manager, standalone skill hashes and
origins, skills-cli lock metadata, JSM provenance, and configured logical
capabilities. Treat the same capability delivered as a plugin and a standalone
skill as equivalent only when the config says so; report duplicate providers.

Use manager-native ownership:

- Codex plugins: use Codex marketplace/plugin commands.
- Claude plugins: use Claude marketplace/plugin commands.
- skills-cli installs: use `npx skills check` or `npx skills update` and retain
  `.agents/.skill-lock.json` provenance.
- JSM installs: use JSM for inventory/update when its metadata proves ownership.
- local source skills: update their owning repository; do not overwrite them
  with a package manager.

Default to a plan listing exact host, harness, manager, source, current version
or hash, and desired action. Seal it with
`"$CLI" seal-plan DRAFT SNAPSHOT PLAN`. Before apply, require exact scope,
recapture inventory, and require
`"$CLI" verify-preconditions PLAN CURRENT-SNAPSHOT` to succeed. Obtain
separate user approval and execute only the exact sealed argv. Do not silently
convert a standalone skill into a plugin or vice versa. For a local target use
`"$CLI" apply-plan PLAN PLAN-ID OUTPUT`; for SSH use
`"$CLI" apply-ssh-plan PLAN PLAN-ID OUTPUT`; Windows uses the native worker
contract in the remote-control reference. Apply recaptures trusted preflight
itself. Preserve its authoritative partial output when an operation or
postcondition fails.
The executor supports exact updates for skills-cli, JSM, and Claude plugins.
Codex replacement uses the native idempotent `codex plugin add
PLUGIN@MARKETPLACE --json` operation. Installs, removals, and provider
conversion are unsupported and fail before plan sealing.

Use local/SSH execution where configured. SSH uses bounded connection and
keepalive timeouts and must match the configured native hostname/user before
mutation. For Windows Codex tasks read and follow
`"$SKILL_DIR/../../references/codex-remote-control.md"`; Claude reports
unsupported rather than using WSL. Re-inventory after changes and report
unresolved provenance as unknown, not guessed.
