# Phinix-Server

Phinix 独立服务端，包含服务端网络适配、宿主及官方服务端插件。

Dedicated server host, server networking adapters and official server extensions. Targets .NET 10 and pins Shared source at `Dependencies/Phinix.Common`. No RimWorld/Unity references are required.

> 当前为本地 F6 迁移候选，正式远端尚未切换。以下构建命令在独立 checkout 中验证通过；构建不等于游戏验收。迁移未改变网络协议、存档格式或物品所有权规则。
> Local F6 migration candidate; remote publication/cutover is pending. Builds were verified in independent checkouts and do not certify in-game behavior. Wire identities, persistence formats and item-ownership rules remain unchanged.

## 获取源码 / Checkout

正式远端就绪后 / Once the reviewed remote commits are published:

```bash
git clone --recurse-submodules https://github.com/HunYuan2333/Phinix-Server.git
cd Phinix-Server
```

已有 checkout 可执行 / For an existing checkout:

```bash
git submodule update --init --recursive
```

正常获取使用固定 gitlink，不使用 `update --remote`。protobuf 为嵌套子模块，需递归初始化。
Acquisition uses pinned gitlinks; do not use `update --remote`. Initialize nested protobuf recursively.

## 编译 / Build

需要 .NET 10 SDK 和项目 NuGet 依赖；不需要客户端目录或游戏引用。
Requires .NET 10 SDK and project NuGet dependencies, with no client checkout or game references.

在仓库根目录运行 / From the repository root:

```bash
dotnet build Server/Server.csproj --configuration Release -p:BuildInParallel=false -m:1
```

## 输出 / Output

```text
Server/bin/Release/net10.0/
Server/bin/Release/net10.0/Extensions/
```

第二个目录包含官方服务端插件。编译不会启动服务器或读取玩家数据；部署时保留独立的数据目录。
The Extensions directory contains official server plugins. Building does not start the server or read player data; preserve the separate deployment data directory.

## Docker

在仓库根目录可构建镜像 / Build an image from the repository root:

```bash
docker build -t phinix-rework:local .
```

此命令仅构建本地镜像，不推送或启动服务。现有 registry、部署名和数据挂载保持；正式发布者切换仍待 F6 交付验收。
This creates a local image without pushing or starting it. Existing registry/deployment identities and data mounts are retained; publisher cutover remains pending F6 acceptance.

## 说明 / Notes

- 普通构建会按 `nuget.config` 还原依赖。离线验证依赖本机已准备的 SDK/框架包与 NuGet 缓存，不保证空机器完全离线。
- NU1900 表示 NuGet 漏洞信息获取失败；不等于编译错误，也不代表已完成漏洞审计。
- 保留原有相关历史，不擅自修改 protobuf 的 SDK 固定文件。根 LICENSE 在原仓缺失，迁移候选未自行添加新的许可证；正式发布说明仍需明确。

Normal builds restore through nuget.config. Offline validation requires preinstalled SDK/framework packs and cached packages. NU1900 reports unavailable vulnerability data, not a completed audit. Relevant history is preserved; do not patch vendored protobuf SDK pins. No new root license was invented during extraction.

## CI 与镜像发布 / CI and image publication

新仓 CI 默认只构建并载入测试镜像，不推送 Docker Hub。准备发布时，在此仓配置 `DOCKER_HUB_USERNAME`、`DOCKER_HUB_TOKEN` 两个 Actions secrets，并将仓库变量 `SERVER_IMAGE_PUBLISH_ENABLED` 设置为字符串 `true`。密钥不会从旧 Rework 仓自动继承。现有 `hunyuan23333/phinix-rework` 镜像名与标签规则保留。

CI builds/loads a validation image by default. To enable publication, configure this repository's DOCKER_HUB_USERNAME and DOCKER_HUB_TOKEN secrets and set SERVER_IMAGE_PUBLISH_ENABLED to the string true. Old repository secrets are not automatically inherited. Existing image/tag identity is preserved.
