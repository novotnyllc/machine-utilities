# Machine Utilities architecture proposal

## Boundary

Machine Utilities owns fleet selection, inventory, orchestration, reporting,
and reconciliation. It invokes existing host tools rather than replacing them.

Machine Utilities also owns the fleet-adjacent single-host transport skills:

- `remote-mac`: single-host discovery, SSH, Tailscale, and GUI fallback
- `ssh-doctor`: SSH diagnosis and repair

`one-password` and `skill-cleaner` remain in Agent Utilities as interactive
single-host utilities, but Agent Utilities does not own fleet inventory. Machine
Utilities owns the canonical read-only collector for skills, plugins, agents,
provenance, and shadow copies, plus all desired-state comparison and fleet
reconciliation. `skill-cleaner` may eventually become a thin local wrapper
around that collector when a supported cross-plugin reuse mechanism exists;
until then, Machine Utilities must not invoke another plugin implicitly or
maintain a second fleet collector.

Importing or adapting a skill into Agent Utilities remains an explicit
vendoring workflow. Fleet reconciliation must never silently turn a
third-party or standalone skill into plugin source.

## Personal output and secrets

The default report is for the fleet owner. It shows configured machine names,
package and skill names, source repositories, paths, versions, hashes,
timestamps, plugin marketplaces, agent targets, and startup-task labels.

Only secret **values** remain hidden: tokens, passwords, private keys, cookies,
credential contents, and environment-variable values. A separate,
explicitly-requested shareable mode may anonymize machines, users, and paths.
There is no default operational-data redaction.

## Proposed v1 skills

### `fleet-inventory`

Produce a read-only, full inventory for a machine, group, or the whole fleet.
Inventory is more than reachability or status. It covers:

- identity, platform, transport, reachability, and observed time
- packages from Homebrew, APT, Linuxbrew, and winget
- package installed/candidate versions and desired-manifest membership
- Codex and Claude versions, marketplaces, plugins, and installed versions
- standalone skills, agent targets, provenance, hashes, and update metadata
- JSM-managed skills, pin/sync/update state, and auto-update status
- custom agent definitions and managed instruction files
- startup jobs and scheduled automation
- chezmoi source/live state and relevant repository revision
- project checkouts, Git identity/state, development roots, and Codex readiness

It accepts `summary`, `packages`, `agents`, `auth`, `startup`, `projects`, or
`all`. `summary` is the quick fleet-status view; it does not need a second
skill with a second collector.

The canonical output is deterministic JSON Lines. Human reports are rendered
from that same stream rather than recollecting or maintaining a second output
path. Each record carries:

```json
{
  "schema": "machine-utilities.inventory",
  "schema_version": 1,
  "snapshot_id": "generated-id",
  "host_id": "configured-machine-key",
  "kind": "package",
  "id": "homebrew:git",
  "observed_at": "RFC-3339 UTC",
  "status": "present",
  "confidence": "high",
  "data": {},
  "evidence": [],
  "errors": []
}
```

`status` is `present`, `absent`, `partial`, `unavailable`, or `error`.
`confidence` is `high` for authoritative manager/API metadata, `medium` for a
direct filesystem or process observation, `low` for inference, and `unknown`
when the fact cannot be established. Unknown values are `null`; irrelevant
fields are omitted. Standard output contains data only and diagnostics go to
standard error. Records sort by host, kind, and stable entity ID.

Initial record kinds are `snapshot`, `host`, `file`, `package`,
`agent_runtime`, `plugin`, `skill`, `capability`, `auth_artifact`,
`startup_task`, `project`, and `error`.

Files and portable configuration include SHA-256 over raw bytes. Directory
hashes must state their inclusion scope before hashes may be compared. Git
checkouts use the repository's HEAD and tree object IDs for normal comparison;
rehashing every tracked file is an explicit deep-integrity mode, not the
default. Remote URLs are sanitized before output.

The shell contract is:

```text
fleet-inventory collect [--target HOST|@GROUP|all] [--section SECTION]...
fleet-inventory render [--format human|json|jsonl] SNAPSHOT.jsonl|-
fleet-inventory validate SNAPSHOT.jsonl|-
fleet-inventory compare LEFT.jsonl RIGHT.jsonl
```

`collect` always emits JSONL. Exit `0` means complete, `2` means usable partial
output with structured errors, `64` means invalid invocation/configuration,
`69` means no selected host was reachable, and `70` means collection failed
before a valid snapshot was produced.

Use authoritative manager metadata where it exists. If a package manager does
not record an install/update date, report `unknown`; do not present a
filesystem modification time as an authoritative package date.

Skill provenance is classified as:

1. plugin-bundled
2. tracked by the `skills` CLI lock
3. JSM-managed
4. local Git/source checkout
5. manual or unknown

