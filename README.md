# machine-utilities (retired)

This plugin has been split and retired.

- Fleet readiness, inventory, projects, agents, auth, updates, chezmoi,
  remote transport (`remote-mac`, `ssh-doctor`), the fleet CLI, enrollment,
  privilege-broker machinery, and `unifi-network-api` moved to
  **[roundhouse](https://github.com/novotnyllc/roundhouse)** — machine and
  infrastructure administration.
- Delivery, routing, and orchestration live in
  **[yardmaster](https://github.com/novotnyllc/yardmaster)**; craft skills in
  **[agent-utilities](https://github.com/novotnyllc/agent-utilities)**.

Enrolled hosts keep the `machine-utilities` system namespace
(`/usr/local/libexec/machine-utilities`, the sudoers broker,
`/etc/machine-utilities/ssh`, `/var/lib/machine-utilities*`); roundhouse ships
the same executor under its legacy name and documents the re-enrollment
migration. History for everything that moved lives in this repository up to
the retirement commit.
