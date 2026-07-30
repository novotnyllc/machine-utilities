---
title: "feat: Implement Machine Utilities v1"
date: 2026-07-29
type: feat
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-plan-bootstrap
execution: code
---

# feat: Implement Machine Utilities v1

## Goal Capsule

- **Objective:** Deliver a dual Codex and Claude plugin that inventories mixed-platform machines, renders human and agent-readable reports, plans guarded reconciliation, and prepares projects for Director dispatch.
- **Authority:** `docs/architecture.md` and the user's settled transport, configuration, ownership, and security decisions govern this plan.
- **Execution profile:** Build one shared dependency-light command surface, then expose focused skills that orchestrate it.
- **Stop condition:** The released plugin is merged, published through the marketplace, installable by both Codex and Claude, and verified by local plus reachable target-native canaries.
- **Tail ownership:** This run owns implementation, review, coordinated pull requests, dependency-ordered merges, publication, and post-publication verification.

---

## Product Contract

### Summary

Machine Utilities owns fleet selection, structured inventory, desired-state comparison, project readiness, guarded reconciliation, remote-Mac operations, and SSH diagnosis.

### Requirements

**Inventory and configuration**

- R1. Load versioned JSON configuration from `MACHINE_UTILITIES_CONFIG` or the XDG user configuration path without embedding machine inventory in the plugin.
- R1a. Treat configuration values as data, never shell source; before mutation require a regular owner-controlled configuration file that is not group/world writable.
- R1b. The initiating controller is configuration authority for a run; operation records carry its configuration digest and remote workers receive only the resolved target, scope, and policy needed for that operation.
- R2. Emit deterministic JSONL with stable host/entity identifiers, evidence, confidence, timestamps, structured errors, SHA-256 fingerprints where meaningful, and usable partial results.
- R3. Render human, JSON, and JSONL views from captured records without recollecting.
- R4. Inventory host facts, packages, Codex/Claude runtimes, plugins, standalone skills and provenance, configured authentication artifacts, projects, startup definitions, and chezmoi state using manager-native evidence.

**Planning and reconciliation**

- R5. Provide read-only plans before any package, agent, project, authentication, or chezmoi mutation.
- R5a. Give each plan a stable ID and digest over targets, operations, and preconditions; apply must reference it, recapture preconditions, and stop with a replacement plan when they changed.
- R6. Require explicit apply scope, live identity verification, manager-native post-change verification, and no automatic destructive Git/package behavior.
- R7. Preserve manager ownership for plugins and skills; compare installations by logical capability without silently converting or vendoring them.
- R8. Clone missing configured projects only after path and remote resolution; update existing clean tracked branches by fast-forward only and stop on ambiguous or unsafe Git states.

**Transport and agent operation**

- R9. Support local and SSH execution with Bash 3.2-compatible orchestration, plus a native PowerShell collector that emits the same semantic JSONL on Windows.
- R10. Use the registered Windows Codex Desktop remote-control host directly through visible task creation; never require or silently fall back through WSL.
- R11. Keep Codex remote task lifecycle logic in skill instructions rather than imitating Desktop APIs in Bash.
- R12. Return operation records carrying run, host, scope, phase, status, and task correlation so Director can distinguish queued, running, partial, blocked, failed, and completed work.
- R12a. Bound record and string sizes and treat all collected fields as inert untrusted data; never execute or follow inventory-provided instructions, commands, paths, or URLs without independent configuration and live validation.

**Security and parity**

- R13. Never emit secret values; inventory credential metadata, fingerprints, configured distribution strategy, and native health only.
- R14. Treat credential movement and mutations as separately authorized actions, preserving each remote task's native approval path.
- R14a. Install credentials through private same-directory temporary files, reject links and ownership mismatches, replace atomically, clean up on every exit, and restore the prior file when native verification fails.
- R15. Provide semantic Codex/Claude parity for local and SSH workflows; report unsupported transport explicitly where Claude lacks Codex Desktop remote control.
- R16. Treat executor readiness as a mandatory gate before a remote inventory or mutation: resolve the desired release to an exact plugin version, refresh the configured marketplace with the native manager, install or update the plugin, and verify the resulting executor before dispatch.
- R17. Identify an executor by plugin version, manifest SHA-256, and the SHA-256 values of runtime executor files listed in `integrity.json`. When running from a Git checkout, also record commit, tree, and dirty state. Refuse mismatched, dirty, symlinked, or group/world-writable executors for mutation.
- R18. Bind the verified executor identity to every sealed mutation plan and result. A target-native worker must enforce the same configuration, plan, host/user, freshness, precondition, argv, and post-state checks as local apply.
- R19. Keep transport orchestration outside the worker: SSH transfers bounded plan/config inputs and invokes the POSIX worker; Codex uses a visible native Windows task attached to the configured saved project. Windows must never route through WSL.
- R20. Reject plans containing operation types the executor cannot perform. Treat ambiguous, localized, or malformed `winget` candidate output as `unknown`, never as an actionable upgrade.
- R21. Run the repository checks in hosted CI on macOS, Linux, and Windows using only native shells and the repository's existing test harness.
- R22. Release with one version across both source manifests and both marketplace records. Merge the plugin source before marketplace metadata, then verify clean-profile installation and exact released identity.

