# machine-utilities (retired)

This plugin has been split and retired.

- Fleet readiness, inventory, projects, agents, auth, updates, chezmoi,
  remote transport (`remote-mac`, `ssh-doctor`), the fleet CLI, enrollment,
  and privilege-broker machinery moved to
  **[yardmaster](https://github.com/novotnyllc/yardmaster)** — the delivery
  system for agent work.
- `unifi-network-api` moved to
  **[agent-utilities](https://github.com/novotnyllc/agent-utilities)** — the
  craft-skill toolbox.

Enrolled hosts keep the `machine-utilities` system namespace
(`/usr/local/libexec/machine-utilities`, the sudoers broker,
`/etc/machine-utilities/ssh`, `/var/lib/machine-utilities*`); yardmaster ships
the same executor under its legacy name and documents the re-enrollment
migration. History for everything that moved lives in this repository up to
the retirement commit.
