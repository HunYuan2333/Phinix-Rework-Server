# P4 无密码身份与安全传输收口（2026-11-01）

> **当前决策（2026-11-01）：TCP＋TLS 已实现，但属于实验性功能，不建议使用。** 默认交付继续采用 UDP / ClientKey。取消本轮现代身份切换及旧登录退役，后续部署、客户端实机验收、证书生命周期和迁移切换工程列为 TODO。已有实验代码和自动化证据保留，不代表正式支持；本文后续启用/迁移步骤仅作实验与历史设计参考，不是部署要求。不得自动启用或在 TLS 认证失败后降级。详见服务端 `docs/Server-Modern-Identity-TODO.md`。


## 当前交付策略（2026-11-01，用户确认）

- 默认部署及当前验收主线为既有 UDP / ClientKey；用户反馈当前 UDP 可用，仅记录为当前人工观察，不扩展为完整交易或全平台验收通过。
- **TCP＋TLS：已实现，实验性；未完成部署者/真实客户端实机验收。** 配套现代无密码身份同属显式启用的实验范围，非默认部署前置。已有合成身份、真实网络与独立进程自动化检查保留其原始证据，不能写成完全未测试，也不能替代实机验收。
- 当前 ECS 保持 UDP，不配置证书、不开放或切换 TCP/TLS、不迁移身份；普通部署者无需为当前 UDP 服务配置证书。显式 TLS 信任失败仍明确拒绝，不能自动降级。
- 证书热加载、自动续签部署及信任换钥流程暂缓，未来重新启用 TLS 实机工作时单独处理；不阻塞当前 UDP 交付或后续必要 P5 工作。


状态：**P4 加速版实现与自动化退出门槛已满足，候选收口。**采用 RoadMap §0/§14 的加速最小门槛，不把未运行的 Windows / RimWorld / Waylith 或系统断电检查计为通过。

## 范围和版本组合

- Server `dev`，HEAD `5a0508acb703319e60dd5600e3e0facd0397b294` 加既有未提交修改。
- Client `codex/p3-trade-business`，HEAD `53c92ce8eb314dcb8ca422ae4b6891a007931128` 加既有未提交修改。工坊/UI 线及交易取消 hotfix 不合并。
- 固定 Common / protobuf gitlink 不变，没有新增 NuGet 依赖或修改 vendored 源码、SDK pin。构建使用已安装 SDK 10.0.114 / runtime 10.0.14，通过进程环境选择 SDK。
- 依赖方向保持 Client → 中立 `ServerTransport` 源码项目，Server authentication → 同一中立项目。新增 `IdentityWire.cs` 只定义有界的身份 bootstrap DTO/编码/验证，不引用 Server 业务程序集；客户端 1.6 不带这段现代实现。

最终证据、实际 DLL 指纹与配套候选在工作区 `Deliverables/Phinix-Rework-P4-Closeout-2026-11-01/`。HEAD 不是干净构建版本，必须结合 sourceInputs 与实际制品 SHA256 识别组合。

## SV-40～45 实现

| 条目 | 本轮链路与不变量 |
| --- | --- |
| SV-40 | 复用既有 TLS stream、ALPN、链/名称/时效/SPKI 校验和中立模块帧；认证及业务均经同一 TLS。1.6 对显式 TLS 选择拒绝，不退回 UDP。无效信任不会创建新现代凭据或注册玩家。 |
| SV-41 | 稳定 ServerId / principal 与随机 credential 分离；32-byte 随机秘密只按服务器保存在私有用户目录；服务端保存域分隔 verifier，固定时间比较。新 User 记录不承载秘密，迁移不替换原 UUID / 交易关联。 |
| SV-42 | 新 TLS-only `Identity.v1` 入口支持首次注册、认证、短时会话、续期、本人轮换/撤销。连接不可切换 principal；注册及消息预算有界。现代模式不发送旧 Hello，也不接受旧 Authentication 模块；协商超时不得转 Legacy。capability 不能授予管理权限。 |
| SV-43 | 两端明确命名的 Legacy identity adapter 独占旧 ClientKey/安装级 fallback 规则；服务端只接受实际存在且一致的旧凭据与玩家映射。先持久化新候选及预期原 UUID，再事务绑定；不按显示名认领、不自动补全不一致旧源。旧秘密已泄露时，首次持有证明者的争议不能靠迁移自动“安全找回”；已有绑定不允许旧证明再绑定另一个新凭据。 |
| SV-44 | operationId、请求指纹、原始结果、身份状态和失效 outbox 同库同事务。注册/绑定丢响应后重放同一操作；轮换先保存候选，重启先验证候选以核对是否已提交。撤销结果可凭原凭据证明只读查询，不能因此重新获得会话权限。冲突明确拒绝。 |
| SV-45 | 私有目录、权限/ACL、文件及父路径链接检查、跨进程独占、同目录临时文件/Flush(true)/rename、取消/失败保留旧文件。现代凭据缺失但有 expectation 时拒绝重新注册；同一可信 name/pin 不可悄悄换 ServerId。已确认现代身份不再依赖旧凭据文件解析。 |

