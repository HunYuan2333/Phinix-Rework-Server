# Phinix-Rework-Server

Owns server and server plugins. Read docs/Design-Philosophy.md and docs/Compatibility-Boundaries.md before changing boundaries or recovery. Preserve plugin parity, authoritative acknowledgements, all-or-nothing item delivery and main-thread game dispatch. Preserve dirty changes. Never commit credentials, server data/logs, GameDlls or build output. Do not edit vendored protobuf SDK pins/source for compatibility. Shared consumers pin exact gitlinks, never update --remote during normal acquisition. No additional Phinix NuGet SDK channel.

## Project naming / 项目命名

The mod is **Phinix Rework** (`phinix-rework`), distinct from the original Phinix project. The canonical repositories are `HunYuan2333/Phinix-Rework` (client), `HunYuan2333/Phinix-Rework-Common` (shared contracts/source), and `HunYuan2333/Phinix-Rework-Server` (dedicated server). Use these names in repository URLs, clone instructions and project documentation; do not refer to this project as the original Phinix. Preserve existing assembly names, namespaces, protocol identifiers, mod package IDs, persisted keys and the submodule directory `Dependencies/Phinix.Common` for compatibility. A repository rename does not authorize changing those identifiers.

模组统一称为 **Phinix Rework**（`phinix-rework`），与原 Phinix 项目区分。客户端、共享层、服务端仓库分别为 `Phinix-Rework`、`Phinix-Rework-Common`、`Phinix-Rework-Server`；仓库链接、clone 说明和文档使用统一名称。改仓库名不改程序集、命名空间、协议标识、模组 packageId、持久化键或 `Dependencies/Phinix.Common` 子模块目录，避免破坏兼容性。
