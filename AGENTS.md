# Phinix-Server

Owns server and server plugins. Read docs/Design-Philosophy.md and docs/Compatibility-Boundaries.md before changing boundaries or recovery. Preserve plugin parity, authoritative acknowledgements, all-or-nothing item delivery and main-thread game dispatch. Preserve dirty changes. Never commit credentials, server data/logs, GameDlls or build output. Do not edit vendored protobuf SDK pins/source for compatibility. Shared consumers pin exact gitlinks, never update --remote during normal acquisition. No additional Phinix NuGet SDK channel.
