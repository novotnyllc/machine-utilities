---
name: fleet-update
description: Plan and explicitly apply package updates across Homebrew, APT, and winget machines. Use for fleet patching, outdated-package reports, manager-specific updates, package drift, or post-update verification.
---

# Fleet Update

Set `SKILL_DIR` to the absolute directory containing this loaded `SKILL.md` and
`CLI="$SKILL_DIR/../../scripts/machine-utilities"`; the shell working directory
is not the skill directory. Resolve exact hosts or groups from the user config
and inventory the package section first. Default to a read-only plan:

- Homebrew: `brew update` changes metadata, so ask before running it; use
  `brew outdated --json=v2` for the plan and `brew upgrade` only for approved
  formulae/casks.
- APT: `apt-get update` changes metadata, so ask first; plan with
  `apt-get --simulate upgrade`. Do not use `full-upgrade`, `dist-upgrade`, or
  `autoremove` unless explicitly selected.
- winget: plan with `winget upgrade --accept-source-agreements
  --disable-interactivity`; apply only named approved packages, or `--all` only
  when the user approves that exact scope.

Present exact host, manager, package, current version, candidate version, and
command. Put those inert operations in a plan draft and run
`"$CLI" seal-plan DRAFT SNAPSHOT PLAN`. Before apply, verify live host and
platform identity, recapture package inventory, and require
`"$CLI" verify-preconditions PLAN CURRENT-SNAPSHOT` to succeed. This binds
config, plan integrity, and preconditions without executing plan text. Then
obtain separate user approval and execute only the exact argv sealed in the
plan. Never infer apply permission from a request to inspect or plan.

Run each native manager directly on local/SSH hosts. For Windows, Codex reads
and follows `"$SKILL_DIR/../../references/codex-remote-control.md"`; Claude
reports unsupported. Never fall back through WSL. Preserve native approval
prompts, stop per host on failure, and recapture package inventory afterward.
Cleanup and autoremove are separate explicit actions.