权限只允许管理自己的活跃凭据。绑定其他主体必须由 Legacy mapping adapter 给出已有凭据的真实映射；现代存储不接收旧字段、协议开关或显示名。服务器错误不变成资产 ACK、退款或换协议重发。Chat/Trade 仍经通用宿主和相同授权/插件管线。

## 会话、持久化与恢复

`PasswordlessIdentityEndpoint` 由 Autofac application scope 拥有，且拥有 session authority；停止时解绑 transport、清理会话、停止计时器。重复模块注册失败不夺走原 handler。客户端认证、续期及 Framework 的 admission state 同步替换会话；服务端 registry 使用新 Entry，避免旧 reader 将新 Ready 标为失效。更新不改变已协商能力或授予新权限。

默认会话 5 分钟、原凭据认证最大年龄 30 分钟，判定使用单调时间。客户端超时也按单调 deadline 判定，旧 Timer 回调不能提前终止新请求；已有候选的负对照会在这条回归失败。客户端最多间隔 2 分钟用持久凭据重新验证；服务端每次授权检查 epoch，轮换/撤销后旧证明不能执行业务。轮换/撤销提交后的短暂回执窗口只抑制该连接的主动关闭，不恢复任何已失效的授权。

服务端首次建库须显式 `InitializeNew=true`；正常打开不重建。保留每库 `.identity-initialized`、原凭据源 `.modern-identity-required` 和数据目录 `identity-authority.required`：缺 DB、缺已使用的玩家/迁移快照、缺配置或移除现代模式，都拒绝启动，不能生成空账本或恢复旧入口。配置改名/路径改变也不能绕过数据目录的 authority pin。标记约束新版本启动，不能使旧程序安全理解新状态；回退必须恢复一致数据点，不能只换回旧二进制。

客户端写入格式升级为 version 2，继续读取原 version 1；写入 v2 包含完整轮换/撤销状态，缺 `Revoked` 等必要证据拒绝读取。`.expected` 和 `trust-*.authority` 为同目录的非秘密恢复证据；备份须保留整个目录。没有自动云备份、显示名找回或无限重试。整个本地身份资料与备份全部丢失时，不能凭公开信息恢复原秘密。

本轮只承诺已经验证的进程级故障窗口；Flush/rename 不等于所有文件系统上的断电保证。检查与使用之间的同权限恶意进程替换、原生系统故障全集、跨机凭据迁移及信任换钥的自动化策略不在此处宣称完成。

## 配置与原有服务

默认未配置 ModernIdentity 时保留既有配置与 UDP/过渡 TLS profile。新的模式须同时配置 TLS、私有用户数据目录和预先可信的服务器元数据；开启后不能靠删除节或改旧凭据路径降回旧身份。现代模式的目标客户端为 1.7/.NET10，1.6 继续使用既有 profile；混合接入扩展属于 P5。

在现有 `server.conf` 的命名空间和成员顺序内，加入 `Tls` 后再加入：

```xml
<ModernIdentity>
  <DatabaseFile>/data/identities.sqlite3</DatabaseFile>
  <InitializeNew>true</InitializeNew>
  <RegistrationPerMinute>10</RegistrationPerMinute>
</ModernIdentity>
```

`InitializeNew` 仅授权首建；即使保留 true，初始化标记存在后也不会重建丢失的 DB。可将后续配置改为 false。注册/轮换/撤销尝试的默认全局预算为每分钟 10，范围 1～1000；每连接总消息预算每分钟 60，session authority 另限制会话签发尝试。预算不足明确返回 RateLimited/Capacity/Busy，不改原结果。

客户端沿既有扩展设置读取：

