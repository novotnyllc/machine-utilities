# machine-utilities

Configuration-driven fleet inventory and maintenance for Codex and Claude
Code. It inventories packages, agent runtimes, plugins, standalone skills,
allowlisted Claude/Codex settings, projects, startup tasks, chezmoi state, and
credential-file or native CLI/session status across macOS, Linux/WSL, and Windows.

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
- an optional `handoff_project` identifying the ordinary configured project
  used as the private cross-project coordination ledger
- Codex/Claude capabilities, plugin or skill providers, and standalone skill
  roots; shared providers use one `agents`/`provider`/`source` declaration,
  while agent-specific providers use separate `codex` and `claude` entries
- agent definitions and credential artifacts, including their distribution
  policy
- safe semantic settings to compare without exposing unrelated config or MCP
  secrets; per-host paths are supported

GitHub project sources may be full clone URLs or `owner/repository` shorthand.
See
[`plugins/machine-utilities/config.example.json`](plugins/machine-utilities/config.example.json)
for the complete schema-by-example.

## Use

Ask Codex or Claude Code for one of the nine bundled skills:

- `fleet-readiness` — assess project, agent, authentication, and host readiness
  across configured machines and route reconciliation to the owning skill
- `fleet-inventory` — collect and compare structured JSONL snapshots
- `fleet-update` — plan, then explicitly apply Homebrew, APT, or winget updates
- `fleet-agents` — reconcile Codex/Claude runtimes, settings, plugins,
  skills-cli, and JSM state
- `fleet-auth` — audit credential metadata and perform deliberate secure copies
- `fleet-chezmoi` — inspect drift and plan pull, add, or apply operations
- `fleet-projects` — ensure repositories and Codex project readiness per host
- `remote-mac` — safely operate configured Macs over SSH or Tailscale
- `ssh-doctor` — diagnose macOS SSH, Remote Login, and launchd failures

The shared CLI is also directly usable:

```sh
plugins/machine-utilities/scripts/machine-utilities validate-config
plugins/machine-utilities/scripts/machine-utilities check-mutation-config
plugins/machine-utilities/scripts/machine-utilities executor-status executor.json
plugins/machine-utilities/scripts/machine-utilities verify-executor executor.json
plugins/machine-utilities/scripts/machine-utilities privilege-status HOST privilege-status.jsonl
plugins/machine-utilities/scripts/machine-utilities prepare-privilege-enrollment HOST enrollment.json
plugins/machine-utilities/scripts/machine-utilities prepare-privilege-identity HOST identity.json
plugins/machine-utilities/scripts/machine-utilities profile-bundle HOST bundle.json
plugins/machine-utilities/scripts/machine-utilities worker-config HOST DOMAIN worker-config.json
plugins/machine-utilities/scripts/machine-utilities collect --target local --section all --output snapshot.jsonl
plugins/machine-utilities/scripts/machine-utilities validate snapshot.jsonl
plugins/machine-utilities/scripts/machine-utilities render snapshot.jsonl
plugins/machine-utilities/scripts/machine-utilities compare before.jsonl after.jsonl
plugins/machine-utilities/scripts/machine-utilities record-codex-readiness snapshot.jsonl metadata.json enriched.jsonl
plugins/machine-utilities/scripts/machine-utilities seal-plan draft.json snapshot.jsonl plan.json
plugins/machine-utilities/scripts/machine-utilities verify-preconditions plan.json current.jsonl
plugins/machine-utilities/scripts/machine-utilities apply-plan plan.json PLAN-ID verified.jsonl
plugins/machine-utilities/scripts/machine-utilities apply-ssh-plan plan.json PLAN-ID verified.jsonl
plugins/machine-utilities/scripts/machine-utilities verify-privilege-plan plan.json current.jsonl
plugins/machine-utilities/scripts/machine-utilities submit-privilege-plan plan.json PLAN-ID verified.jsonl
plugins/machine-utilities/scripts/machine-utilities lookup-privilege-result plan.json OPERATION-INDEX result.txt
plugins/machine-utilities/scripts/machine-utilities preview-privilege-upgrade HOST upgrade.json
plugins/machine-utilities/scripts/machine-utilities preview-privilege-revocation HOST revocation.json
```

`--section` accepts `all`, `host`, `packages`, `agents`, `projects`, `startup`,
`chezmoi`, or `auth`.

Collectors write data-only JSONL to stdout. Human rendering is a separate
step, and snapshots written to disk are installed atomically with mode `0600`.
Credential contents are never included in inventory output.
Claude Code CLI and Desktop share supported Code settings but require separate
login enrollment; a full interactive Claude login is required on every Remote
Control host. The invoking agent must check Codex Desktop Remote enablement
manually because Codex has no documented persistent setting for it. See
[`plugins/machine-utilities/references/agent-settings-and-auth.md`](plugins/machine-utilities/references/agent-settings-and-auth.md).
Sealed plans are inert data: verification checks their digest and freshly
recaptured preconditions but neither grants consent nor executes plan text.
Every sealed plan binds the exact plugin version, integrity-manifest SHA-256,
and hashes of the runtime executor files listed in `integrity.json`.
`executor-status` verifies those bytes and records Git commit/tree evidence
when run from a source checkout. This detects a stale or altered worker without
custom signing infrastructure; the authenticated Git marketplace remains the
source of provenance.

