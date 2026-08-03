---
title: "fix: Preserve launchd partial inventory"
date: 2026-08-03
type: fix
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-plan-bootstrap
execution: code
---

# fix: Preserve launchd partial inventory

## Goal Capsule

- **Objective:** Keep POSIX startup inventory usable when one macOS launchd property list cannot be read or parsed.
- **Authority:** The current task's settled failure shape and this repository's existing JSONL and fixture conventions govern the change.
- **Execution profile:** One proof-first implementation unit changes the launchd record path and its deterministic fixture coverage.
- **Stop condition:** A fake-Darwin regression proves that a bad plist produces one valid partial `startup_task` record and does not suppress later JSONL records, while the repository validation gates pass.
- **Tail ownership:** `ce-work` returns implementation and local verification to LFG. LFG owns simplification, review, commit, PR, and CI settlement; Goal Driven Delivery owns authorized merge and post-merge proof.

---

## Product Contract

### Summary

An unreadable or malformed launchd plist is a record-local inventory failure. It must not abort the full collector or discard facts that can still be observed.

### Requirements

- R1. When `plutil` cannot provide one valid JSON object for a launchd plist, emit that plist's `startup_task` as `partial` with error code `launchd_definition_parse_failed` and a message that the property list could not be read or parsed.
- R2. Preserve the existing record schema. Use an empty internal parsed definition, omit parsed plist-derived fields, and set `data.definition_digest` to `null`; do not add a nested `definition` field.
- R3. Continue the launchd loop and the remainder of the startup section after the bad plist so subsequent JSONL records remain available and valid. The direct `collect-posix` process completes successfully; the higher-level CLI may still classify the usable snapshot as partial.
- R4. Keep successful launchd records unchanged, including their raw-byte SHA-256 digest and parsed definition fields.
- R5. Do not change the global `sha256_file` helper or broaden failure handling outside the launchd `startup_task` path.
- R6. Add a deterministic fake-Darwin regression with a failing `plutil` fixture. The test must validate the partial record shape and prove a later record is emitted.
- R7. Keep the delivery scoped to the two named scripts plus the mechanically required `integrity.json` refresh. Do not bump the plugin version, publish a release, or roll out to fleet hosts.

### Acceptance Examples

- A launchd plist whose conversion fails yields one valid record with the filename-derived label, `status:"partial"`, `data.definition_digest:null`, and a non-empty structured error array.
- A second launchd plist ordered after the failing plist and the later cron record both appear, proving the launchd loop and the remainder of the startup section continued rather than merely returning parseable truncated output.
- A successfully converted plist still yields `status:"present"`, its parsed launchd fields, and its SHA-256 definition digest.

### Scope Boundaries

- In scope: `plugins/machine-utilities/scripts/collect-posix`, `plugins/machine-utilities/scripts/test-machine-utilities`, and the generated integrity entry required by CI.
- Out of scope: global hashing behavior, Windows startup inventory, record-schema additions, version metadata, marketplace metadata, release publication, and fleet rollout.

---

## Planning Contract

### Key Technical Decisions

- KTD1. **Contain the failure at one launchd record** (session-settled: required) — detect digest and plist conversion/parsing failure inside the launchd loop, emit a partial record, and continue.
- KTD2. **Preserve the existing flattened data shape** (session-settled: required) — the internal parsed definition falls back to `{}`, but no new `.data.definition` field is introduced.
- KTD3. **Use one unambiguous digest state for every definition failure** (session-settled: required) — set `definition_digest:null` whenever launchd definition parsing fails, including readable-but-unparseable plists, so a digest cannot be mistaken for a validated definition and empty or pipeline-masked hash output is never trusted.
- KTD4. **Prove the full failure chain with portable fixtures** (session-settled: required) — fake Darwin, launchd tooling, and `plutil` in the existing Bash fixture suite so both Linux and macOS CI exercise the behavior deterministically.

### Implementation Constraints

- Preserve Bash 3.2-compatible syntax and the script's `set -eu` behavior.
- Keep JSONL on stdout data-only; fixture diagnostics stay outside the collected output.
- Use the existing `emit` envelope and structured error conventions.
- Do not rely on the host's real launch agents, `plutil`, `launchctl`, or crontab state.
- Start with the regression and observe its expected failure before changing collector behavior.

---

## Implementation Units

### U1. Make launchd plist failures record-local

- **Goal:** Emit trustworthy partial launchd inventory and preserve later records when one plist cannot be read or parsed.
- **Requirements:** R1-R7; KTD1-KTD4.
- **Dependencies:** None.
- **Files:** `plugins/machine-utilities/scripts/collect-posix`, `plugins/machine-utilities/scripts/test-machine-utilities`, generated `plugins/machine-utilities/integrity.json`.
- **Approach:** Have `test-machine-utilities` generate deterministic fake-Darwin `uname`, `plutil`, `launchctl`, and `crontab` commands at runtime in its temporary fixture bin; add a focused startup collection assertion that fails on the current collector, then make digest and plist parsing explicit guarded operations inside the launchd loop. Build the record from the available path/mtime/label facts, with partial status and structured error when definition bytes or conversion are unavailable. Refresh integrity only after the behavior and script checks pass; add no tracked fixture files.
- **Execution note:** Proof first. Run the new regression against the original collector and record the expected nonzero/truncated behavior before editing `collect-posix`.
- **Test scenarios:**
  - Failing `plutil` yields exactly one partial launchd task with filename-derived label, no parsed definition fields, null digest, and error code `launchd_definition_parse_failed`.
  - A second, ordered launchd plist and a deterministic later cron record are both present in the same valid JSONL output.
  - The fake-Darwin direct `collect-posix` invocation exits zero, and the test harness validates the output with `plugins/machine-utilities/scripts/machine-utilities validate <fixture-jsonl>`.
  - Existing fixture inventory and successful platform behavior remain green.
- **Verification:** Run the focused regression during proof-first work, then the full fixture suite and CI-equivalent static/integrity checks.

---

## Verification Contract

- **Syntax:** `bash -n plugins/machine-utilities/scripts/collect-posix plugins/machine-utilities/scripts/test-machine-utilities`
- **Static analysis:** `shellcheck --severity=warning plugins/machine-utilities/scripts/collect-posix plugins/machine-utilities/scripts/test-machine-utilities`
- **Behavior:** `plugins/machine-utilities/scripts/test-machine-utilities`
- **Integrity:** `plugins/machine-utilities/scripts/update-integrity` followed by `git diff --exit-code -- plugins/machine-utilities/integrity.json` after the refreshed manifest is staged in the final diff.
- **Scope:** The final diff contains only the two named scripts and `plugins/machine-utilities/integrity.json`; `plugins/machine-utilities/.codex-plugin/plugin.json` and `plugins/machine-utilities/.claude-plugin/plugin.json` both remain at version `0.2.11`.
- **No browser gate:** This is a shell collector change with no browser-routable surface.

---

## Definition of Done

- U1 satisfies every test scenario with observed evidence, including the pre-fix regression failure and post-fix pass.
- An unreadable or unparseable launchd plist emits a valid partial record with empty parsed definition contribution, null definition digest, and an explicit error.
- The same collection emits a later record and validates as usable JSONL.
- Syntax, static analysis, full fixture, integrity, and scope gates pass.
- The final review finds no unresolved correctness, regression, or scope issue.
- No abandoned experiment remains in the diff.
- No plugin version, marketplace metadata, release state, or fleet host has changed.
