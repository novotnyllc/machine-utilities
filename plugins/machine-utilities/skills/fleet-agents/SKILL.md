---
name: fleet-agents
description: Inventory and reconcile Codex and Claude runtimes, safe settings, plugins, standalone skills, skills-cli provenance, JSM-managed skills, and logical capabilities across machines. Use when agent tooling, Remote Control defaults, models, updates, or skills are missing, duplicated, stale, or inconsistent.
---

# Fleet Agents

Set `SKILL_DIR` to the absolute directory containing this loaded `SKILL.md` and
`CLI="$SKILL_DIR/../../scripts/machine-utilities"`; the shell working directory
is not the skill directory. Collect `--section agents --section auth` when
evaluating capability readiness; provider-only inventory may collect just
`agents`. Windows tasks must pass `-AllowAuthVerify` for the combined readiness
check. Compare
runtime versions, plugin name/version/manager, standalone skill hashes and
origins, skills-cli lock metadata, JSM provenance, and configured logical
capabilities. Read `"$SKILL_DIR/../../references/agent-settings-and-auth.md"`
before auditing agent settings, runtime installation, model policy, or Remote
Control. Treat the same capability delivered as a plugin and a standalone
skill as equivalent only when the config says so; report duplicate providers.

Use manager-native ownership:

- Codex plugins: use Codex marketplace/plugin commands.
- Claude plugins: use Claude marketplace/plugin commands.
- skills-cli installs: use `npx skills check` or `npx skills update` and retain
  `.agents/.skill-lock.json` provenance.
- JSM installs: use JSM for inventory/update when its metadata proves ownership.
- local source skills: update their owning repository; do not overwrite them
  with a package manager.

## Routine named-plugin refresh

An explicit request to update or refresh one named marketplace plugin is
mutation authorization for exactly that plugin, marketplace, requested hosts,
and applicable harnesses. Do not require a fleet-wide inventory/readiness
matrix or a sealed-plan round trip for this routine path. Resolve every target
from `MACHINE_UTILITIES_CONFIG`, falling back to
`${XDG_CONFIG_HOME:-$HOME/.config}/machine-utilities/config.json`, and use each
target's configured `local`, `ssh`, or `codex-remote-control` transport. Never
guess an SSH alias or substitute WSL for native Windows.

Before changing a target, capture a bounded `agents` inventory and retain only
the exact plugin's Codex and Claude records as the before-state. Treat the two
harnesses independently and refresh each available, applicable runtime.
For local execution run the manager directly. For SSH, use the configured
alias and execute through the target login shell (`$SHELL -lc`). Run only these
manager-native command sequences, in order, substituting the authorized names:

```text
codex plugin marketplace upgrade MARKETPLACE --json
codex plugin add PLUGIN@MARKETPLACE --json

claude plugin marketplace update MARKETPLACE
claude plugin update PLUGIN@MARKETPLACE --scope user
```

The Codex add is idempotent. For Claude, use `plugin update` when the exact
plugin is installed; if it is absent, replace only that second Claude command
with `claude plugin install PLUGIN@MARKETPLACE --scope user`. Do not update any
other plugin or synchronize unrelated runtimes, settings, skills, provenance,
or configuration. Manager output is progress evidence, not post-state.
Recapture the bounded `agents` inventory after each harness attempt. Require the
exact `PLUGIN@MARKETPLACE` record to be installed and enabled. Post-state must
report the requested version. A failure in one harness does not erase the other
harness's evidence or success.

For a configured `codex-remote-control` target, follow the routine-refresh
path in `"$SKILL_DIR/../../references/codex-remote-control.md"`, using a visible
native task and native PowerShell. Lazy-discover the task-control app tools
before declaring them unavailable.

Use the sealed-plan reconciliation path below instead when the request includes
broad drift, runtime or settings changes, provenance repair, provider
conversion, ambiguous plugin/marketplace/host scope, or any mutation beyond
the explicit named-plugin refresh.

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
The executor supports exact `codex update` and `claude update` runtime updates,
plus updates for skills-cli, JSM, and Claude plugins.
Codex replacement uses the native idempotent `codex plugin add
PLUGIN@MARKETPLACE --json` operation. An already-current runtime is a successful
no-op after it remains present in post-inventory. For a missing runtime, use the
official installer interactively or delegate a manager-owned install to
`fleet-update`; downloaded installer pipelines, removals, and provider
conversion are unsupported by sealed plans.

Configured `agent_artifacts` may declare an allowlisted `settings` object for a
JSON or TOML config file. Inventory emits one `agent_setting` record per key,
including observed value, desired value, presence, and `in_sync`; it never emits
unlisted config fields. Reconcile the owning file through `fleet-chezmoi` where
possible. Do not write Claude Desktop internal state or undocumented Codex
Desktop preferences. Claude's supported Remote Control default lives in the
shared Code settings file; the invoking agent must check Codex Desktop host
enablement manually.

Use local/SSH execution where configured. SSH uses bounded connection and
keepalive timeouts and must match the configured native hostname/user before
mutation. Run every SSH operation through the target user's configured login
shell (`$SHELL -lc`) so user-level paths such as `$HOME/.local/bin` are
available. Never use raw non-login SSH command execution or infer that tooling
is absent from its restricted `PATH`. For Windows Codex tasks read and follow
`"$SKILL_DIR/../../references/codex-remote-control.md"`; Claude reports
unsupported rather than using WSL. Re-inventory after changes and report
unresolved provenance as unknown, not guessed.

## Protected profile actions

Use `"$CLI" privilege-status HOST SNAPSHOT` before proposing protected profile
work. The only agent-content action is the readiness-advertised
`profile.apply-managed-bundle.v1` or read-only
`profile.inventory-managed-state.v1` in `windows-user-s4u-v1`. Build identical
Codex/Claude bytes with `"$CLI" profile-bundle SPEC SOURCE-ROOT OUTPUT`; include
only bounded config scalars, standalone-skill files, agent definitions, and
local marketplace desired records already authorized by the protected entry
map. Never copy credentials, secret-backed templates, installed plugin caches,
agent internal state, startup tasks, or arbitrary paths. A staged plugin
desired record is `manager_activation_pending` until the next ordinary user
session lets its manager activate it; logged-off S4U success is not plugin
activation evidence.

The shared protected lifecycle vocabulary is `prepare-privilege-identity`,
`prepare-privilege-enrollment`, `verify-privilege-plan`,
`submit-privilege-plan`, `lookup-privilege-result`,
`preview-privilege-upgrade`, and `preview-privilege-revocation`. Preserve every
readiness/result state without fallback, including
`unsupported_security_boundary`, `partial`, and `stale`.
Never ask for or relay a sudo or Administrator password; stop at the local human
password/UAC boundary.
