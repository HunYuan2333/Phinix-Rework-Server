# 服务端依赖与容器安全升级准备（2026-10-22）

优先级高于 P3 后续功能；本轮仅修改 Phinix-Rework-Server，不修改客户端/共享仓库或固定 protobuf 子模块。未部署、重启生产、推送分支或镜像。base SHA 不是完整构建身份，具体 dirty 源码、固定 gitlink、命令与证据在交付 manifest。

## 结论与适用范围

| 层 | 实际发现 | 本轮处理 |
|---|---|---|
| 开发机 SDK/运行时 | SDK 10.0.113，runtime 10.0.13，属于十月修复版本 | 保留开发机安装，不擅自改系统 |
| Docker 构建 SDK | Dockerfile 原 10.0.401 | 准备 10.0.402-noble；与 10.0.113 为不同 SDK feature band，不是要求本机降/升级 |
| Docker runtime | 原浮动 10.0，当前线上实际版本/镜像 digest 未知 | 准备显式 10.0.13-noble 与独立 runtime-base target；需要重建/重建容器才生效 |
| protobuf 旧目标构建图 | System.Net.Http 4.3.0：CVE-2018-8292，高危 | 定向固定至 4.3.4 |
| protobuf 旧目标构建图 | System.Text.RegularExpressions 4.3.0：CVE-2019-0820，高危 | 定向固定至 4.3.1 |
| net10 发布图 | Autofac 9.3.2、Stateless 5.20.1、Microsoft.Data.Sqlite/Core 10.0.13、SQLitePCLRaw 四包 2.1.13，共 8 个 NuGet 包 | 在线全级别审计未报告已知漏洞；没有无理由升级无关包 |
| SQLite 原生库 | 实际 linux-x64 sqlite3_libversion=3.53.3；SHA256 b0f5c48026fedcf9f05cf31c6fadf9e1e001ca59cd85aa68b8b6814e52c18a07 | 单独登记；NuGet 无告警不等于完整原生/OS CVE 扫描 |
| CA 信任库 | eMudhra 事件对 Linux .NET 使用的系统信任库也相关 | 构建和运行镜像均刷新 CA/OpenSSL 库，并检查 Noble CA 最低修复版本；不是只改 .NET 包 |

两个旧框架包不在当前 net10 发布 deps 中，但实际多目标 restore 会拉入并触发审计。因此修复构建链，不将它们误报成生产使用旧 System.Net.Http，也不忽略告警。新增覆盖只针对 Google.Protobuf 的 netstandard1.1 目标，放在 Server 自有 Directory.Build.targets；没有编辑 vendored 源码、global.json、SDK pin 或 gitlink。嵌套子模块有自己的 targets，普通父项目配置不会自动覆盖；正式审计、质量 runner 和 Docker 入口显式设置 `DirectoryBuildTargetsPath` 指向 Server 文件。直接手工 dotnet 构建也应设置该变量，不能假定默认继承。

当前自有 Server/Extensions 代码搜索未发现 HttpClient/HttpWebRequest/SslStream/X509/QUIC 入口，主通信为 LiteNetLib UDP。微软十月 Critical QUIC CVE-2026-105016 针对 Windows/特定 MsQuic.Schannel 包，当前 Linux net10 发布清单没有该包；不能据此排除全部 .NET/OS 风险。任意第三方插件或将来增加的 HTTPS 都可能扩展范围。

## 官方依据（检索日期 2026-10-22）