### Scope Boundaries

- Human-only: Desktop remote-control enrollment, saved-project registration where no supported API exists, OAuth/device login, OS permission dialogs, and approval decisions.
- Deferred: a daemon, central database, generic workflow DSL, automated saved-project registration, task leases, and experimental unsandboxed remote execution.
- Generated reports and caches remain local artifacts and are not committed.
- Snapshot files default to private atomic writes, reject symlink destinations, and remain until the owner deletes them; Machine Utilities performs no automatic retention cleanup in v1.

---

## Planning Contract

### Key Technical Decisions

- KTD1. **One shared command surface** (session-settled: user-approved — chosen over independent per-skill implementations: inventory and reconciliation must stay consistent). Skills call the same plugin-owned scripts.
- KTD2. **JSON configuration and JSONL records** (session-settled: user-directed — chosen over TOML and prose-first output: Bash plus `jq` can validate and compose both without another runtime).
- KTD3. **Portable host code, harness-native orchestration** (session-settled: user-directed — chosen over a generic transport framework: Bash/PowerShell collect facts while skills use Codex task tools for Desktop remote control).
- KTD4. **Visible Windows Codex tasks in v1** (session-settled: user-directed — chosen over WSL routing: Windows already runs Codex Desktop and should be addressed directly).
- KTD5. **Plan before apply** (session-settled: user-approved — chosen over implicit reconciliation: package, Git, chezmoi, and credential changes have different risk boundaries).
- KTD6. **Git object identity for normal project comparison** (session-settled: user-approved — chosen over hashing every tracked file: HEAD/tree object IDs are authoritative and cheaper; deep SHA-256 remains explicit).
- KTD7. **Exact version plus hashes, no custom PKI** (session-settled: user-approved — authenticated Git/marketplace transport establishes provenance; SHA-256 detects stale or altered executors without a signing service).
- KTD8. **One target-native worker contract** (session-settled: user-approved — chosen over separate remote semantics: local and remote mutation must share the same sealed-plan checks).
- KTD9. **Controller-resolved releases** (implementation-settled — chosen over executing mutable `latest`: refresh first, resolve an exact version, then verify that exact installation before task dispatch).
- KTD10. **Native managers remain owners** (session-settled: user-approved — Codex/Claude plugin managers, Homebrew, APT, winget, `npx skills`, JSM, chezmoi, and Git retain their own installation state and verification).

### High-Level Technical Design

```mermaid
flowchart TB
  C["User configuration"] --> D["Shared dispatcher"]
  D --> P["POSIX collector/actions"]
  D --> W["Windows PowerShell collector"]
  D --> S["SSH target"]
  P --> J["Canonical JSONL"]
  W --> J
  S --> J
  J --> H["Human renderer"]
  J --> A["Director and agent consumers"]
  A --> T["Codex remote task on saved project"]
  T --> W
```

```mermaid
stateDiagram-v2
  [*] --> Inventory
  Inventory --> Plan
  Plan --> Blocked: unsafe or ambiguous
  Plan --> Apply: explicitly authorized
  Apply --> Verify
  Verify --> Completed
  Verify --> Partial
  Apply --> Failed
```

### Assumptions

- `jq` is a declared fleet prerequisite on POSIX hosts; the controller probes it before collection and emits a structured unavailable record when it is missing.
- Windows task execution uses the running Codex Desktop instance; direct cross-host command execution is not required for v1.
- Remote task JSONL may be returned in the durable final task response only after a transport spike establishes a safe byte bound and byte-for-byte retrieval. Larger results require a durable artifact path reported by the task.
- Claude reports Windows Codex remote-control as unsupported rather than pretending transport parity.

