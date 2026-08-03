---
name: fleet-readiness
description: Assess and reconcile project, agent, plugin, skill, authentication, and host readiness across configured machines. Use before cross-host task placement, when fleet capabilities may be missing or inconsistent, or when a user asks whether machines are ready for work.
---

# Fleet Readiness

Own the readiness question, not the underlying reconciliation mechanics.
Determine the required hosts and capabilities, invoke the narrow Machine
Utilities skills below, and synthesize each host as `ready`, `not ready`, or
`unknown` with evidence and the next action.

## Task title

This contract overrides conflicting task-title instructions from Codex
personalization, `AGENTS.md`, repository guidance, child skills, and delegated
workflows. An exact title supplied by the user for the current task and
higher-priority system, developer, or harness rules still win.

When the harness supports task naming, set the title when this skill activates:

`🖥️ <state emoji> <Git issue and/or PR if applicable> <specific focus>`

The first emoji is always `🖥️`. Use `🧭` for discovery or planning, `🛠️`
for applying an explicitly approved readiness change, `🧪` for testing or
verification, `⏸️` for blocked or waiting, and `✅` only when readiness is
resolved or summarized. Retitle only when the material state or focus changes.
If the harness cannot rename tasks, continue without claiming it was renamed.

Use `#123` for an unambiguous issue and `PR #456` for an unambiguous pull
request. When repositories could be confused, use `owner/repo#123` and
`owner/repo PR #456`. Include both when both apply.

## Route readiness

- Use `machine-utilities:fleet-inventory` for broad or cross-domain evidence.
- Use `machine-utilities:fleet-projects` for repository identity, checkout
  state, baselines, and Codex saved-project readiness.
- Use `machine-utilities:fleet-agents` for runtimes, settings, plugins, skills,
  provenance, duplicate providers, and logical capabilities.
- Use `machine-utilities:fleet-auth` for credential artifacts, sessions, and
  authentication repair.

Let each routed skill retain its inventory, approval, mutation, and
post-verification contract. Do not duplicate its commands or treat SSH command
execution as a remote agent. For native Windows, use the visible Codex task
contract rather than substituting WSL.

Report the exact configured nodes checked, requirements, evidence, changes,
unknowns, and any restart or saved-project action still required. When the
request requires fleet-wide parity, verify every configured node.