| 键 | 现代模式值 |
| --- | --- |
| `identity.mode` | `passwordless`（默认 `legacy`） |
| `transport.mode` | `tls` |
| `transport.tls.serverName` | 证书验证名称 |
| `transport.tls.spkiPin` | 已可信的 SHA256 SPKI Base64 |
| `transport.tls.rootCertificateFile` | 可选公开可信 CA 文件；空值使用系统根 |

用户秘密保存在 LocalApplicationData/Phinix-Rework/identities-v1，不能落到当前游戏或工坊目录；缺少可用绝对用户路径明确失败。TLS 证书/公钥 pin 是部署信任元数据，不要求玩家输入密码、验证码或操作私钥。测试中的 synthetic CA 使用 NoCheck 仅为隔离 fixture；正式工厂仍使用 Online 撤销策略，不提供静默关闭校验的设置。

当前开发云容器未切换到这套模式。其原有 UDP 映射不能当作 TLS 已部署；实际切换需单独准备证书、TCP 映射及一致数据备份，不在本轮自动操作。

## 验证清单

1. L1/L2：真实 SQLite 身份、epoch、轮换/撤销、只读原结果、冲突/跨 owner/跨 server；私有文件 version1/2、损坏/取消/失踪、路径链接、真实跨进程锁。
2. L2/真实网络：Autofac 生产装配、真实 TLS、无效 pin/旧入口/跨连接拒绝、注册预算、首次注册/原 UUID 迁移、Framework Ready 刷新、脱离损坏旧客户端凭据的重连。
3. L3/L4：四个独立服务端强杀 profile——首次注册、已有 UUID 绑定、轮换、撤销，均停在 COMMIT 后/响应前；重启后客户端核对原操作，独立 SQLite 检查无重复主体/操作。另有四个客户端文件提交前/后强杀点。
4. 最终 published `PhinixServer.dll` Main：两名真实网络客户端、原 UUID 登录、真实 Chat 回复/提及、真实 Trade 创建空报价及对端快照、正常停止；缺配置/移除现代模式拒绝启动且不重建用户。空报价不是资产物化或守恒证明。
5. 双版客户端图、三个维护中插件消费者、包内 DLL 与被测 DLL 指纹、发布包审计、P3 既有阶段/组件回归。

上述检查已执行，最终结果见下节和实际 manifest。Windows ACL/Junction、Windows/Waylith/真实游戏保存和真实资产交付未运行，不把 Linux 结果推广过去。

## 仍需人工/专用环境的范围

P6 时先用备份和测试存档，在 Windows 1.7/Waylith 配置可信现代入口：旧客户端资料升级后 UUID/昵称/交易关联保留，退出重启再登录；故意配置错 pin 应明确失败，不能切旧协议；检查私有目录及权限，然后两人验证聊天/回复/艾特、库存保存/读档、交易完成/取消/断线/重启。真实资产异常立即保留日志和操作时间，不能把重试/退款当作恢复成功。

Windows 原生 ACL/Junction 可用 `Tests/ClientIdentityCredentialTests` 控制台入口对其自建合成目录执行；没有链接创建权限时记录未验证，不能跳过后宣称覆盖。测试不得指向玩家或现有凭据目录。

P5 负责既有 profile/Legacy adapter 的进一步收束和混合客户端声明；P6 负责真机与扩大证据；P7 的部署/切换/退役仍需对应授权。本轮不跨分支合并、不发布工坊、不部署云端。

## 最终执行结果与证据

按 §14 的 P4 最小门槛关闭：自动注册/认证、原 UUID 迁移、重启回读、无效信任/凭据明确失败、秘密与公开包隔离均有实际证据。SV-40～45 的上述产品范围完成；全平台/全故障排列、P5 兼容扩展和 P6 实机不算已通过。

