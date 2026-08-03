# Automation SSH identity

Privileged fleet planning uses a node-local identity file separate from the
portable fleet configuration. Set `MACHINE_UTILITIES_IDENTITY`, or create
`identity.json` beside the default `config.json`. Keep it outside repositories,
plugin caches, and synced folders with an owner-only private key.

The version 1 identity contains the fleet domain, node ID, absolute private-key,
certificate, and dedicated known-hosts paths, expected node-key and fleet-CA
SHA256 fingerprints, CA generation, certificate serial and validity, and local
enrollment receipts. `worker-config` never projects this file or its paths.

Portable `automation_transport` routes name an explicit host, port, request
user, pinned host-key fingerprint, and management networks. They do not accept
SSH aliases, options, proxy or local commands, control sockets, or credential
bytes. The automation client must bypass ordinary SSH configuration and agents.

Enrollment and protected policy activation remain human operations. Editing a
portable `policy_proposal` only prepares a candidate; it never changes the
root- or Administrator-owned active generation.