---

## Output Structure

```text
plugins/machine-utilities/
├── config.example.json
├── scripts/
│   ├── machine-utilities
│   ├── collect-posix
│   ├── collect-windows.ps1
│   └── test-machine-utilities
└── skills/
    ├── fleet-inventory/
    ├── fleet-update/
    ├── fleet-agents/
    ├── fleet-auth/
    ├── fleet-chezmoi/
    ├── fleet-projects/
    ├── remote-mac/
    └── ssh-doctor/
```

---

## Implementation Units

### U1. Shared configuration and JSONL contract

- **Goal:** Establish the dispatcher, configuration discovery/validation, record envelope, operation records, rendering, validation, and comparison.
- **Requirements:** R1-R3, R12-R13; KTD1-KTD3.
- **Dependencies:** None.
- **Files:** `plugins/machine-utilities/scripts/machine-utilities`, `plugins/machine-utilities/scripts/test-machine-utilities`, `plugins/machine-utilities/config.example.json`.
- **Approach:** Keep stdout data-only, stderr diagnostic-only, deterministic ordering, explicit partial exit status, bounded task payloads, exact schema-version rejection, argv-safe configuration handling, and private atomic snapshot writes.
- **Execution note:** Begin with fixture-based contract failures for invalid configuration, unstable ordering, secret leakage, and partial results.
- **Test scenarios:**
  - Missing default configuration produces a clear configuration error without creating files.
  - An explicit valid fixture selects a host/group and emits schema-versioned records in stable order.
  - Human and aggregate JSON render from saved JSONL without invoking a collector.
  - Invalid schema versions, malformed records, and mismatched snapshot IDs fail validation.
  - Values marked secret in fixtures never appear in stdout or stderr.
- **Verification:** The self-check proves configuration precedence, validation, rendering, comparison, ordering, and exit-code behavior.

### U2. Portable single-host inventory

- **Goal:** Collect POSIX host, package, agent/capability, authentication metadata, project, startup-definition, and chezmoi records.
- **Requirements:** R2, R4, R7-R8, R13; KTD6.
- **Dependencies:** U1.
- **Files:** `plugins/machine-utilities/scripts/collect-posix`, `plugins/machine-utilities/scripts/machine-utilities`, `plugins/machine-utilities/scripts/test-machine-utilities`.
- **Approach:** Probe only installed native managers, inspect configured artifacts/projects, sanitize remotes, preserve provenance fields, use HEAD/tree IDs for Git, and emit typed unavailable/partial records instead of fabricated values. Treat every observed string as bounded untrusted data.
- **Test scenarios:**
  - A fixture environment with fake manager binaries produces normalized package and runtime records.
  - Missing optional managers produce capability absence without making the snapshot invalid.
  - A dirty, detached, wrong-origin, or missing project is classified distinctly.
  - Authentication records include path metadata and SHA-256 but not file contents.
  - Skill-lock provenance and plugin-cache birth-time inference retain evidence and confidence labels.
- **Verification:** Fixture collection is deterministic and a real local read-only smoke run validates successfully.

### U3. SSH and native Windows collection

- **Goal:** Execute the same inventory contract over SSH and from native Windows PowerShell without a WSL dependency.
- **Requirements:** R2, R4, R9-R10, R15; KTD3-KTD4.
- **Dependencies:** U1, U2 and the remote-result transport spike described below.
- **Files:** `plugins/machine-utilities/scripts/machine-utilities`, `plugins/machine-utilities/scripts/collect-windows.ps1`, `plugins/machine-utilities/scripts/test-machine-utilities`.
- **Approach:** Use explicit configured SSH aliases with batch/no-TTY options; make PowerShell emit compact UTF-8 JSONL directly; keep Codex Desktop task dispatch out of shell code.
- **Test scenarios:**
  - SSH target resolution rejects absent aliases and identity mismatches before collection.
  - Remote partial failure preserves valid records and returns partial status.
  - PowerShell fixture output normalizes hashes, timestamps, package records, and nulls identically to POSIX semantics.
  - No WSL path or implicit fallback is selected for a `codex-remote-control` machine.
