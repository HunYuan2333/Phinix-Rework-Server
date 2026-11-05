# P6 联合候选与验收矩阵

SV-60 先固定已交付的服务端和客户端测试制品，供 SV-61 按同一组合联测。两个 Rework SHA 是构建基线，构建均包含未提交改动，以实际制品哈希区分版本。

| 组件 | 构建基线及制品 |
| --- | --- |
| Server | `5a0508acb703319e60dd5600e3e0facd0397b294`，Common `67f243d9edced77dc3fe669f8a435b1f14b59199`。交付目录 `Phinix-Rework-Silver-Names-Hotfix-2026-11-04` 的 manifest 固定 50 个文件；主 DLL SHA-256 `635f347f845476dcd84938bafa896dbd9d1523af8fba8f013c89570c8b6c8986`。 |
| Rework 客户端 | 业务分支 `53c92ce8eb314dcb8ca422ae4b6891a007931128`，Common `c165f61d749189f700ca896d63d9366d5c71dbf7`，Host API 3.0.0，1.6/1.7 诊断测试包 SHA-256 `afb4b28a9110d374bb44f6563ef963d8fb0153bfa4dd1d659f36cf452bc8d5df`。 |
| 原版 Phinix | 上游 Release 0.9.7，源码 `ce82b8f2cd57718f05de86458eebd3320f35c4d8`，原版 UDP profile。 |
| 插件 | 内置 Chat、Trade、Inventory 随候选制品。独立红包、人才交易及其他受影响插件的联合版本与运行证据仍需补齐。 |

本轮只读校验使用 Python `hashlib.sha256` 逐项比对 Server manifest（50 项通过），使用 `zipfile.testzip()` 验证客户端压缩包（95 项），两版 Inventory GameAdapter DLL 均包含 P5 空投诊断标记。通过 `git rev-parse HEAD` 和 `git ls-tree HEAD Dependencies/Phinix.Common` 核对基线及 gitlink。没有新增网络或实机测试结果。

| 联测组合 | 已有证据 | 待验 |
| --- | --- | --- |
| 原版 ↔ Rework，升级 Server | P5 真实 UDP 混合交易与聊天回归；玩家反馈一笔 30 医药成功入库 | Windows/Waylith 直投与库存提取后地图物品数量，保存并重启后仍可核对；测试双方取消、离线与重连 |
| Rework ↔ Rework，升级 Server | P3 独立进程 UDP/SQLite 恢复，现代报价保留完整载荷的回归 | 用固定候选核对双方游戏资产，覆盖 1.6/1.7 跨版 |
| 历史 Rework → 升级 Server | 固定历史模块登录、公开同步与重启 UUID 回归 | 历史客户端实际 Mod 的支持与拒绝矩阵 |
| 候选 Rework → 未升级 Server | 本轮无联合证据 | UDP 登录、聊天、交易的受支持行为及明确拒绝 |

用户报告本机空投正常，未绑定具体包哈希、地图或存档。此前 Windows/Waylith 提取异常继续作为个案记录，保留开发者模式探针用于后续排查。SV-60 已有核心制品基线。按本轮范围，SV-61 新增实机测试只安排 Rework 双端；其他组合保留既有证据及未验证状态，两项尚未退出。

## TODO：插件加载策略与兼容回归

候选客户端对插件声明的 Phinix 兼容版本为 `0.10.5`，客户端契约为 `3.0.0`。发布版本号变化不要求第三方插件跟着改包版本或重发。仍在兼容范围内的旧插件包应原样进入正常预检，再分别尝试加载。只有明确的不兼容或单个插件加载失败才停止该插件及其必需依赖链上的下游插件，其他插件继续。安装包完整性、游戏运行时、依赖、程序集引用与作者声明的兼容范围仍是执行前的边界。

后续单独验证（不作为本轮 P6 退出条件）：在候选 Host 上使用已发布、未改版本号的插件原包，验证正常加载；再用明确不兼容和激活失败的包核对隔离行为。若当前版本号变化导致仍兼容的包被拦截，先查产品发布版本、兼容版本和契约版本是否混用，再修正对应判断。当前只核对了预检代码与版本声明。加载器改动及既有发布包和失败隔离验证均列为后续 TODO。

## SV-62 服务端容器候选

Docker 工作流在 PR 和原有触发条件下复用 Server quality 检查，随后构建并加载 linux/amd64 镜像。推送步骤使用已经通过容器测试的本地标签，仅在非 PR 且 `SERVER_IMAGE_PUBLISH_ENABLED=true` 时执行。Dockerfile 增加对 `LegacyAdapter.Server.dll` 的打包检查。

