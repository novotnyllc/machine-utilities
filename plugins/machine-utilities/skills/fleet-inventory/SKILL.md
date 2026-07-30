---
name: fleet-inventory
description: Inventory and compare a configured fleet of macOS, Linux, WSL, and Windows machines. Use for packages, agent runtimes, plugins, standalone skills and provenance, auth-file metadata, projects, startup tasks, chezmoi state, SHA-256 comparisons, install-time inference, or human and JSONL status reports.
---

# Fleet Inventory

Set `SKILL_DIR` to the absolute directory containing this loaded `SKILL.md` and
`CLI="$SKILL_DIR/../../scripts/machine-utilities"`; the shell working directory
is not the skill directory. Start with `"$CLI" validate-config`, resolve
requested host names or groups from the config, then collect only the sections
needed. Default to all configured hosts and a human report; preserve the JSONL
snapshot when another agent will consume it.

For `local` and `ssh` targets, run `collect --target HOST --section SECTION`.
Exit 2 means a usable partial snapshot; show its errors instead of discarding
valid records. Use `validate`, `render`, and `compare` rather than parsing the
records ad hoc.

Inventory is private but not cosmetically redacted: show operational values the
owner requested. Never include credential contents, tokens, environment values,
or authenticated Git URL credentials. Credential records may include path,
owner, mode, size, mtime, SHA-256, strategy, and native health. A Codex plugin
cache directory birth time is only `inferred_installed_at`, never an
authoritative install date.

For `codex-remote-control`, Codex must read and follow
`"$SKILL_DIR/../../references/codex-remote-control.md"`. Claude must report that
transport as unsupported. Never route Windows through WSL unless the config
explicitly chooses a different transport.

Conclude with observed drift, unavailable evidence, and exact next actions.
Do not mutate anything from this skill.
