# Phinix-Rework-Server

**Phinix Rework 服务端**：独立的专用服务器宿主、网络适配器以及官方服务端插件。

**Phinix Rework Dedicated Server**: Standalone server host, networking adapters, and official server extensions.

> [!NOTE]
> 本项目统一称为 **Phinix Rework**，与原 Phinix 项目区分。服务端基于 .NET 10 开发，不需要客户端工程或 RimWorld/Unity 引用。
> This project is named **Phinix Rework**, distinct from the original Phinix. The dedicated server targets .NET 10 and requires no client checkout or RimWorld/Unity references.

---

## 仓库职责与关联 / Responsibilities & Relations

| 仓库 / Repository | 角色 / Role | 职责 / Responsibility |
| :--- | :--- | :--- |
| [Phinix-Rework-Server](https://github.com/HunYuan2333/Phinix-Rework-Server/tree/dev) | 服务端（本仓库） | 独立的专用服务器程序、服务端插件及 Docker 容器镜像 |
| [Phinix-Rework-Common](https://github.com/HunYuan2333/Phinix-Rework-Common/tree/dev) | 共享层 | 游戏无关的底层核心逻辑、网络传输、加密认证与协议契约 |
| [Phinix-Rework](https://github.com/HunYuan2333/Phinix-Rework/tree/dev) | 客户端 | RimWorld 1.6 模组客户端宿主、游戏内 UI 及客户端插件 |

服务端将共享层作为 Git 子模块固定在 `Dependencies/Phinix.Common`，通过项目引用直接参与编译。为保持生态与连接兼容性，仓库更名不改动现有程序集名称（如 `PhinixServer.dll`）、命名空间、协议标识或 `Dependencies/Phinix.Common` 子模块目录名。

The server repository pins the shared layer as a Git submodule at `Dependencies/Phinix.Common` and references its projects directly during compilation. Repository names do not alter existing assembly names (such as `PhinixServer.dll`), namespaces, wire protocol identifiers, or the `Dependencies/Phinix.Common` submodule directory name.

---

## 部署运行（面向部署者） / Deployment (For Deployers)

### 方式 A：Docker 快速部署（推荐） / Option A: Docker (Recommended)

使用官方已验证发布的 Docker Hub 镜像启动服务：

```bash
docker pull hunyuan2333/phinix-rework:dev

docker run -d \
  --name phinix-server \
  --restart unless-stopped \
  -p 16200:16200/udp \
  -v ./server_data:/data \
  hunyuan2333/phinix-rework:dev

# 查看运行日志
docker logs -f phinix-server
```

### 方式 B：Docker Compose 部署 / Option B: Docker Compose

使用仓库根目录的 `docker-compose.yml` 启动：

```bash
docker compose up -d
```

默认配置将 UDP 端口 `16200` 映射到宿主机，数据持久化卷挂载到 `/data`，自定义外部插件挂载至 `/app/UserExtensions`。

### 服务端配置与运行机制 / Server Runtime & Configuration

- **端口**：默认监听 UDP 端口 `16200`，请确保宿主机防火墙和云服务器安全组已放行。
- **数据持久化**：服务端以当前运行工作目录（容器内为 `/data`）为存储路径，在首次启动时自动生成 `server.conf`（默认使用 `ClientKey` 认证方式）、数据库和日志。部署更新时请显式挂载并保留独立的数据目录。
- **控制台交互**：前台运行时支持 `help`、`version`、`exit` 等命令。

---

## 源码获取与编译（面向开发者） / Developer Build

### 获取源码与子模块 / Source Acquisition & Submodules

克隆本仓库及递归子模块 / Clone the repository with recursive submodules:

```bash
git clone --branch dev --recurse-submodules https://github.com/HunYuan2333/Phinix-Rework-Server.git
cd Phinix-Rework-Server
```

已有检出递归更新固定子模块 / For an existing checkout:

```bash
git submodule update --init --recursive
```

> [!IMPORTANT]
> 获取与构建使用固定提交的 gitlink，**切勿使用** `git submodule update --remote`。共享层中嵌套有 protobuf 子模块，必须使用 `--recursive` 初始化。
> Acquisition uses pinned gitlinks; **do not use** `git submodule update --remote`. protobuf is a nested submodule inside the shared layer and requires `--recursive`.

### 编译与测试 / Build & Test

环境要求：.NET 10 SDK 与可还原的 NuGet 依赖。无需客户端工程或游戏 DLL。

Requires .NET 10 SDK and restored NuGet dependencies, with no client checkout or game references.

在仓库根目录执行编译 / Build from repository root:

```bash
dotnet build Server/Server.csproj --configuration Release -p:BuildInParallel=false -m:1
```

运行自动化运行时回归测试（注意：自动化测试通过仅用于验证服务端逻辑，不代表游戏内运行验证）：
Run automated runtime regression tests (note: automated tests verify server logic and do not certify in-game behavior):

```bash
dotnet run --project Tests/Phase35ServerRuntimeTests/Phase35ServerRuntimeTests.csproj --configuration Release
```

本地启动服务 / Run locally:

```bash
dotnet Server/bin/Release/net10.0/PhinixServer.dll
```

### 输出位置与结构 / Output Location & Structure

```text
Server/bin/Release/net10.0/
├── PhinixServer.dll                  # 服务端主程序
├── PhinixServer.runtimeconfig.json   # 运行时配置
├── LiteNetLib.dll                    # 网络库
├── ...                               # 共享层依赖库
└── Extensions/                       # 官方服务端插件目录
    ├── ChatExtension.Server.dll
    ├── ChatExtension.dll
    ├── TradeExtension.Server.dll
    └── TradeExtension.dll
```

构建过程仅生成二进制文件，不启动服务或读取玩家数据。部署时请保留独立的数据存储目录。

Building produces binaries only; it does not start the service or touch player data. Maintain a separate persistent data directory during deployment.

---

## 本地镜像与 CI 发布 / Docker Image & CI Publication

### 本地构建 Docker 镜像 / Local Docker Build

在仓库根目录执行构建 / Build an image from the repository root:

```bash
docker build -t phinix-rework:local .
```

该命令仅在本地构建验证镜像，不推送镜像或启动容器。

### CI 与镜像发布状态 / CI & Publication Status

服务端镜像构建与发布由 `.github/workflows/docker.yml` 工作流驱动。镜像发布目的地统一为 Docker Hub 的 `hunyuan2333/phinix-rework`（注意用户名为 `hunyuan2333`；旧地址 `hunyuan23333/phinix-rework` 已停止自动更新）。

- **已验证的发布路径**：
  - `dev` 分支的镜像发布已验证生效：生成 `dev` 标签和完整的 commit SHA 标签（如 `sha-42516ec...`），可直接通过 `docker pull hunyuan2333/phinix-rework:dev` 拉取。
  - 本地 Dockerfile 镜像构建与 CI 自动化装载测试（`load: true`）已验证通过。
- **尚未完成 / 需配置事项**：
  - `latest` 标签尚未发布：工作流策略规定仅当合并推送到 `main` 分支且发布开关开启时，才会打出 `latest` 标签。
  - 自动化推送凭据：向 Docker Hub 推送依赖本仓库配置的 `DOCKER_HUB_USERNAME`、`DOCKER_HUB_TOKEN` 两个 Actions secrets，以及仓库变量 `SERVER_IMAGE_PUBLISH_ENABLED=true`（密钥不会从旧仓自动继承）。未开启该变量时 CI 仅做镜像构建与装载验证，不推送到 Docker Hub。
  - 正式版本 Tag（`v*`）：推送版本 tag 时会按 semver 规则发布对应的版本镜像标签，目前尚未打出正式版本 release tag。

---

## 维护说明 / Notes

- 普通构建会按 `nuget.config` 还原依赖。离线构建依赖本地已准备的 SDK 与 NuGet 缓存。
- NU1900 警告表示 NuGet 漏洞数据库连接失败，属于网络建议提示，不等于编译错误，也不代表已完成安全审计。
- protobuf 为外部固定依赖，请勿擅自修改 vendored protobuf 的 SDK 锁定文件与源码。
- Builds restore dependencies through `nuget.config`. Offline builds require preinstalled SDKs and cached packages.
- NU1900 reports unavailable vulnerability data; it is an advisory notice, not a compile failure or a completed security audit.
- Do not modify vendored protobuf SDK pins or submodule configuration.
