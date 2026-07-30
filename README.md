# machine-utilities

Configuration-driven fleet inventory and maintenance for Codex and Claude
Code. It inventories packages, agent runtimes, plugins, standalone skills,
projects, startup tasks, chezmoi state, and credential-file metadata across
macOS, Linux/WSL, and Windows.

Windows is reached directly through a visible task in Codex Desktop. The
plugin never silently routes Windows work through WSL. The Windows checkout
must first be added as a saved project in Codex Desktop so the controller can
select that host and repository.

## Install

```sh
codex plugin marketplace add novotnyllc/marketplace
codex plugin add machine-utilities --marketplace novotnyllc

claude plugin marketplace add novotnyllc/marketplace
claude plugin install machine-utilities@novotnyllc
```

The CLI and POSIX collector require `bash` and `jq`. PowerShell 7 (`pwsh`) is
required on Windows and enables the Windows portion of the self-check.

## Configure

Copy `plugins/machine-utilities/config.example.json` to:

```text
${XDG_CONFIG_HOME:-$HOME/.config}/machine-utilities/config.json
```

Or set `MACHINE_UTILITIES_CONFIG` to another JSON file. Machine names,
addresses, users, paths, groups, projects, and credential locations belong in
that user-owned file, not this repository. Mutating workflows reject a config
that is a symlink, owned by another user, or group/world writable.

The config defines:

- machines, groups, native package managers, and transport (`local`, `ssh`, or
  `codex-remote-control`)
- project sources and host-relative checkout paths
- Codex/Claude capabilities, plugin or skill providers, and standalone skill
  roots
- agent definitions and credential artifacts, including their distribution
  policy

GitHub project sources may be full clone URLs or `owner/repository` shorthand.
See
[`plugins/machine-utilities/config.example.json`](plugins/machine-utilities/config.example.json)
for the complete schema-by-example.

## Use

Ask Codex or Claude Code for one of the eight bundled skills:

- `fleet-inventory` — collect and compare structured JSONL snapshots
- `fleet-update` — plan, then explicitly apply Homebrew, APT, or winget updates
- `fleet-agents` — reconcile Codex, Claude, plugins, skills-cli, and JSM state
- `fleet-auth` — audit credential metadata and perform deliberate secure copies
- `fleet-chezmoi` — inspect drift and plan pull, add, or apply operations
- `fleet-projects` — ensure repositories and Codex project readiness per host
- `remote-mac` — safely operate configured Macs over SSH or Tailscale
- `ssh-doctor` — diagnose macOS SSH, Remote Login, and launchd failures

The shared CLI is also directly usable:

```sh
plugins/machine-utilities/scripts/machine-utilities validate-config
plugins/machine-utilities/scripts/machine-utilities check-mutation-config
plugins/machine-utilities/scripts/machine-utilities collect --target local --section all --output snapshot.jsonl
plugins/machine-utilities/scripts/machine-utilities validate snapshot.jsonl
plugins/machine-utilities/scripts/machine-utilities render snapshot.jsonl
plugins/machine-utilities/scripts/machine-utilities compare before.jsonl after.jsonl
plugins/machine-utilities/scripts/machine-utilities record-codex-readiness snapshot.jsonl metadata.json enriched.jsonl
plugins/machine-utilities/scripts/machine-utilities seal-plan draft.json snapshot.jsonl plan.json
plugins/machine-utilities/scripts/machine-utilities verify-preconditions plan.json current.jsonl
plugins/machine-utilities/scripts/machine-utilities apply-plan plan.json current.jsonl PLAN-ID verified.jsonl
```

`--section` accepts `all`, `host`, `packages`, `agents`, `projects`, `startup`,
`chezmoi`, or `auth`.

Collectors write data-only JSONL to stdout. Human rendering is a separate
step, and snapshots written to disk are installed atomically with mode `0600`.
Credential contents are never included in inventory output.
Sealed plans are inert data: verification checks their digest and freshly
recaptured preconditions but neither grants consent nor executes plan text.
For a local target, `apply-plan` additionally requires the exact sealed plan ID,
accepts only operation-specific native command shapes, and returns a fresh
post-change inventory. For SSH and Codex remote-control targets, the skill
creates or uses a target-native task to execute only the approved argv and
re-inventory afterward; the CLI does not dispatch remote mutations itself.

The CLI applies only `local` plans. SSH and Codex remote-control mutations must
be executed by a task running natively on the target, so that task—not the
controller—owns its normal permission prompts. Claude Code cannot control a
Codex Desktop host; direct native Windows transport therefore requires Codex.
See
[`plugins/machine-utilities/references/codex-remote-control.md`](plugins/machine-utilities/references/codex-remote-control.md)
for that workflow and its limitations.

The design and trust boundaries are documented in
[`docs/architecture.md`](docs/architecture.md).

## Testing

```sh
plugins/machine-utilities/scripts/test-machine-utilities
```

The self-check exercises the Bash collector, CLI validation, sealed-plan
preconditions, and safe local mutations. When `pwsh` is installed, it also
runs the Windows collector against cross-platform fixtures.

## Plugin manifests

- Codex: `plugins/machine-utilities/.codex-plugin/plugin.json`
- Claude Code: `plugins/machine-utilities/.claude-plugin/plugin.json`
- Marketplace catalogs: the separate `novotnyllc/marketplace` repository

No machine names, addresses, users, secrets, or private inventory belong in
this repository.