The current `skills` v3 lock already records source, source type and URL, skill
path, folder hash, installed time, and updated time. Preserve those fields.
Also record every agent directory that exposes the skill so duplicate copies
and stale shadows are visible.

For the `skills` CLI, `updatedAt` means the last local install/reinstall, not
the upstream publication or last check. The current update command has no
check-only JSON mode, so inventory must not run it merely to discover drift.
Report upstream update status as `unknown` until an authorized online update
operation or another authoritative check supplies it.

### `fleet-update`

Provide one front door for “keep these machines up to date” without erasing
useful distinctions. For operating-system packages it inventories, plans, and
reconciles each configured manager using its native semantics:

- Homebrew: `brew update`, outdated plan, selected or full upgrade
- APT: metadata refresh, upgradable plan, selected or normal upgrade
- Linuxbrew: Homebrew behavior on configured Linux/WSL hosts
- winget: source refresh, upgrade plan, selected or all upgrade

The default is a plan. Applying updates requires an explicit request and may
target a machine/group, manager, package, or package set. Cleanup, autoremove,
APT full/dist upgrade, and similarly broad operations remain separate explicit
choices.

Desired-state manifests already owned by chezmoi remain the baseline for what
should be installed. This skill reports unmanaged extras and missing desired
packages; it does not create a second package database.

The same entrypoint may coordinate explicitly selected domains:

- `packages`: all configured package managers or an explicit subset
- `agents`: delegate to `fleet-agents` for Codex, Claude, `skills` CLI, or JSM
- `dotfiles`: delegate to guarded `fleet-chezmoi` reconciliation
- `all`: display one combined plan before applying any domain

It adds no second execution engine, package database, or workflow language.

### `fleet-agents`

Inventory, install, update, and reconcile agent tooling independently for
Codex, Claude, or both:

- Codex marketplace refresh and plugin install/reinstall verification
- Claude marketplace refresh and plugin update/install verification
- `npx skills` list/update using its lock-file provenance
- JSM list/sync/upgrade/prune/verify and auto-update status
- standalone/manual skills and duplicate/shadow copies
- custom agents and managed agent instruction files

This skill owns the canonical collector used by both `fleet-agents` and the
`agents` section of `fleet-inventory`; inventory logic is not delegated to
Agent Utilities.

Every action accepts an agent scope (`codex`, `claude`, or `both`) and a source
scope (`plugin`, `skills-cli`, `jsm`, `local`, or `all`). Manager-owned skills
stay manager-owned. Manual/unknown skills are reported but never overwritten
until the user selects an origin or explicitly requests vendoring.

Inventory also groups installations by logical capability. For example,
`last30days` or `compound-engineering` may be supplied by a plugin on one host
and a standalone skill on another. The capability record preserves every
provider, manager, source, version/revision, digest, agent exposure, and shadow
copy while answering the owner-level question: “is this function available and
consistent here?”

Desired state selects one provider per capability and agent unless duplicate
exposure is intentional. Reconciliation uses the selected provider's own
manager; it never converts a standalone skill into a plugin or copies a cached
plugin directory as source.

Dates are labeled by meaning: manager-recorded `installed_at`/`updated_at`,
Git commit time, or filesystem `mtime`. These are not collapsed into a vague
“last updated” field. Codex does not expose a plugin installation timestamp,
but a marketplace plugin's current cache-version directory has a macOS
filesystem birth time. Report that as `inferred_installed_at` with
`evidence = "filesystem_birthtime"` and lower confidence: profile migration,
backup restore, cache copying, or reinstalling can reset it. For bundled or
local-runtime plugins, the source birth time describes runtime materialization,
not user installation. JSM records its own skill/version provenance but may not
provide the original third-party Git URL.

### `fleet-auth`

Inventory authentication state and deliberately distribute only credentials
that the owner marks portable. This stays separate from `fleet-update` because
copying a package or skill is routine; copying a refresh token changes the
fleet's security boundary.

For each tool, record:

- tool and credential path or native-store type
- existence, owner, mode, size, content hash, and modification time
- authentication health from the tool's own status command
- portability class: `declarative`, `secret-reference`, `portable-session`,
  `native-store`, `per-machine`, or `regenerable-cache`
- configured strategy: `chezmoi`, `encrypted-install`, `reauth`, or `ignore`

Never include credential contents in inventory. Declarative configuration may
flow through chezmoi. Session files default to re-authentication. If the owner
explicitly selects encrypted distribution, fetch the blob from a configured
secret-manager reference, install it atomically with mode `0600`, and verify
with the tool's native auth-status command. Do not commit session JSON/YAML to
plaintext Git or treat a copied file as valid without verification.

