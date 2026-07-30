---
name: fleet-projects
description: Inventory and prepare configured Git repositories and Codex saved-project readiness across machines. Use for missing clones, wrong origins, branch or dirty-state drift, development-root discovery, distributed task placement, Codex project registration, or cross-host handoff readiness.
---

# Fleet Projects

Set `SKILL_DIR` to the absolute directory containing this loaded `SKILL.md` and
`CLI="$SKILL_DIR/../../scripts/machine-utilities"`; the shell working directory
is not the skill directory. Collect the `projects` section with `"$CLI"`.
Evaluate configured path, expected source, sanitized origin, HEAD/tree IDs,
branch, dirty count, host groups, and whether Codex exposes the
environment-native checkout as a saved project. Git readiness and Codex
readiness are separate.

Default to a plan:

- missing checkout: create the configured parent and clone the configured
  source into the exact path;
- wrong origin or non-repository path: stop for user choice;
- clean checkout behind its upstream: allow fetch plus fast-forward-only pull;
- dirty, detached, ahead, or diverged checkout: report it; never reset, stash,
  switch, merge, or discard work automatically.

Before apply, verify live host/platform and the destination parent, then seal
the exact source/path operations with
`"$CLI" seal-plan DRAFT SNAPSHOT PLAN`. Recapture project inventory and require
`"$CLI" verify-preconditions PLAN CURRENT-SNAPSHOT` to succeed. Obtain
separate approval, execute only the sealed argv, and inventory again afterward.

For Codex readiness, discover saved projects and match both host and native
path. The controller does not need that path locally. If missing, tell the user
to add the exact checkout in Codex Desktop on that host; do not edit Codex
internal databases. Record the discovery result and any real task/correlation
IDs in an owner-controlled mode-0600 metadata file containing `host_id`,
configured project name, `available`/`missing`/`unreachable`, configured
`codex_host`, exact observed native path and expected source, plus opaque
project/task/correlation IDs when available. Then run
`"$CLI" record-codex-readiness SNAPSHOT METADATA OUTPUT` so Director receives
the controller-observed status in canonical JSONL. Direct Windows task creation follows
`"$SKILL_DIR/../../references/codex-remote-control.md"` and never WSL.
Cross-host handoff requires matching saved-project repository identity at both
ends; creating a remote task directly only requires the destination saved
project.
