# Fix launchd collector same-loop failure modes

## Goal

Close exactly two review-proven failure modes in the current launchd startup-task loop while preserving the compact partial-record fallback already implemented on this branch.

## Scope

- Modify only `plugins/machine-utilities/scripts/collect-posix` and `plugins/machine-utilities/scripts/test-machine-utilities`.
- Do not change manifests, versions, marketplace metadata, release files, global hashing helpers, or unrelated collectors.
- Do not introduce a new abstraction.
- The host caller will refresh generated `plugins/machine-utilities/integrity.json` after integration.

## Required behavior

1. A nonzero `plutil` must be treated as failure even if it writes syntactically valid JSON. Use a local pipeline-status mechanism such as a `pipefail` command-substitution subshell; do not enable global `pipefail` for the script.
2. A per-plist digest failure must not terminate the loop. Guard the existing `sha256_file` call locally; do not change `sha256_file` itself.
3. Parse before hashing so malformed or unreadable definitions do not incur a full-file hash read. Only a successful parse and successful digest produce a `present` record.
4. Either failure uses the existing single compact fallback: `status=partial`, internal `definition={}`, `data.definition_digest=null`, the existing structured `launchd_definition_parse_failed` warning, and loop continuation.
5. Preserve output order and prove that a later good launchd plist and the later cron record are still emitted.

## Verification

- Extend the fake-Darwin fixture to cover both: (a) `plutil` writes valid JSON then returns nonzero; and (b) hashing fails for a parseable plist. Each must emit a partial record with null digest and the existing error.
- Retain a later good plist that emits `present` with a valid SHA-256 digest, followed by the cron record.
- Run `bash -n` on both changed scripts.
- Run ShellCheck at warning level on both changed scripts when available.
- Run the focused fake-Darwin direct collection and validation proof.
- Keep the resulting diff limited to the two scoped scripts.