Protected APT, WinGet, bounded Windows profile, and macOS root actions use
repository-fixed semantic action/context pairs rather than request-supplied
commands. macOS root support is owner-enrolled and disabled by default; v1
permits only `macos.install-signed-pkg.v1` and
`macos.apply-system-setting.v1`. It never exposes root Homebrew, arbitrary
`sudo`, or arbitrary plist paths. The signed-package action is script-free by
default; its `sealed-cask-payload-v1` policy mode may run only the exact,
owner-enrolled Apple-signed package through fixed `installer -pkg … -target /`.
It never runs Homebrew as root and does not claim Homebrew's Caskroom state was
updated when the broker action is used alone. Fleet cask apply instead keeps
Homebrew as the ordinary-user transaction owner so successful app upgrades
update Caskroom metadata. A human-enrolled `macos-cask-app` constraint binds
one cask token to one app basename; the typed broker may prepare only that
existing, non-symlink `/Applications` target for the enrolled user with an
attested, non-symlink-following `chown`. Homebrew still performs the app
replacement as the ordinary user. If Homebrew attempts its hardcoded package
`sudo`, the packaged hook redirects only the fixed signed-package installer
shape to the broker, which substitutes the exact protected
`sealed-cask-payload-v1` artifact. Unenrolled app targets, receipt-pattern
deletion, installer choices, scripts outside that sealed package, and other
exotic artifacts return
`unsupported_homebrew_cask_privilege_boundary`. WinGet is
required for V1 Windows machine-package operations. Enrollment preparation and
upgrade/revocation previews are inert: the agent stops at the owner's local
password or UAC boundary and never requests or relays an elevation credential.
WSL has no protected root boundary and reports `unsupported_security_boundary`
without fallback. Result lookup is read-only and never resubmits a request.

The current V1 broker map is deliberately small: Linux runs fixed APT actions
through the root broker; macOS keeps Homebrew and Caskroom under the ordinary
user and uses the enrolled typed broker only to prepare an exact existing app
bundle or sealed package payload (including generic casks such as Visual
Studio Code); native Windows runs machine WinGet through LocalSystem using the
pinned, signed official `Microsoft.WinGet.Client` PowerShell module (1.29.280).
The repository self-checks these contracts and policy parsers locally. Real
root-owned macOS bundle preparation and logged-off LocalSystem WinGet remain
native canaries requiring a recoverable host and interactive human enrollment;
they are not implied by fixture or CI results.

Release integrity proves plugin source bytes. It does not prove or mutate the
separately installed root-/Administrator-owned broker generation, policy,
OpenSSH transport, tasks, or native-canary receipts. Updating or uninstalling
the plugin therefore neither upgrades nor revokes protected host state. The
Windows SFTP readiness record is a `protected-local-observation`: its exact
local ACL, controller-signed candidate/CMS, expiry, route, and protected local
projections are checked together. U6 v1 does not expose portable native-canary
proof, and copied public bytes or a user-owned identity receipt cannot establish
readiness. The
complete node-key, offline-CA, Windows SFTP, recovery, rotation, and revocation
runbook is in
[`plugins/machine-utilities/references/windows-sftp.md`](plugins/machine-utilities/references/windows-sftp.md).

For a local target, `apply-plan` requires the exact sealed plan ID, accepts only
operation-specific native command shapes, recaptures its own trusted preflight,
and returns a fresh post-change inventory. When an operation or postcondition
fails, apply stops that host and, when post-inventory remains available, writes
an authoritative partial result before returning failure. `apply-ssh-plan`
sends only the bounded worker config and sealed plan to the exact installed
release, then enforces the same executor, configured hostname/user, fresh
precondition, argv, and semantic post-state checks on the SSH host. SSH
connection establishment is bounded to 10 seconds, with 15-second keepalives
and two missed keepalives allowed. Ordinary schema-2 native Windows mutations
use `apply-windows.ps1` inside a visible Codex Desktop task. Protected or
logged-off Windows operations use only the enrolled `windows-sftp` broker
route.
The task—not the controller—owns its normal permission prompts. Claude Code
cannot control a Codex Desktop host; direct native Windows transport therefore
requires Codex.
See
[`plugins/machine-utilities/references/codex-remote-control.md`](plugins/machine-utilities/references/codex-remote-control.md)
for that workflow and its limitations.

The design and trust boundaries are documented in
[`docs/architecture.md`](docs/architecture.md).

## Testing

```sh
plugins/machine-utilities/scripts/test-machine-utilities
```

The self-check exercises integrity verification, bounded worker config,
collectors, CLI validation, sealed-plan preconditions, and safe local/SSH
fixtures. When `pwsh` is installed, it also runs the Windows collector against
cross-platform fixtures. Hosted CI runs on macOS, Linux, and Windows.

## Plugin manifests

- Codex: `plugins/machine-utilities/.codex-plugin/plugin.json`
- Claude Code: `plugins/machine-utilities/.claude-plugin/plugin.json`
- Marketplace catalogs: the separate `novotnyllc/marketplace` repository

No machine names, addresses, users, secrets, or private inventory belong in
this repository.