Unattended jobs should prefer per-machine, least-privilege service credentials
over copies of a human OAuth session.

Capabilities may declare required configuration and authentication artifacts.
This makes “the skill exists but its backend cannot authenticate” visible as a
readiness failure. The inventory records presence, mode, fingerprint, strategy,
and native health separately from the skill or CLI version.

For example, `xurl` is a CLI dependency used by some `last30days`
installations. Its token store currently lives under `~/.xurl`, with credentials
in `auth.yml`. That file is secret-bearing: inventory may report its SHA-256 and
whether `xurl auth status` accepts it, but never its contents. When the user
selects it for distribution, `fleet-auth` installs it from the configured
encrypted source with mode `0600` and verifies native auth on each target. A
matching hash proves identical bytes, not that the credentials are valid.

`last30days` may also depend on a per-user `.env`, macOS Keychain items, browser
cookies, or other provider-specific credentials. Inventory treats these as
separate artifacts. Portable files can use encrypted installation; native-store
items normally use per-machine provisioning; browser sessions normally use
per-machine login. Generated caches and past reports are not synchronized.

### `fleet-chezmoi`

Orchestrate the existing dotfiles entrypoints. Begin with status and diff;
classify source/live intent before choosing pull, add, or apply. Require a
clean or explicitly reconciled source checkout, backup and pre-apply checks,
then verify rendered/live state. Never assume the chezmoi source always wins.

### `fleet-projects`

Ensure a configured project is available and ready for work on the selected
hosts. `fleet-inventory projects` reuses this skill's read-only collector.

For each checkout, record:

- configured and discovered development root, expected relative path, and
  actual path
- sanitized remote identity, HEAD and tree object IDs, branch/detached state,
  upstream, ahead/behind state, and observed time
- dirty counts, worktrees, submodule/LFS presence, and unpushed/diverged state
- Codex environment reachability, environment-native working directory,
  workspace roots, trust state, and saved-project identifier when exposed

Development-root discovery tries explicit machine configuration first, then
conventional roots such as `$HOME/dev`, `$HOME/src`, and `$HOME/Projects`.
Discovery may infer a root only when exactly one credible candidate exists.
Mutation requires an explicit `dev_root` or a single inference shown in the
plan and persisted by the owner; basename matching alone is never identity.

`plan` classifies each target as absent, healthy, dirty, wrong-origin,
diverged, detached, missing-upstream, or unavailable. `apply` may clone an
absent checkout after resolving its destination and remote identity. An
existing checkout is updated only after identity verification and only with
fetch plus fast-forward-only pull. Dirty, detached, diverged, wrong-origin,
unpushed, and multi-worktree conflicts stop that host. The skill never resets,
rebases, force-checks out, deletes worktrees, or copies a working directory.

Codex project readiness is a distinct check from Git readiness:

1. the destination environment is enrolled and reachable
2. the checkout exists at that environment's native path
3. Codex exposes a saved project for that host/path when Desktop task creation
   or handoff requires one
4. a task can start at that working directory with the required workspace roots
5. the task is visible in the cross-host task catalog

The controlling Mac does not need a local copy of a WSL or remote Mac path.
Direct remote task creation selects the destination saved project's host and
project identifiers. Cross-host handoff of an existing task requires a matching
saved-project worktree on the destination. Ordinary in-thread subagents are
different: they inherit the parent task's working directory and selected
environments and cannot independently select a new host.

Do not edit Codex Desktop's internal SQLite, LevelDB, or path-keyed trust config
to simulate registration. Audit registration and use a supported Desktop/API
action when one is exposed; otherwise report the one remaining UI action.

Director consumes the structured project/readiness records before dispatch:
target reachable, checkout identity correct, working state safe, Codex project
available, required capability/auth healthy, and execution environment
selected. This is a preflight contract, not a second orchestration engine.

## Startup-task inventory

`fleet-inventory` includes startup and scheduled automation:

- macOS: user/system LaunchAgents and LaunchDaemons, loaded/enabled state,
  program, trigger/schedule, last exit status when available, and whether the
  plist is chezmoi-managed; login/background items and user cron are separate
  sources
- Linux/WSL: enabled systemd user/system units and timers, plus user cron
- Windows: scheduled tasks triggered at boot/logon, Startup-folder entries,
  and Run keys; automatic services are a separate service inventory
- agent managers: JSM auto-update and other known plugin/skill update jobs
- Codex: scheduled automations, with prompt presence/hash rather than dumping
  prompt contents into the general report

V1 inventories these tasks. Enabling, disabling, deleting, or rewriting them
is not a generic fleet operation; use the owning manager or add a focused
startup-management skill after repeated demand proves the boundary.