本机从当前服务端工作树执行以下命令通过，镜像 ID 前缀 `a5084de295fd`。测试使用空的临时数据卷、无 stdin、无外部网络。两次启动均到达监听状态，两次 SIGTERM 停止均退出 0。第一次停止后只读检查了配置、用户及凭据文件、聊天历史和交易 SQLite 文件，并用同一卷启动第二次。

```sh
docker build --pull --platform linux/amd64 -t phinix-server:p6-ci-smoke .
bash tools/quality/docker_smoke.sh phinix-server:p6-ci-smoke
```

两份 workflow 的 YAML 解析及脚本语法检查通过。GitHub 托管 runner 上的执行和镜像推送仍待验证。此容器测试不包含有交易数据的迁移或 Rework 双端实际到账验收。

## SV-63 工坊存档升级核对

实际安装工坊版 `3735269431` 和候选包的 Mod packageId 都是 `hunyuan2333.phinixrework`。工坊 1.6/1.7 库存 DLL 均使用原有 `phinixInventorySaveId` / `phinixInventorySnapshot` 字段。候选在原字段基础上增加可缺省的 `phinixInventoryJournalIdentity`，本地日志仍按存档 ID 与绝对路径查找。

使用实际工坊 1.7 DLL（SHA-256 `2f10f60ea479c4bf58690b200f73d961ba71c9e265ed9991a1cf3c9eb3b64231`）在隔离目录写出合成存档快照及日志，再由固定候选包中的 1.7 DLL 读取。通过检查存档数量与载荷、较新日志明确要求批准、批准后保留较新库存、新增库存后带 authority 标记重开。另运行 `tools/quality/run-client-component-probes.py --suite inventory-checkpoint`，候选包内程序集通过 33 项合成存档与保存回调检查。未修改玩家数据。

该证据覆盖原机器原路径的向前升级，完整游戏存读档和 1.6 实机未执行。切换 Mod 前应备份旧存档、游戏配置及 `framework-extensions/client/builtin.inventory`。升级保存后搬迁应同时保留匹配的 journal 和 authority 文件，并考虑绝对路径变化；仅复制新 .rws 或直接降级不作为已验证路径。

## ECS 部署与首次 main 发布准备

ECS 开发容器于 2026-11-05 11:31:28 +08:00 启动，现有镜像、UDP 16200 与数据目录继续使用。发布归档 SHA-256 为 `8d54830eb5a94563ca580bee281a45acb734c264d8c5b2720c1bade1f14fe241`。容器内 62 个程序文件与候选逐项校验通过，容器 running、restartCount=0。原程序和停机时的数据备份位于 `/opt/phinix/backups/p6-udp-20261105.vK0nHy`。另从同一工作树本地构建 linux/amd64 Docker 镜像 `3c5cd166136f`，通过两次无 stdin 启动、SIGTERM 正常停止及同卷重启检查。云端使用发布目录替换，和本地 Docker 构建是两项独立证据。

拆分前 Docker 工作流可从客户端仓库 `798821f:.github/workflows/docker.yml` 或归档 `codex/pre-split-20261008` 找到。客户端拆分提交 `cf7360a` 删除 Dockerfile 和发布工作流，独立 Server 保留流程并在 `42516ec` 修正 Docker Hub 目标为 `hunyuan2333/phinix-rework`。当前候选在此基础上加质量检查、镜像启停测试与显式发布开关。

远端只读核对时，默认分支是 `dev`（`42516ec02fa77a072faf1839fbc0ff2d00237734`），还没有 `main`。首次发布顺序：

1. 将当前服务端工作树的候选整理成可审查提交，保留固定子模块，排除玩家数据、密钥及构建制品。
2. 从已发布的服务端远端 `dev` 基线建立 `main`，将候选提交通过 PR 合入。先关发布开关验证 GitHub runner 的质量和 Docker 检查。
3. 确认独立服务端仓库的 Docker Hub secrets 配置后，授权开启发布开关，从 `main` 运行已验证工作流，生成 `latest` 和 SHA 标签。需要正式版本标签时，再从已验证提交创建版本 tag。

未创建远端分支、合并或推送镜像。

发布准备复核：独立服务端仓库已登记 `DOCKER_HUB_USERNAME` 与 `DOCKER_HUB_TOKEN`，未读取其值。`SERVER_IMAGE_PUBLISH_ENABLED` 当前是 `true`，所以创建或推送 `main` 前须先关闭它，避免引发尚未验证的 `latest` 推送。
