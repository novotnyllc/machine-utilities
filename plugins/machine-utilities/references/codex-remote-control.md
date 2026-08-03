# Codex Desktop remote control

Use this only for a machine whose configured transport is
`codex-remote-control`. Never substitute WSL or SSH.

## Task-control capability check

Task-control tools may be loaded lazily. Only when the selected targets include
a `codex-remote-control` machine, discover and check the app tools, including
`list_projects`, `create_thread`, and `wait_threads`; reuse that capability
result for the bounded operation. An absent eager tool listing is not evidence
that a tool is unavailable. Classify failure precisely:

- `tool_surface_missing`: lazy discovery proves a required app tool is absent;
- `host_offline`: `list_projects` reports the configured host in
  `unavailableHosts` or its source as unreachable;
- `saved_project_missing`: the host and source are reachable, but
  `list_projects` has no saved project matching both the configured
  remote-control host and exact native path;
- `native_evidence_unavailable`: only WSL evidence exists for the configured
  native Windows target;
- `task_creation_failed`: `create_thread` fails for the matched project or its
  client task never resolves through `wait_threads`;
- `executor_mismatch`: the task runs but the installed executor version or
  integrity hashes do not match; or
- `executor_or_plugin_failure`: the verified task runs but a manager command
  or requested-version postcondition fails.

Do not collapse these states into a generic task-control failure.

## Routine named-plugin refresh

An explicit named-plugin refresh does not require a full native inventory or
sealed plan. Resolve the host, saved project, plugin, marketplace, requested
version, and applicable Codex harness from the controller's configured scope.
Use the capability check above, create a visible task in the configured saved
project, and run native PowerShell only. Capture `codex plugin list --json`
before and after, retaining the exact `PLUGIN@MARKETPLACE` record. In the task,
run only these commands in order:

```powershell
codex plugin marketplace upgrade MARKETPLACE --json
codex plugin add PLUGIN@MARKETPLACE --json
```

Require the post-state record to be installed, enabled, and equal the requested
version. Do not use WSL, update another plugin, synchronize settings or skills,
or claim success from manager output alone. Record a failed postcondition as
`executor_or_plugin_failure` while preserving before-state and command output;
keep executor version/hash mismatch separate as `executor_mismatch` only when
an executor load or verification was attempted. This manager-native refresh
does not require or preflight the Machine Utilities executor, because that
would prevent it from repairing a stale Machine Utilities installation.
Claude is not applicable to `codex-remote-control`; do not infer its state from
WSL.

Use the full protocol below for inventory, broad reconciliation, settings,
provenance, conversions, ambiguous scope, and sealed-plan mutations.

1. Treat the initiating machine's config as authoritative. Resolve one host,
   one section, and the applicable projects/policy locally; include that
   bounded JSON object and the raw-config SHA-256 in the task prompt.
2. Read `integrity.json` from the authenticated merged source and record its
   exact plugin version, manifest SHA-256, and ordered file hashes. Executor
   inspection is read-only: on the target, locate the active
   `machine-utilities@novotnyllc` cache and run
   `pwsh -NoProfile -File scripts/apply-windows.ps1 -VerifyExecutor
   -ExecutorRequirementPath executor.json`. If the version or any hash differs, return
   `executor_update_required` and run no collector or mutation.
3. Updating the executor is a separately approved bootstrap action. Use
   `codex plugin marketplace upgrade novotnyllc --json` followed by
   `codex plugin add machine-utilities@novotnyllc --json`. For Claude local or
   SSH use `claude plugin marketplace update novotnyllc` followed by
   `claude plugin update machine-utilities@novotnyllc --scope user`. End the
   bootstrap task and start a fresh task before loading any Machine Utilities
   skill or script.
4. Use Codex Desktop project discovery. Match the configured host and the
   environment-native project path. If no saved project matches, stop and tell
   the user to add that checkout as a project in Codex Desktop on the target.
   Record `available`, `missing`, or `unreachable` with the opaque host/project
   IDs, configured host ID, exact native path, and expected source in a
   mode-0600 metadata file, then use `machine-utilities
   record-codex-readiness`; never inspect or edit Codex's internal databases.
5. Create a visible task against that saved project using its local checkout,
   not a new worktree: inventory must observe the real host.
6. Tell the task to use the verified installed Machine Utilities collector, native
   PowerShell on Windows, and no WSL. Request one inventory section per task.
   Pass the initiating config's raw SHA-256 as `-ControllerConfigDigest`; the
   worker records both that digest and its bounded worker-config digest. Pass
   `-AllowAuthVerify` only for an explicitly requested auth inventory after
   the bounded config and controller digest have been verified.
   The task writes the complete JSONL to a private target-local temporary file
   and computes its byte count, record count, and SHA-256.
7. If setup returns a client task ID, wait for the real task ID before using
   task tools. Wait in bounded intervals. Leave approvals and needs-attention
   prompts to the user.
8. If the payload is at most 48 KiB, return it with its byte count and SHA-256.
   Otherwise return only a manifest, then use follow-up messages on the same
   task to retrieve numbered chunks of at most 48 KiB. Each chunk carries its
   index, byte count, and SHA-256. Concatenate in order, verify the full byte
   count and SHA-256, and tell the task to delete the temporary file.
9. Validate every returned JSONL record locally. Reject a wrong schema,
   config digest, host ID, section, oversized record, missing/duplicate chunk,
   or truncated response. Never merge an unvalidated partial response into a
   good snapshot.
10. Enrich the returned project and operation records with the real saved-project,
   task, and correlation IDs using `record-codex-readiness`.

For an approved mutation, generate the bounded target config with
`machine-utilities worker-config HOST DOMAIN OUTPUT`, seal the plan, and send
only that config, the plan, the controller-derived executor status, and their
byte counts/SHA-256 values. The native task must verify its configured
`expected_hostname` and `expected_user`, then run `apply-windows.ps1` with the
exact plan-file SHA-256 and plan ID. Accept success only from validated JSONL
containing the matching final `apply:PLAN-ID` record, executor hashes, both
configuration digests, and semantic post-state. Prose is never success
evidence. The worker recaptures its own trusted preflight. If an operation or
postcondition fails, it stops the remaining operations and returns an
authoritative partial result with fresh post-inventory whenever collection is
still possible; validate and preserve that evidence even though the task
failed.

The controller does not need a local copy of the remote path. Task creation
uses the saved remote project. Cross-host handoff is separate and requires the
same repository to be saved on both source and destination hosts.

Project work is handed off through that project's Git repository and exact
commit. An optional private coordination repository may track pointers and
status across projects, but it does not replace their repositories or provide
their working trees. Never use an unrelated development checkout as the
control project merely because it already exists on the destination.

The native Windows saved-project workflow was exercised on 2026-07-30 with
small byte-for-byte payloads and a 323-record inventory split and exactly
reassembled at record boundaries. Revalidate the protocol if the Desktop task
result limits or task-control surface change.