- [.NET 十月 10.0.13 发布](https://github.com/dotnet/core/issues/10640)、[10.0.13 组件版本](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.13/10.0.13.md)。实际 MCR tags API 也确认 10.0.402-noble/10.0.13-noble 已发布。
- [System.Net.Http 公告及修复版](https://github.com/advisories/GHSA-7jgj-8wvc-jh57)、[Regex 公告及修复版](https://github.com/advisories/GHSA-cmhx-cq75-c4mj)。
- [Ubuntu eMudhra 修复公告](https://ubuntu.com/blog/emudhra-ca-compromise-fixes-available)：Ubuntu 24.04 的 CA 修复门槛为 `20260816ubuntu0.24.04.1`；[微软 Linux 信任库说明](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography)解释 .NET 对 OpenSSL CA store 的使用。
- [Debian 12 DLA-4845-1](https://lists.debian.org/debian-lts-announce/2026/10/msg00039.html)的 ca-certificates 修复版为 `20250419~deb12u2`；[Debian 13 DSA-6572-1](https://security-tracker.debian.org/tracker/DSA-6572-1)为 `20250419+deb13u1`。不要把 Debian 宿主版本当成容器版本。
- [.NET 10 默认镜像切到 Ubuntu](https://github.com/dotnet/dotnet-docker/discussions/6801)。因此保留 Noble 系列，不编造不存在的 .NET 10 Debian tags，也不无理由迁移到 Alpine。

## 已完成的工程准备

`tools/security/audit_dependencies.py` 单独执行在线安全门禁，使用公共 NuGet package/audit sources、锁定还原、刷新 HTTP 元数据、NuGetAudit=all/low；NU1900～NU1905 全部失败，包括数据源不可用。记录直接/传递包清单、漏洞清单、SDK/源码/锁与索引 hash。原质量 runner 仍可离线复用缓存，其 manifest 明确不代表漏洞审计；CI 新 security job 先通过，再执行 stage 和镜像构建。无需以禁用告警/忽略高危通过检查。

Docker 的 SDK 与 runtime 阶段都执行 `refresh-container-ca.sh`：验证 Ubuntu 24.04、安装官方 CA 与 libssl3t64 更新、检查 CA 最低修复版并刷新信任库。构建基镜像启用 pull；手工安全构建使用 --no-cache，避免复用旧 apt 层。固定 patch tag 并非永久 digest，后续上线仍应保存最终 image digest 和 dpkg 清单。自定义 SSL_CERT_FILE/SSL_CERT_DIR、附加 trust bundle、额外插件及宿主证书需要另外核验。

## 实际执行与结果

- 未刷新/受限网络的第一条 list 命令失败，不算扫描通过。仅运行 --vulnerable 列表也不能覆盖所有多目标构建依赖。
- 强制在线 restore 初始明确失败于两项 NU1903，构成真实前置反例；最初普通父 targets/ProjectReference metadata 未被嵌套 restore 使用，也保持失败。改用入口级 `DirectoryBuildTargetsPath` 后确认实际 netstandard1.1 assets 已解析 4.3.4/4.3.1；无 submodule diff。
- 正式新审计完整通过，官方索引最近更新为 `2026-10-21T11:33:21.1277449Z`；这是已公开数据截至扫描时点的结论，不是零漏洞保证。
- 用不可用的本机代理做受控数据源故障，正式脚本退出 1、manifest passed=false；没有将扫描失败当成无漏洞。
- 最终 `stage --isolated` **33 步全部通过**，含实际组件、固定模型/负控制、SQLite 强杀恢复、架构与 publish 制品检查。没有执行 Docker 构建或云端验收。
- Python 编译与 shell 语法检查、本轮修改文件的 diff whitespace 检查通过。全仓 diff 检查遇到既有 CRLF/空白问题，不清理用户已有业务修改，不称全仓格式通过。
- 本机 Docker socket 权限不足，`sudo -n` 要求密码；未索取密码或尝试绕过，也没有运行镜像。Docker/CA 安装与 SDK 10.0.402 镜像实际执行仍是明确证据缺口；本地业务测试使用已安装的安全 SDK 10.0.113/runtime 10.0.13。

主要命令（NuGet 缓存为 `/home/hunyuan2333/.nuget/packages`，CLI HOME 为 `/tmp/phinix-dotnet-113`）：

```sh
python3 tools/security/audit_dependencies.py --output /tmp/phinix-server-security-20261022/final-audit
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-server-security-20261022/quality-stage
sh -n tools/security/refresh-container-ca.sh
python3 -m py_compile tools/security/audit_dependencies.py tools/quality/check.py
```

隔离公开元数据、初始失败、最终审计与 33 步 manifest 留存在交付目录 `Phinix-Rework-Server-Security-2026-10-22`。没有打包玩家数据、凭据、游戏 DLL 或整份用户环境。

## 云端只读核验（尚未执行）

你此前使用 `mcr.microsoft.com/dotnet/runtime:10.0` 加挂载应用。宿主升级不会自动更新现有镜像或运行中的容器。先在 ECS 执行：

```sh
sudo docker exec phinix-server sh -c 'cat /etc/os-release; dotnet --list-runtimes; dpkg-query -W ca-certificates libssl3t64'
sudo docker inspect --format '{{.Image}} {{.Config.Image}}' phinix-server
sudo docker inspect --format '{{json .Mounts}}' phinix-server
```

不要输出完整 inspect/环境变量/认证配置。若不是 Noble/没有 dpkg，按实际 OS 官方公告核验，不能套用本脚本。Debian 宿主的 ca-certificates 也应按上方对应版本更新，但这属于宿主管理，未由本轮自动执行。

## 可审查升级路径（待实际镜像验证与安排维护窗口）

只准备基础运行环境，暂不改变现有挂载应用或交易 DB：

```sh
sudo docker build --pull --no-cache --target runtime-base -t phinix-runtime:10.0.13-ca20261022 .
sudo docker run --rm --network none phinix-runtime:10.0.13-ca20261022 sh -c 'dotnet --list-runtimes; dpkg-query -W ca-certificates libssl3t64'
```

这是未在本机执行的命令，不是镜像验收结论。之后核对实际容器参数和持久卷、安排停止/备份/重建窗口，使用新 runtime 镜像和原挂载/命令重建容器；单纯 docker restart 不会换镜像。保留原应用、身份与交易数据，不能删除数据卷；应用数据库 schema 本轮不变。旧镜像保留供排障，但安全回退不能把旧 CA 库视作已修复。完整应用镜像路径用 `docker build --pull --no-cache -t phinix-rework:security-20261022 .`，同样须先核验，不发布远程。

## protobuf 能否升级

可以。实际固定源码版本为 C# 3.34.1，官方 NuGet API 已确认稳定版候选 **3.36.2**（另有较小跨度 3.34.2）。[官方交叉版本保证](https://protobuf.dev/support/cross-version-runtime-guarantee/)允许同一 major 中较旧生成代码使用更新 runtime，但新生成代码不能配更旧 runtime；安全修复也可能要求同步更新。不能把 C++/Java/PHP 公告直接套到 C#。

本轮只修复已确认的构建依赖，没有替换 protobuf DLL 或改其 gitlink。当前并未确认 C# 3.34.1 存在本轮这两项以外的直接运行时公告。下一独立升级切片应在隔离副本比较 3.36.2 的 C# API/程序集、重新编译全部 Server/插件/生成契约，用既有独立 golden/profile 验证认证/聊天/交易与旧端字节兼容、错误输入和持久记录读写，再决定共享层版本提交范围。服务器/客户端是独立进程，C# runtime 更新不当然要求客户端同时升级，但必须用实际编码兼容证据确认，不能仅凭服务器编译成功宣布兼容。

## 云端执行结果补记（用户回传，2026-10-22）

以下为用户在 ECS 实际执行并回传的证据，不是本机 Docker 测试。前文“尚未执行/待验证”描述准备时状态，以本节的限定结果为准。

- 对 `phinix-runtime:10.0.13-ca20261022` 执行隔离容器检查：Microsoft.NETCore.App **10.0.13**，ca-certificates **20260816ubuntu0.24.04.1**，libssl3t64 **3.0.13-0ubuntu3.16**；CA 达到本次官方修复门槛。
- 重建前 inspect 确认 `/opt/phinix/app:/app:ro`、`/opt/phinix/data:/data:rw`，启动命令 `dotnet /app/PhinixServer.dll`，重启策略 `unless-stopped`。
- 重建后 `docker ps --filter name=phinix-server`：容器短 ID `b98a62b36344` 使用上述新镜像，状态 Up，发布 UDP 16200（IPv4/IPv6）。短 ID 不是镜像 digest，也不是应用构建 SHA。
- 用户回传启动摘要显示：加载既有凭据、用户、聊天及 framework trade state，并监听 `0.0.0.0:16200`，未见该段启动错误。未保存玩家记录、原始生产日志或凭据。

据此，本次运行环境替换与启动检查通过；这不证明客户端登录、聊天、交易、游戏物化或双边存读档通过，也不证明账本全部记录已独立核对。应用目录仍使用原挂载，本轮修改的构建依赖/CI 不因 runtime 替换自动部署。完整应用镜像 SDK 10.0.402 构建、镜像 digest、宿主 CA 状态及重启后的业务冒烟仍待各自验证。启动日志自带时间为 2026-10-21 16:23:55Z，与本记录日期不同，不据此推断云主机时钟已校验。

后续由用户验证一次客户端重连、历史数据展示及聊天；交易和存读档按原联合验收范围单列。保留停止状态的旧容器用于排障，不同时运行两个写入同一数据目录的服务端。