- **Execution note:** Before treating final task messages as a result channel, send a representative bounded JSONL fixture through a real remote task and verify byte-for-byte retrieval, truncation behavior, and resume behavior. Select a durable artifact-path fallback if the bound is insufficient.
- **Verification:** Shell tests validate dispatch construction; PowerShell parser/tests run when `pwsh` is available and otherwise report a precise local validation limitation.

### U4. Guarded plans and applies

- **Goal:** Add minimal package, project, agent, auth, and chezmoi planning/apply commands using native managers and shared safety gates.
- **Requirements:** R5-R8, R13-R14; KTD5-KTD6.
- **Dependencies:** U1-U3.
- **Files:** `plugins/machine-utilities/scripts/machine-utilities`, `plugins/machine-utilities/scripts/collect-posix`, `plugins/machine-utilities/scripts/collect-windows.ps1`, `plugins/machine-utilities/scripts/test-machine-utilities`.
- **Approach:** Default every domain to plan; require explicit apply, exact targets, and a referenced plan ID/digest; recapture and compare plan preconditions before mutation. Validate configuration ownership/mode without evaluating its values; delegate package/chezmoi work to native commands; clone only absent projects and fast-forward only safe repositories. Install configured portable credentials through private same-directory temporary files, reject links and ownership mismatches, replace atomically, clean up on every exit, run native verification, and restore the prior file on verification failure.
- **Test scenarios:**
  - Omitting `--apply` never invokes a mutating fixture command.
  - Apply refuses identity mismatch, dirty/diverged Git, ambiguous development root, and an unconfigured credential strategy.
  - Apply refuses a missing/mismatched plan digest or changed package, Git, credential, or target precondition and emits a replacement plan.
  - Apply refuses a non-regular, wrong-owner, group/world-writable, or symlinked configuration file.
  - Package cleanup, autoremove, distribution upgrade, and broad credential copying remain separate explicit choices.
  - Independent host failure does not broaden or mutate another target.
  - Successful fixture apply is followed by native verification and a new inventory record.
  - Credential fixture apply leaves no temporary file and restores the prior file when verification fails.
- **Verification:** Fake managers record exact argv so the self-check proves planning and safety gates without mutating the workstation.

### U5. Dual-agent fleet skills

- **Goal:** Expose `fleet-inventory`, `fleet-update`, `fleet-agents`, `fleet-auth`, `fleet-chezmoi`, and `fleet-projects` as concise Codex/Claude skills over the shared commands.
- **Requirements:** R3-R15; KTD1-KTD5.
- **Dependencies:** U1-U4.
- **Files:** `plugins/machine-utilities/skills/*/SKILL.md`, `plugins/machine-utilities/skills/*/agents/openai.yaml`, `plugins/machine-utilities/.claude-plugin/plugin.json`, `plugins/machine-utilities/skills/README.md`.
- **Approach:** Put deterministic collection/action behavior in scripts and judgment/orchestration in skills. Codex instructions use controller-resolved configuration, saved-project discovery, task creation, bounded waiting, result validation, and handoff requirements. Claude uses local/SSH execution and reports unsupported Windows transport.
- **Test scenarios:**
  - Every skill validates and points only to plugin-owned resources.
  - Inventory and planning requests remain read-only by default.
  - Windows Codex flow resolves a saved project, handles queued setup before a real task ID, waits for structured completion, and reports human-gated enrollment/registration.
  - Approval, disconnect, needs-attention, invalid JSONL, and missing matching handoff project surface as blocked/partial rather than triggering broader retries.
- **Verification:** Skill validators, manifest validation, and a forward-test agent can follow representative inventory and project-readiness prompts without hidden context.

### U6. Integration and documentation

- **Goal:** Leave a self-contained, validated plugin and accurate marketplace metadata.
- **Requirements:** R1-R15.
- **Dependencies:** U1-U5.
- **Files:** `README.md`, `docs/architecture.md`, `plugins/machine-utilities/.codex-plugin/plugin.json`, `plugins/machine-utilities/.claude-plugin/plugin.json`, and the separate marketplace repository manifests and README.
- **Approach:** Document configuration and invocation, keep architecture aligned with implemented behavior, list unsupported/deferred surfaces, and validate both harness formats without installing or publishing.
- **Test scenarios:**
  - All JSON manifests parse and version coupling stays aligned.
  - Codex and Claude plugin validators accept the final tree.
  - Shell syntax, self-checks, PowerShell checks when available, and whitespace validation pass.
  - A local end-to-end inventory snapshot validates and renders human output.
