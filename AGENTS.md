# AGENTS.md

## Repo Purpose

This repository owns the `machine-utilities` plugin for Codex and Claude Code.
It orchestrates fleet maintenance and owns fleet-aware single-host transport
and SSH diagnosis.

## Release Coupling

When changing the plugin version, update:

- `plugins/machine-utilities/.codex-plugin/plugin.json`
- `plugins/machine-utilities/.claude-plugin/plugin.json`
- `<marketplace-repo>/.agents/plugins/plugin-versions.json`
- `<marketplace-repo>/.claude-plugin/marketplace.json`

When the plugin is newly added or renamed, also update:

- `<marketplace-repo>/.agents/plugins/marketplace.json`
- `<marketplace-repo>/.claude-plugin/marketplace.json`
- `<marketplace-repo>/README.md`

Never treat an installed plugin cache as the source repository.

## Skill Rules

- Keep skills usable by both Codex and Claude Code.
- Do not hard-code secrets, host names, users, vault names, addresses, or
  machine inventory.
- Read inventory from `MACHINE_UTILITIES_CONFIG`, then
  `${XDG_CONFIG_HOME:-$HOME/.config}/machine-utilities/config.json`.
- Default to audit/report mode. Mutations require an explicit user request,
  target resolution, identity verification, preflight, and post-change checks.
- Reuse existing chezmoi and package-manager commands instead of reimplementing
  them.
- Keep fleet-aware SSH diagnosis and remote-machine mechanics in this plugin.
- Validate JSON manifests and skill frontmatter before committing.
