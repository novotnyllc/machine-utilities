---
name: remote-mac
description: Operate and diagnose configured remote Macs over Tailscale or SSH, with tmux and GUI fallback. Use when a fleet Mac needs inspection, repair, service checks, or remote automation.
---

# Remote Mac

Use the machine inventory from
`${MACHINE_UTILITIES_CONFIG:-${XDG_CONFIG_HOME:-$HOME/.config}/machine-utilities/config.json}`.
Do not assume host names, users, paths, or services. Prefer the requested
machine, then its configured `ssh_alias`, SSH config, and Tailscale state.
Treat inventory as routing hints until `hostname`, `id -un`, `sw_vers`, and
`pwd` confirm the destination.

Use non-interactive SSH for one-shot checks:

```bash
ssh -o BatchMode=yes -o RequestTTY=no -o RemoteCommand=none \
  -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=2 \
  ALIAS 'hostname; id -un; sw_vers'
```

Override aliases that auto-attach tmux or run a remote command. Use a login
shell only when checking developer tools that depend on shell initialization:

```bash
ssh -o BatchMode=yes -o RequestTTY=no -o RemoteCommand=none \
  -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=2 ALIAS \
  'zsh -lc "command -v brew; command -v pnpm; command -v node"'
```

If Tailscale is involved, inspect `tailscale status --json` before trying
mDNS. For long-running or interactive work, create a clearly named remote
tmux session and report the attach command.

For a named service, discover its real launchd label, process, or port from
repo docs, `AGENTS.md`, or the configured project before checking:

```bash
launchctl list
tmux list-sessions
ps axww
lsof -nP -iTCP -sTCP:LISTEN
```

Filter locally without exposing environment values or secrets. Read-only
checks come first. Do not install, start, stop, restart, unload, or edit a
service unless the user asks.

Prefer SSH and service APIs. Use GUI automation only for explicitly GUI-bound
work or a visible security prompt. Capture current state before each action
and verify afterward. Never type or expose a secret in chat; let the user
approve keychain or browser prompts.

If the host is unreachable, report each attempted configured route. Never
substitute WSL for a direct Windows Codex Desktop transport.

Adapted from `steipete/agent-scripts` `skills/remote-mac` at
`6e512e6fe0546471dfce5f48c9896c6ddce669cd` (MIT).
