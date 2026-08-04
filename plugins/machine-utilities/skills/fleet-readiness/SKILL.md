---
name: fleet-readiness
description: Assess and reconcile project, agent, plugin, skill, authentication, and host readiness across configured machines. Use before cross-host task placement, when fleet capabilities may be missing or inconsistent, or when a user asks whether machines are ready for work.
---

# Fleet Readiness

Own the readiness question, not the underlying reconciliation mechanics.
Determine the required hosts and capabilities, invoke the narrow Machine
Utilities skills below, and synthesize each host as `ready`, `not ready`, or
`unknown` with evidence and the next action. Never rename the task. When Task
Orchestrator invokes this skill, retain the parent-assigned title.

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
execution as a remote agent. A visible Codex task covers only ordinary native
Windows work. Protected or logged-off Windows work requires fresh
`privilege_broker` readiness from the enrolled `windows-sftp` route; never
substitute the visible task, WSL, or another transport.

For macOS, report a separate root-broker state only when readiness advertises
the owner-enrolled, default-disabled `macos.install-signed-pkg.v1` or
`macos.apply-system-setting.v1` action. SSH is not elevation; root Homebrew,
arbitrary `sudo`, installer scripts, and arbitrary plist paths are unsupported.

Report the exact configured nodes checked, requirements, evidence, changes,
unknowns, and any restart or saved-project action still required. When the
request requires fleet-wide parity, verify every configured node.