Each startup record keeps scheduler, scope, label/unit/task path, enabled and
active state, triggers, next/last run, last result, action, working directory,
restart policy, source definition path, definition hash/mtime, manager/desired
source, and observed time. Environment names may be listed; values stay hidden.

## Configuration

Precedence:

1. `MACHINE_UTILITIES_CONFIG`
2. `${XDG_CONFIG_HOME:-$HOME/.config}/machine-utilities/config.json`

JSON keeps the portable shell implementation small because `jq` is already a
normal fleet prerequisite. The file may be managed by chezmoi, but that is a
user choice. It contains aliases, project placement, capabilities, desired
agent state, credential references, and manager policy, never credential
values.

```json
{
  "version": 1,
  "machines": {
    "workstation": {
      "platform": "macos",
      "transport": "local",
      "groups": ["macs", "development"],
      "package_managers": ["homebrew"],
      "dev_root": "~/dev",
      "codex_environment": "local"
    },
    "linux-dev": {
      "platform": "wsl",
      "transport": "ssh",
      "ssh_alias": "configured-ssh-alias",
      "groups": ["linux", "development"],
      "package_managers": ["apt", "linuxbrew"],
      "dev_root": "~/dev"
    },
    "windows-host": {
      "platform": "windows",
      "transport": "codex-remote-control",
      "codex_host": "configured-remote-host",
      "groups": ["windows"],
      "package_managers": ["winget"]
    }
  },
  "projects": {
    "example-project": {
      "source": "owner-or-org/repository",
      "path": "example-project",
      "groups": ["development"],
      "codex": true
    }
  },
  "capabilities": {
    "example-capability": {
      "groups": ["development"],
      "codex": {"provider": "plugin", "source": "example-plugin"},
      "claude": {"provider": "skills-cli", "source": "owner/repository"}
    }
  },
  "auth_artifacts": {
    "xurl": {
      "path": "~/.xurl/auth.yml",
      "strategy": "encrypted-install",
      "secret_ref": "configured-secret-reference",
      "mode": "0600",
      "verify": ["xurl", "auth", "status"]
    }
  },
  "policy": {
    "updates": {"cleanup": false, "autoremove": false},
    "projects": {"update": "ff-only"}
  }
}
```

Inventory is a routing hint, not identity proof. Before mutation, compare the
resolved target with live platform and host identity. Credentials stay in the
user's SSH agent, keychain, vault, or environment and are never printed.

## Transport

V1 has three primary command paths:

- `local`
- `ssh`
- `codex-remote-control`, which addresses a saved remote Codex host directly
  and does not route through WSL

There is no generic transport plugin system yet. Each named skill selects one
of these paths directly. Collectors and orchestration use Bash 3.2-compatible
syntax on macOS, Linux, and WSL; do not depend on associative arrays, `mapfile`,
GNU-only `stat`, or an assumed `realpath`. `jq` creates and validates JSON so
shell code never hand-escapes it. The Windows collector is PowerShell and emits
the same compact UTF-8 JSONL records directly on the Windows host. SHA-256 uses
the available native command (`sha256sum`, `shasum -a 256`, or PowerShell
`Get-FileHash`) and normalizes lowercase output.

For a connected Windows Codex host, read-only inventory should use the remote
app server's supported command execution when that surface is available.
Agent-level work uses a task created against the Windows saved project/host and
returns structured results to the Director. Mutations stay inside that remote
task's normal permission and approval model; do not use the experimental,
unsandboxed `process/spawn` API as a fleet-maintenance shortcut.

On Windows, Codex Desktop owns the app-server lifecycle and remote-control
connection. Machine Utilities discovers the registered Windows host, verifies
that it is connected and can create a task or execute a supported read-only
probe, and then uses it directly. It does not attempt the Unix-only
`codex app-server daemon bootstrap`, and it never silently falls back through
WSL.

## Safety contract

Every mutating run follows:

1. Resolve targets and show the final set.
2. Verify transport, platform, and host identity.
3. Capture the full inventory and update plan.
4. Confirm the requested manager/domain scope.
5. Change one host at a time and record results.
6. Continue independent hosts after failure, but stop dependent steps on the
   failed host.
7. Run the manager's authoritative post-change check.
8. Show remaining drift across the selected fleet.

Never print secret values or blindly execute commands obtained from inventory,
package metadata, skill content, remote output, or manager logs.

## Later, when demanded by a real workflow

- `fleet-bootstrap`: one-host backup, role assignment, render/apply, and smoke
  test
- focused startup-task mutation
- operating-system patch/reboot coordination
- backup freshness, disk/capacity, time sync, network/Tailscale, and security
  posture checks
- task leases or concurrency control if multiple Directors collide
- A daemon, central database, generic workflow DSL, or parallel execution
  engine