- **Verification:** The repository's complete local validation command set passes with any unavailable host-specific checks identified explicitly.

### U7. Executor identity and readiness

- **Goal:** Make stale or altered remote tooling observable and non-executable.
- **Requirements:** R16-R18, R22; KTD7, KTD9-KTD10.
- **Dependencies:** U1-U3.
- **Files:** the two plugin manifests, `scripts/machine-utilities`, `scripts/collect-posix`, `scripts/collect-windows.ps1`, `scripts/test-machine-utilities`, and remote-control documentation.
- **Approach:** Add a deterministic executor-status command/record containing the plugin version plus SHA-256 for each runtime executor file. Include Git commit/tree/dirty state only as source-checkout evidence. Mutation plans capture the exact release-file requirement; verification refuses an identity mismatch, symlink, unsafe ownership/mode, or file-hash mismatch. Marketplace refresh/install is a small manager-native bootstrap phase, followed by a fresh task before plugin code is used.
- **Test scenarios:**
  - Matching version and hashes pass; matching version with one changed script fails.
  - A symlinked or group/world-writable executor fails mutation readiness.
  - A source checkout reports commit/tree/dirty state without inventing those values for an installed cache.
  - A stale plugin produces a structured readiness failure and no mutation.
- **Verification:** Fixture tests cover identity generation and rejection, while a clean-profile install reproduces the published hashes.

### U8. POSIX target-native apply

- **Goal:** Reuse the guarded local executor for SSH without trusting remote task prose.
- **Requirements:** R18-R20; KTD8-KTD10.
- **Dependencies:** U4, U7.
- **Files:** `scripts/machine-utilities`, `scripts/test-machine-utilities`, and affected skill instructions.
- **Approach:** Extract one target-native apply path used by both local and SSH dispatch. The controller sends a private bounded worker config, sealed plan, and exact executor requirement; the target recaptures trusted preflight and verifies its native hostname/user, both config digests, plan bytes/digest, fresh preconditions, operation allowlist, and semantic post-state. SSH uses bounded connection/keepalive timeouts, and cleanup removes the private temporary workspace on success and failure. After an operation or postcondition failure, preserve authoritative partial output with fresh post-inventory whenever possible.
- **Test scenarios:**
  - Wrong hostname/user, worker-config digest, plan digest, executor hash, or precondition fails before argv execution.
  - Unsupported `agent-install`, `agent-remove`, and `chezmoi-add` operations fail sealing rather than failing after approval.
  - A supported Homebrew/APT, agent update, project, auth, or chezmoi fixture uses exact argv and returns authoritative post-state.
- **Verification:** Fixture SSH proves transfer/invocation/cleanup; a reachable-host canary proves identity and a reversible or no-op-safe operation.

### U9. Native Windows worker

- **Goal:** Execute selected winget, agent, project, and chezmoi operations on Windows through a visible saved-project Codex task.
- **Requirements:** R10, R18-R20; KTD4, KTD8-KTD10.
- **Dependencies:** U3-U5, U7.
- **Files:** `scripts/apply-windows.ps1`, `scripts/collect-windows.ps1`, `scripts/test-machine-utilities`, `references/codex-remote-control.md`, and affected skills.
- **Approach:** Keep task creation/waiting/chunk retrieval in Codex instructions and put validation/execution in one PowerShell worker. Require expected Windows hostname/user, exact plan and executor hashes, supported operation shapes, immediate native exit-code capture, fresh pre/post inventory, and structured result metadata. Use the configured native saved project and explicitly reject WSL.
- **Test scenarios:**
  - A configured native Windows host and its configured WSL sibling cannot satisfy each other's platform/identity gates.
  - Exact named winget upgrade succeeds only when the observed candidate is known and post-state equals it.
  - Localized, malformed, or ambiguous winget output records `unknown` and cannot be sealed.
  - Wrong task/project/correlation metadata, missing result chunks, or prose-only success is rejected.
- **Verification:** PowerShell fixture tests plus one visible native Windows canary on the configured saved project.

### U10. CI and structural hardening