| 检查 | 实际结果 |
| --- | --- |
| 隔离 Server stage | 43 个步骤全部通过；61 个发布文件审计通过，含 P3 独立进程恢复回归 |
| 身份 SQLite / TLS 宿主 | 74 / 60 项通过；后者含 23 项会话断言、生产装配和 Ready reader 竞态 |
| 最终双方 SDK 与真正发布版 Main 的联合 | 99 项通过；独立 Server Main 使用自己的 SDK，客户端使用自己包内的 SDK/序列化器，避免同进程引用统一掩盖漂移 |
| 进程故障 | 服务端四种 COMMIT 后/响应前强杀恢复通过；客户端文件四个提交前/后强杀点通过 |
| 客户端实际包内凭据组件 | 67 项通过，Linux 权限/链接、旧 v1 读取和 v2 写入/损坏、轮换/撤销/marker、跨进程锁 |
| 双版交易组合 | 1.6 / 1.7 各 320 项通过，未以此证明游戏真实到账 |
| 原有 TLS/认证兼容及宿主 | 11 项（包含 32 秒真实空闲）/ 10 项通过 |
| 构建及客户端包 | 1.6/net472 与 1.7/net10 完整图、三个维护插件消费者、95 文件包审计通过 |

最终 machine-readable 记录位于 Deliverables 目录。每个命令、返回值、输入 DLL/source SHA256、协议/profile 与失败日志分别保留；无 coverage 百分比或系统断电保证。SDK 10.0.114 / runtime 10.0.14；既有 generated/obsolete 构建警告未通过修改 vendored 文件消除。

命令入口（具体绝对输入路径在各 manifest）：

```sh
python3 tools/quality/check.py --suite stage --isolated --output <evidence>/server-stage-final --timeout 120
python3 tools/compatibility/build-dual-host.py --game16 <refs16> --game17 <refs17> --harmony17 <Harmony17> --output <isolated-build>
python3 tools/quality/run-client-tls-probe.py --profile identity --client <exact-package-1.7-dlls> --server <published-server> --output <evidence>
python3 tools/quality/run-client-identity-tests.py --authentication-assembly <package-Authentication.Client.dll> --output <evidence>
python3 tools/quality/run-client-component-probes.py --suite trade-composition --assemblies <exact-package-dlls> --game <refs> --harmony <Harmony> --output <evidence>
```

最终联合明确包括：迁移原 UUID、消息/回复/艾特、空报价创建与对端快照、正常停止，缺配置及改旧源路径不能降级；它不是 RimWorld 库存/物化/游戏保存证明。Root/profile 全部由测试自动建立和删除，未读取或迁移玩家数据。

开发期间的失败另存而不算通过：测试宿主漏复制 SQLite native asset；冲突错误一度误分类为 AuthorityUnavailable（产品已明确返回 Conflict）；新增 Main 验收脚本漏填 Request 命令类型（已按既有协议修正）；旧 Timer 回调的上一候选负对照确实失败，修复候选同一回归通过。没有重试掩盖关键 flaky，也没有放宽服务端命令验证去适配错误测试。

后续顺序：P4 候选冻结 → P5 必要 adapter/混合 profile → P6 Windows/Waylith/真实资产与发布前扩大证据。P3 历史 Unbound 核对/长期冷归档等既有延期未被此次身份工程伪装为完成。工坊修复仍独立，源码未提交/推送、云端未部署。


### 2026-11-01 P4 开发 ECS 部署

按用户授权，已部署冻结的 P4 服务端包并重启阿里云 `47.93.172.62` 的原 `phinix-server` 容器。包 SHA256 `3b4d2e4534aeae04194855ad96a2572faf7e0ac7f22377c929ae3b7d419162dc`；2026-11-01 10:26:22 UTC 启动，宿主及容器内全部 61 个发布文件与本地最终包一致。运行状态 running、restartCount=0，加载 6 credentials / 6 users / 30 chat messages 和 framework trade state，监听 16200/UDP。

先停服取得一致数据备份 `/opt/phinix/data.before-p4-20261101-102620.tar.gz`（仅云端 root 可读），旧程序保留 `/opt/phinix/app.before-p4-20261101-102620`。原 server.conf 字节校验未变，runtime 镜像、挂载、端口及 restart policy 保留。此次是程序升级，仍使用既有 ClientKey/UDP；未启用 ModernIdentity/TLS、未迁移玩家身份、未发布工坊或合并分支。未执行公网客户端联机或真实资产验收。

命令实际执行通过：scp 上传；SSH 下 sha256 / 安全归档检查；docker stop；tar 数据备份；程序目录替换；docker start；docker inspect / logs；宿主及 docker exec sha256sum 全文件核对。完整无凭据部署记录在工作区 `Deliverables/Phinix-Rework-P4-Closeout-2026-11-01/Deployment-2026-11-01.json`。本补记是部署后的新证据，不改变归档包及其原始 manifest/source 快照。后续仍按 P5 → P6 推进。
