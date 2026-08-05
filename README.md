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

Nothing retains this name: roundhouse's executor, namespaces, and config
paths are all roundhouse-native. History for everything that moved lives in
this repository up to the retirement commit.