- **Goal:** Keep the release maintainable and continuously checked without introducing a framework.
- **Requirements:** R20-R21.
- **Dependencies:** U7-U9.
- **Files:** existing scripts, one Windows worker, and `.github/workflows/test.yml`.
- **Approach:** Reuse the existing self-check and native syntax tools. Split only code that must run natively on Windows; do not add a transport abstraction, daemon, dependency, or workflow DSL. CI runs Bash/ShellCheck on macOS/Linux and PowerShell parse/fixtures on Windows.
- **Test scenarios:** malformed manager JSON, unsafe paths/modes, false-success native commands, no-op agent updates, and unknown winget candidates all fail deterministically.
- **Verification:** Hosted jobs pass on all three operating systems and `git diff --check` is clean.

### U11. Release and publication

- **Goal:** Publish one coherent dual-agent release and prove consumers receive it.
- **Requirements:** R16-R17, R21-R22.
- **Dependencies:** U7-U10.
- **Files:** source manifests, marketplace version/catalog manifests, READMEs, and pull requests.
- **Approach:** Set Machine Utilities to `0.2.0`; keep Agent Utilities at its already prepared coordinated version. Commit and push source changes, resolve review/CI, merge Machine Utilities first, then Agent Utilities, then marketplace metadata. Verify marketplace commit reachability, clean-profile Codex/Claude installation, active version, and script hashes.
- **Test scenarios:**
  - The two source and two marketplace Machine Utilities versions are identical.
  - Both marketplace JSON formats resolve the merged source repository and version.
  - A clean profile installs the exact release in both managers; post-install executor status matches the source tree.
- **Verification:** Merged pull requests, green hosted checks, successful clean-profile installs, and exact released executor evidence.

---

## Verification Contract

| Gate | Applies to | Done signal |
|---|---|---|
| Shell syntax and self-check | U1-U4 | All Bash scripts parse and fixture assertions pass |
| PowerShell parser/fixture check | U3-U4 | Passes when `pwsh` is present; absence is explicitly reported |
| Real local read-only smoke | U2, U6 | JSONL validates and renders without exposing credential values |
| Remote result transport spike | U3, U5 | Representative bounded JSONL returns byte-for-byte or a durable artifact fallback is proven |
| Windows Codex Desktop end to end | U3, U5 | Visible remote task runs the PowerShell collector and returns validated JSONL, including blocked/needs-attention handling |
| Skill validation | U5 | All eight skill folders pass the skill validator |
| Codex plugin validation | U5-U6 | Plugin creator validator passes |
| Claude plugin validation | U5-U6 | Strict Claude validation passes |
| Manifest and diff hygiene | U6 | JSON parsing and `git diff --check` pass |
| Forward test | U5 | Independent agent correctly follows inventory/project workflow without mutations |
| Executor integrity | U7-U9 | Exact version/hashes pass; modified, unsafe, dirty, or stale executors fail before mutation |
| Target-native apply | U8-U9 | Local, SSH, and native Windows workers enforce identical plan/pre/post-state gates |
| Hosted CI | U10 | macOS, Linux, and Windows jobs pass |
| Publication | U11 | Source merges precede marketplace merge; clean Codex/Claude installs report release `0.2.0` and matching hashes |

---

## Definition of Done

- The shared dispatcher supports configuration validation, local collection, rendering, validation, comparison, plan sealing, and precondition verification.
- POSIX and Windows collectors share the documented JSONL semantics; Windows never requires WSL.
- Every remote task completes executor readiness before collection or mutation; sealed mutation plans bind the exact executor identity and refuse unsupported operations.
- SSH and Windows mutations run through target-native workers that enforce host/user, config, plan, precondition, argv, and semantic post-state checks.
- Ambiguous winget output remains `unknown` and cannot authorize an upgrade.
- Hosted macOS, Linux, and Windows checks pass.
- Machine Utilities `0.2.0` is merged, published in the marketplace, installable by Codex and Claude, and verified by exact version/hash evidence.
- The eight skills are registered in both plugin manifests; fleet skills use the shared scripts.
- Project, capability/provenance, authentication, package, startup, and chezmoi records are present with safe evidence labeling.
- Mutations are impossible without explicit scope and stop on identity, ambiguity, approval, or unsafe-state failures.
- Director-facing Codex task lifecycle and saved-project rules are operationally documented in the relevant skills.
- All local gates pass and the enrolled Windows Codex Desktop workflow is verified end to end. If access is unavailable, Windows support remains explicitly unverified and requires the user's documented waiver; no commit, push, installation, remote mutation, or credential copying is performed.
