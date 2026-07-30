---
name: fleet-chezmoi
description: Inspect, compare, and deliberately reconcile chezmoi source and live-state drift across configured machines. Use for chezmoi status, diff, pull, add, apply, source-repository drift, or post-apply verification.
---

# Fleet Chezmoi

Set `SKILL_DIR` to the absolute directory containing this loaded `SKILL.md` and
`CLI="$SKILL_DIR/../../scripts/machine-utilities"`; the shell working directory
is not the skill directory. Collect the `chezmoi` section, then use native
read-only commands such as `chezmoi status`, `chezmoi diff`,
`chezmoi source-path`, and Git status in the source repository. Determine
whether source, live state, or both changed; do not assume the source always
wins.

Default to a plan per host:

- source should win: preview `chezmoi apply --dry-run --verbose`;
- live state should win: list exact `chezmoi add` targets;
- remote source advanced: plan a clean fast-forward pull before apply;
- both changed or source is dirty/diverged: stop for reconciliation.

Before apply, verify host identity, preserve a diff/backup, and seal the exact
approved operations with `"$CLI" seal-plan DRAFT SNAPSHOT PLAN`. Recapture
chezmoi inventory and require
`"$CLI" verify-preconditions PLAN CURRENT-SNAPSHOT` to succeed immediately
before mutation. Obtain separate approval and execute only the sealed argv.
Never force-reset, auto-commit, or reveal template secret values.

Use local/SSH execution. If a Windows host is configured for Codex remote
control, read and follow
`"$SKILL_DIR/../../references/codex-remote-control.md"`; Claude reports
unsupported and no WSL fallback is allowed. Verify `chezmoi status`, rendered
diff, source Git state, and relevant auth/tool health afterward.
