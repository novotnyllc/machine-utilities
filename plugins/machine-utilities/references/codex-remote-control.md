# Codex Desktop remote control

Use this only for a machine whose configured transport is
`codex-remote-control`. Never substitute WSL or SSH.

## Task-control capability check

Task-control tools may be loaded lazily. Only when the selected targets include
a `codex-remote-control` machine, discover and check the app tools, including
`list_projects`, `create_thread`, `wait_threads`, and `set_thread_archived`;
reuse that capability
result for the bounded operation. Reuse only the tool-availability result,
never a `list_projects` response or project ID. An absent eager tool listing is not evidence
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
- `task_cleanup_failed`: the completed task cannot be archived, or unexpected
  task-owned worktree cleanup cannot finish safely;
- `executor_mismatch`: the task runs but the installed executor version or
  integrity hashes do not match; or
- `executor_or_plugin_failure`: the verified task runs but a manager command
  or requested-version postcondition fails.

Do not collapse these states into a generic task-control failure.

## Fresh-task project binding

Every remote-control operation starts a new visible task. Never resume,
unarchive, or send a follow-up to an older task as a recovery path, even when
that task used the same host, project, or operation type.

Do not use `list_threads`, `read_thread`, `set_thread_archived`, `fork_thread`,
`handoff_thread`, or `send_message_to_thread` to select or recover the task.
Follow-ups are allowed only on the new task created for the current operation.

Call `list_projects` immediately before creation and select exactly one object
whose `hostDisplayName` matches the configured `codex_host` and whose
environment-native path matches the configured project path. Retain its opaque
`hostId`. Pass that same object's `projectId`
verbatim to `create_thread` with `environment: { type: "local" }`; do not type,
reconstruct, cache, or copy an ID from prose, memory, readiness metadata,
config, or an earlier listing. Keep the selected object and the creation result
together as evidence.

If `create_thread` returns `Unknown projectId`, call `list_projects` once more
and discard every prior project object and ID. Rematch the exact host and path,
then retry once with that newly returned object's `projectId`, whether or not
the value changed. If either call used an ID other than the same response's
matched object, record a controller invocation error. If the fresh rematch is
missing or unreachable, classify that exact state; if the one retry fails,
classify `task_creation_failed`. Do not reuse an old task or substitute another host.
When creation returns a client task ID, wait only for that setup to yield its
real task and host IDs; all waits and follow-ups must remain correlated to that
new task.

## Parent-owned task cleanup

The controller that creates the task owns its full lifecycle. After it captures
and validates the terminal result, deletes any task temporary payload, and
needs no further follow-up, it archives that exact new task in the same
operation. Never leave a successfully completed child visible for later reuse.

`environment: { type: "local" }` uses the saved checkout and normally creates
no task worktree. If a task nevertheless reports an owned worktree, do not assume archive removes it: verify its changes are integrated or explicitly
handed off, then use the host's supported handoff or worktree cleanup and wait
for success before archiving. Never use raw filesystem deletion or force
cleanup of dirty or unintegrated work. Leave the task and worktree visible and
record `task_cleanup_failed` on conflict or cleanup failure. Archive failure
also records `task_cleanup_failed`; it never authorizes reuse or unarchiving of
an older task.

## Routine named-plugin refresh

An explicit named-plugin refresh does not require a full native inventory or
sealed plan. Resolve the host, saved project, plugin, marketplace, requested
version, and applicable Codex and Claude harnesses from the controller's
configured scope. Use the capability check and fresh-task binding above,
create a new visible task in the configured saved project, and run native PowerShell only.
Before and after each
applicable harness, capture `codex plugin list --json`; for Claude, capture
`claude plugin list --json`. Retain the exact `PLUGIN@MARKETPLACE` record and,
for Claude, compare every non-target record by `id`, `version`, `enabled`, and
`scope` and require the unrelated plugin diff to be empty. In the task, run
only these mutation commands in order for each applicable harness:

```powershell
# Codex
codex plugin marketplace upgrade MARKETPLACE --json
codex plugin add PLUGIN@MARKETPLACE --json

# Claude
claude plugin marketplace update MARKETPLACE
claude plugin update PLUGIN@MARKETPLACE --scope user
```

The Codex add is idempotent. For Claude, use `plugin update` when the exact
plugin is installed; if it is absent, replace only that second Claude command
with `claude plugin install PLUGIN@MARKETPLACE --scope user`. Require each
matching post-state record to be present, enabled, and equal the requested
version; the Codex record must be installed and the Claude record must have
user scope. If native PowerShell cannot find
`claude`, mark only the Claude harness unavailable. WSL or SSH are prohibited.
Treat Codex and Claude harness failures independently: preserve each before/after
record and attempt the other applicable harness. Do not update a runtime,
settings, skills, provenance, or another plugin, and do not claim success from
manager output alone. Record a failed postcondition as
`executor_or_plugin_failure` while preserving before-state and command output;
keep executor version/hash mismatch separate as `executor_mismatch` only when
an executor load or verification was attempted. This manager-native refresh
does not require or preflight the Machine Utilities executor, because that
would prevent it from repairing a stale Machine Utilities installation.

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
