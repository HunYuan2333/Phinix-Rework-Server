# 服务端测试与质量保障专项规划

> 2026-10-16 用户确认加速实施：当前开发/阶段门槛以 [RoadMap §0](Server-Upgrade-Roadmap.md#0-加速实施范围2026-10-16当前有效) 和 [§14](Server-Upgrade-Roadmap.md#14-加速版最小验收与后续执行) 为准。本文完整矩阵保留为扩展清单，不再全部前置或默认阻断。P0 最小前置已满足，直接推进 P1 产品实现；覆盖率/生产预算/全故障与完整三 profile 矩阵可后补，未测不标通过。资产/身份底线和针对当前改动的必要回归保留。

日期：2026-10-14，以下主体保留原规划与历史评估口径。2026-10-15 已开始实施：串行检查入口、安全回归、FsCheck 小试点与独立报价模型/架构基线、原版模块构建与登录聊天派生 capture 已有本机证据；见 [首轮证据](Server-Upgrade-Evidence-2026-10-15.md)、[模型/原版续验](Server-Upgrade-P0-Model-Evidence-2026-10-15.md) 与 [RoadMap §10](Server-Upgrade-Roadmap.md#10-2026-10-15-首轮垂直切片验收)。published 新 Server 的三 profile Harness、SQLite、运行时故障接口仍待实施；远端 CI 尚未运行。没有采集覆盖率，不给出覆盖率百分比。

依据：[RoadMap](Server-Upgrade-Roadmap.md)、[现状评估](Server-Assessment-2026-10-14.md)、[协议与无密码身份](Server-Protocol-and-Passwordless-Identity.md)、[静态性能](Server-Static-Performance-Audit-2026-10-14.md)、[容错与恢复](Server-Fault-Tolerance-and-Recoverability-2026-10-14.md)。现状指向真实文件；未来类型/项目/事件名称均为设计草案。

## 1. 封闭开发与测试退出门槛

P0～P5 不要求各自对外发布，每一切片必须带可重复回归并形成阶段证据。保持 P0～P7 及既有依赖；测试从 P0 起建，不把基础设施和资产/身份故障测试推迟至 P6。

为落实“第四批核心开发完成已有自动回归”，仅增加验收汇总口径：B1=P1 风险止血，B2=P2 会话/协议，B3=P3 事务资产，B4=P4 身份 + P5 必要 Legacy 能力；P0 是共同前置。这是验收批次映射，不重排阶段，P4/P5 按原依赖实施。B4 按 RoadMap §14 保留必要 L1/L2/网络/基本恢复和架构检查，复用已有基础；完整受控故障/强杀/混合矩阵按需扩展，P6 先完成声明范围真实客户端验收。

现代协议、可靠资产、自动身份及必要 Legacy 支持/拒绝范围均达到门槛后才评估替换旧服务端。2026-10-16 用户确认旧端普通交易采用 [尽力交付范围](Server-Trade-Persistence-and-Legacy-Delivery.md)：离线保留、重连尝试，已可能发送则保留未知，不合成 reliable ACK；要求可靠持久到账的能力仍须有效 receipt。物品不可无损表达继续明确拒绝，不把测试跳过包装为完成。

## 2. 仓库现状与证据

| 证据位置 | 当前能力 | 缺口与不能替代范围 |
|---|---|---|
| `Tests/Phase35ServerRuntimeTests/Phase35ServerRuntimeTests.csproj`、`Program.cs:12`、`:390` | net10.0 控制台 Exe，只引用 ServerRuntime；delegate PipelineTestHarness；8 个 Main 场景、13 条历史执行断言 | 无真实宿主、网络、auth/UserManager/TradeStore/SQLite；不是标准 dotnet test 项目 |
| 同一 Program `AssertLegacyApisRemoved` | 源文本检查 NetServer 不出现 Abort( | 不是程序集依赖/领域架构完整约束 |
| `Dependencies/Phinix.Common/Tests/Phase35CoreRuntimeTests/Program.cs` | Main 定义 4 场景：设置恢复、存储路径、API owner/policy、结构化日志；net10 控制台 | 本轮未重新执行，不能自动算 Server 业务覆盖 |
| `Dependencies/Phinix.Common/Tests/ExtensionLoaderRuntimeTests/` | net472/net10.0，加载测试需要 args[0] fixture DLL 目录 | 调用/fixture 构建需清单；不是现成独立 Server plugin CI 测试 |
| 同级客户端 `Phinix-Rework/Tests/LegacyTradeRuntimeTests/Program.cs`、`.csproj` | 真实 adapter/Trade/序列化组件，内存传输、旧包确认与整批失败；项目 net472，含 --baseline | 不是真实 UDP、服务器 outbox 或实际游戏到账；不能把 baseline 预期当产品目标 |
| 本地 `local-compatibility-analysis/server-assessment-20261014/probe/` | 已有真实 DLL 合成资产缺陷复现 | 非仓库内正式套件，不能靠绝对路径/临时产物在干净 CI 运转 |
| `.github/workflows/docker.yml`、`Dockerfile` | ubuntu-latest、20 分钟工作流，递归固定子模块，buildx publish/load；Dockerfile publish 主图/检查制品 | 没有测试/coverage/协议 smoke 步骤，镜像 load 不等于启动或恢复验收；Docker COPY 不含 Tests |
| `Tests`、Common 两个测试工程以及 `.github` 的依赖/配置检索 | 所查图未见 FsCheck、xUnit/NUnit/MSTest、TestSdk 或 coverage collector | 没有已测 coverage；不能宣称全仓所有客户端工程均没有测试框架 |
| `Server/Server.cs:39`、`NetServer.cs:111`、TradeStore Save/Load | 当前静态 Main、固定文件/轮询、全量保存和私有 store | 缺可重复宿主启动控制、确定性 clock/barrier、提交故障点与持久状态 oracle |

本机只查询 SDK（10.0.112）和 HEAD（5a0508a）；此前构建通过仅沿用历史证据。本轮不重新 build/restore，以保护仅规划边界。行/分支覆盖须 TQ-00 正式测量：排除 vendored/generated protobuf，单列 adapters/domain/auth；报告未覆盖路径及分母，不用总百分比代替最高风险矩阵。

## 3. 四层测试及职责

| 层 | 职责/真实对象 | 频率与环境 | 不可替代 |
|---|---|---|---|
| L1 单元/属性/模型 | 纯意图转换、协商/权限、session 与 Trade 状态机、操作指纹和不变量；固定例子 + seed 生成 | P0 开始，相关提交；net10 SDK，无网络/游戏；旧框架身份单列 | 不证明 serializer wire、真实事务、持久交付或网络 |
| L2 组件/集成 | 真 domain/registry/pipeline/adapter、真实 serializer、插件 DLL、真实 SQLite 文件/连接、身份和恢复 | P1 起扩展，相关提交快子集、阶段合并全组；独立临时目录，固定依赖 | mock commit 不是真 COMMIT；内存数据库不证明重启/WAL；不替代 socket/game |
| L3 协议/E2E | 独立进程真实 published Server + 多 profile Harness，实际 socket/transport 完整流程 | P2 起小规模 loopback 每阶段；P5 混合矩阵；固定端口分配/测试身份 | headless 原子到账模拟不证明 Thing/Scribe/地图、存档或工坊客户端 |
| L4 故障/恢复/性能 | model traces、定点异常/强杀、网络故障、SQLite/系统 IO 故障、压力及预算 | P1/P3 起短故障子集；每阶段重复相关点；长测定期/手动、RC 全套；隔离 VM/专用机 | SIGKILL 不等于断电；注入 SQLITE_FULL 不等于真实卷满；hosted CI 不等于生产磁盘/游戏 |

Mock 仅隔离明确外部系统（clock 调度、外部通知、安全信任 fixture、受控 IO 故障响应），真实业务组件/serialization/DB 不替换成“返回成功”。使用真实 plugin assembly 测注册/激活/失败清理，不能只调用替身 Register。

### 框架选型

首选评估 FsCheck 的 C# 属性生成、shrinking/replay：官方 [运行与重放](https://fscheck.github.io/FsCheck/RunningTests.html) 支持控制台/测试 runner；[模型测试文档](https://fscheck.github.io/FsCheck/StatefulTestingNew.html) 明示实验性，适合验证思路而非强制依赖整套 API。[FsCheck 包元数据](https://www.nuget.org/packages/FsCheck/) 当前标明 netstandard2.0，net10 测试工程具备评估条件；FSharp.Core 等测试依赖、版本与 runner 兼容需 P0 实测锁定。

TQ-00 先保留现有 Exe，统一结果/退出码/入口，不先迁移全部旧测试；TQ-01 用小规模三属性/状态序列试点决定 FsCheck + 现有 runner 或一个标准 runner。xUnit 等可改善过滤/结果采集，但本轮不新增包、不选未经试编的精确版本、不强迫新 runner 进入 net472 产品图。若 FsCheck 成本不合适，退回小型 C# 独立模型 + seed/trace 生成与显式缩减，仍须属性、失败复现及 oracle 独立性，不自建通用测试平台。

shrinking 必须重置 DB/进程/随机和时间输入；真实并发/网络只靠随机种子无法完全确定，另记录调度/投递 trace 和 barrier 命中序列。随机通过不是证明，应配边界 golden 和手工关键故障矩阵。

## 4. Headless Protocol Harness 可行性与边界

### 源码层面的可行性

原版 `Phinix/Common/{Chat,Trading,Authentication,UserManagement,Connections}` 工程目标 net472；所查引用是 Common、protobuf、LiteNetLib 和基础库，没有 Verse/Unity/RimWorld。`ClientChat`、`ClientTrading` 操作协议 DTO/事件，`Thing.proto` 是 ProtoThing 描述而非 Verse.Thing。可复现登录/聊天/交易 wire 与 token/字段方向，不能复现游戏物品物化。

旧 Connections 固定 `.nuget/LiteNetLib.0.7.7.3/lib/net35`；旧 Utils 含 Pastel/System.Drawing，旧 protobuf 与 SDK 图也需验证。当前 Server 使用 Common `libs/netstandard2.0/LiteNetLib.dll`；不能仅因名字相同认定跨 runtime/旧包实现直接兼容。本轮未编译或跑旧 client module，结论为“高可行性、P0/P2 构建与运行 spike 待证实”。

原版 CompleteTradePacket 没有现代 deliveryId/ACK（真实 `Packets/CompleteTradePacket.proto`）；原版也没有现代版本范围/feature/schema hello。Harness 必须 faithfully 标成未提供/不支持，不能给原版凭空加握手或 ACK。未升级 Rework profile 使用当前 FrameworkPacket/FrameworkSerialization 与 ClientKey/fallback 行为；现代 profile 随 P2/P4 定稿，不照搬服务器 Adapter 当独立客户端。

### 最小模块与构建隔离（全部待实现）

| 模块 | 职责 | 独立性与输入 |
|---|---|---|
| Process supervisor | 从 publish 目录启动 Server，分配 loopback 端口/独立 cwd，监听 readiness、deadline、重启/清理 | 不引用 Server private state 制造成功；版本/哈希固定 |
| WireProfiles | Original、ExistingRework、Modern 三类 encoder/decoder/会话驱动 | 复用审核过的协议定义、真实 serializer；禁止引用 Server Adapter/其归一化方法 |
| Network driver | 与 Server 匹配的真实 transport、poll/安全连接、收发 timeline | 不把内存 dispatch 标成 L3；不同 old protobuf/程序集冲突时分 client 进程隔离 |
| Scenario runner | 客户端意图、聊天/交易/查询、收发条件、重连、多连接/epoch | capability 缺失来自 profile，预期来自独立规范/model |
| Impairment scheduler | 可记录的 application replay/order 与网络故障控制 | 层次分开，不绕过生产 auth/protocol gate |
| Asset/identity oracle | 合成 opaque asset lineage、client 持久账本、重启后 DB/结果读取 | 不复用服务端 TradeStore 状态转换；只核对生产可观测事实 |
| Reporter | JSON summary、JSONL trace、JUnit/TRX（随 runner），seed 与最小失败序列 | secrets 脱敏、稳定 test/scenario ID、非零退出失败 |

优先复用固定 proto/generated DTO 和现有 serialization，不复制新协议语义、不修改正式协议。若抽取旧 wire-only 项目，采用审核的固定来源/sha，不能改 vendored 工程或要求 CI 随意读取本机 sibling checkout；原版兼容 runtime 的独立 fixture producer 可先在 Windows/.NET Framework 环境验证，最终基于实际运行结果选 CI lane。

### Legacy fixture/golden 来源

P0 获取真实原版与未升级 Rework 的客户端测试身份发送/接收记录；每个 fixture 记录 source SHA/build、方向、module/Any TypeUrl、字段、codec、capture 工具、原始 bytes hash、预期业务意图及来源类别。现在没有已核验的独立 raw capture corpus；现有 LegacyTradeRuntimeTests 创建的包只能标为 synthetic/component fixture。

golden 优先由真实旧客户端或独立旧 ClientChat/ClientTrading 产生，与生产 adapter 无代码依赖；手审 token/视角/空报价并做负对照（交换双方字段、删 token、损坏批次）。不能 encoder 和 oracle 都调用被测 adapter，不能只做同一 serializer 的 roundtrip。protobuf 非确定字段/map 顺序要明确“原 bytes 回放”与“语义比较”，不能随意重编码后冒充原始 capture。

所有 fixture 仅用专用合成身份、空私服/合成资产；不上传真实玩家秘密。需替换字段时另存脱敏派生 fixture 和转换记录；受签名保护的字节不能简单改秘密后宣称原合法包。合法会话动态字段由独立 profile 绑定，fixture 原 bytes 仍保持来源，记录哪些字段做实例化。

### 分期与 MVP

- P0：supervisor/result/fixture 目录规范与旧 module 独立构建验证；最小 1 个旧 wire 用户的连接/登录/聊天 fixture smoke。旧 adapter 尚未接入时用原版测试 server 验证 fixture producer，不能标新 Server Legacy 已通过。
- P2：1～3 现代候选/未升级 Rework 真连接，version/feature/schema/session/reject、普通聊天与重连；原版 profile 保持真实旧协议，预期新服务暂拒绝/未接入。
- P3：2 个现代端真实 Trade/outbox/ACK/query、持久合成 client ledger 与定点重启；此时就验证事务，不等 P5。
- P4：安全连接、自动身份/UUID 迁移、secret 保留/损坏、拒绝降级；旧规则仍在 adapter。
- P5：原版/未升级 Rework 必要兼容、支持/拒绝的全部矩阵；小混合场景每阶段，30 原版 Legacy + 70 现代场景作为 L3/L4 参数配置，不视作已验证容量。另测原版/未升级/现代三类同时在线，报告准确 profile 数。
- P6：固定候选与真实游戏联机，场景/trace 与 Harness 对照；完整压力由 SV-05 预算判定。

ReliableOrdered（`NetServer.Send`）会隐藏部分 UDP 重排/丢包/重复。区分：重发应用 operation、按不同合法发送序列制造业务乱序；代理/netem 制造 transport 丢包/时延；故意截断/错误 envelope 通过实际 wire 送入边界。加密后的 bit flip 应被 transport 拒绝，不期望进入 serializer。日志注明故障所在层；丢 transport datagram 不等于丢 application response。Harness 无 GUI，但无法替代 RimWorld 的 Scribe/Thing/地图到账、库存预留、主线程、存档与工坊更新验证。

## 5. 资产不变量与独立模型

### 状态与身份

测试给每个合成资产一个稳定 testAssetId/lineage、payload hash、origin principal、批次/operation、owner 和 escrow/delivery 状态；拆分/合并用 lineage/数量分配记录。测试 ID 存在于合成可合法携带的 opaque fixture/sidecar，不要求本轮或正式 wire 新增字段。实际产品缺少全局资产 ID 时，只能对可关联谱系范围声明保证，不能把 test oracle 产生的 ID 当成生产已有能力。

允许状态由独立业务规范模型描述：ClientOwned → ClientEscrowPending → ServerCustodied → DeliveryPending → ClientDelivered；拒绝在证据足够时恢复上一 owner；cancel 的原资产返还也是有证据的 delivery；断线/超时/未知响应保留 escrow，不自动返还。状态是测试参考模型，当前产品不一定已有这些类型/日志。

内容相同的两件资产不是同一件；同一资产在多处保存的恢复副本不是多份可支配所有权。证明对象是“可消费权唯一、完整 payload/谱系关联、状态转换合法、持久交付幂等”，不是 SQL row count、stack count 或物品数量总和。

| 不变量 | 证据边界 | 实例/故障矩阵与预期 |
|---|---|---|
| AS-01 整批完整与权限 | Server 可独立证明引用/owner/参与者/payload 不被部分提交；游戏可解码性另验 | 缺一项/全缺/重复/跨 owner/非法 trade/codec/preview：整批拒绝，不改报价、不消耗合法引用；FR-01 |
| AS-02 稳定操作结果 | Server 可独立证明 durable result + fingerprint | duplicate 同输入读原结果；同 operationId 不同内容拒绝；并发重试不再次执行；仅缺 token 旧适配不能造确认 |
| AS-03 托管到交付原子性 | Server 可独立证明 custody/state/outbox 在同事务；不能证明客户端资产起源真实 | create/update/accept/cancel：成交/取消不出现既无 custody 又无 durable delivery 证据；COMMIT 前后强杀；FR-02/03 |
| AS-04 已提交响应丢失 | Server 结果 + Client escrow trace 必需 | client 不自动退款；原 operation query/重连得到原结果，late/unrelated snapshot 不制造确认 |
| AS-05 delivery/outbox/ACK | Server 证明持久 outbox/receipt；ClientDelivered 必须 client durable 证据 | duplicate event、ACK 丢/迟/错主体/错 delivery、未知 ACK：最多一次有效消费，ACK 只清对应投递；FR-12 |
| AS-06 玩家实际到账与恢复 | 真实 Client ledger/保存/交付证据，Headless 只能模拟 | crash 本地到账前/后及 receipt 前/后；重复送不重复物化、取消不错误退款、关窗/跨存档不丢 escrow |
| AS-07 session/timeout/multi-peer | Server session+model 与 client reconnect trace | 过期不参与/接收；epoch stale 拒绝；多连接同身份策略唯一；超时不隐式退款；FR-09～11 |
| AS-08 opaque 资产保真 | Server hash/metadata + real client codec/materialization | 混合协议无法无损表达在业务前拒绝，缺 Mod/Def/codec 不部分到账；Legacy 不能合成 ACK |
| AS-09 Chat 的语义和输入隔离 | 真 Chat service/serializer+protocol | history 完成不等于每项已持久；reply/mention 缺能力拒绝/准确支持；畸形 Chat 不影响独立 Trade，secret 不在用户增量/全量包 |

Chat 当前是消息/历史而非资产账本（`PhinixFrameworkChatService`）；测试不创设 Chat 资产交易语义。资产起源、游戏作弊、服务器可信托管也不是服务器数据库能够独立证明的命题。

L1 用 model-based/property-based 序列（create、offer、accept、cancel、disconnect、retry、query、deliver、ack、crash/restart），显式合法/非法 precondition 与并发交错，逐步对比领域结果；L2/L3 同一 reference model 检查实际组件/网络可见记录。oracle 不调用 MutableTrade/TrySetStatus/adapter 翻译当 expected。

当前缺口：Trade store 私有、无 stable delivery receipt/query 与未来 COMMIT hook；客户端跨存档 escrow 恢复仍未完全证明（SV-04/R44 gates）。TQ-30/31、SV-03/31～35 规划非秘密、只读持久状态查询/验证器与独立 client ledger；需测试-only clock/故障 seam 时独立评审，不能本轮添加。Headless ledger 通过只计 server 协议/模拟持久客户端，真实 AS-06/08 是 RC 阻断。

## 6. 确定性故障注入设施

稳定 faultId + occurrenceIndex + operationId + seed + 调度/投递 trace + barrier 命中记录；不要只随机 sleep。Barrier 由 supervisor 收到命中确认再执行 kill/release，证明实际走过目标点。每次使用独立 DB/数据目录/端口/测试凭据，从已记录 initial-state hash 重放。

| Fault ID（待实现） | 位置与类型 | 验证 |
|---|---|---|
| FI-VALIDATE-BEFORE / AFTER | 入站/整批验证前后；受控异常或 barrier | AS-01 无半批副作用，拒绝和原引用可核对 |
| FI-TX-OPEN / PRE-COMMIT | 真事务开始后、COMMIT 前；异常/rollback/强杀 | 未提交无权威成功，重启后原持久状态 |
| FI-POST-COMMIT-PRE-SEND | COMMIT 后发送前；进程终止/应用响应丢失 | 独立重启连接验证结果/outbox，AS-02/04 |
| FI-POST-SEND-PRE-ACK | 已派发响应/投递后 ACK 前；断线/重复 | send 返回不当成到账；重连幂等补投 |
| FI-ACK-PRE-PERSIST / POST-PERSIST | ACK receipt 写入前后；kill/异常 | 未持久继续 pending；已持久不重复有效交付，unknown ACK 不伪证 |
| FI-CLIENT-PRE-APPLY / POST-APPLY | headless ledger 或实际 client 的本地到账前/后 | 帐到 receipt 间隙能核对；游戏 hook 需单独批准，不能以 headless 代替 |
| FI-SQLITE-BUSY / FULL / CORRUPT | real DB 多连接写锁、受控 provider IO、受损 copy | busy 重试有界，disk-full 不成功，损坏只读保护；事务失败与 commit 结果不猜测 |
| FI-PLUGIN / WORKER / LOGSINK | plugin predicate/handler/observer、worker 退出、日志不可写 | 非权威隔离，权威 gate 不放行，未知副作用不再次执行；FR-06～08 |
| FI-GRACEFUL-STOP / PROCESS-KILL | SIGTERM/drain 与 SIGKILL/进程终止分开 | 在途已接纳操作/持久点可解释；Stop timeout 不冒充退出 |

三种级别分别出报告：A 受控异常/测试 seam（不证明真实 OS IO）；B 子进程终止/网络代理（不证明断电）；C 限额专用卷盘满、独立 VM 文件系统/断电模拟（不泛化所有硬件）。真实 SQLITE_BUSY 用独立连接保持写锁；内存 DB/假 Repository 不用于耐久恢复。disk-full/corrupt 只在隔离数据副本/专用卷，包含 WAL/SHM 与正确备份；不能触碰玩家文件或填满 CI 主机。

未来故障机制只在测试构建/受控宿主组装启用，独立私有 IPC 权限、专用目录与进程凭证；生产普通 socket 没有“faultId”命令，玩家 payload 不能触发 barrier/kill。RC 需扫描生产制品并用正常/恶意客户端证明无法启用注入。无权修改生产时可先做 supervisor/process/网络层故障；事务准确定位仍需批准 seam 之后才能声称通过。

重启后先只读核对 authoritative DB/operation/outbox/receipt/schema/health，再跑正常 query；不能用内存镜像、同一未退出连接或 sqlite rows 总数充当 oracle。清理只杀本 run 启动 PID/process group，确认退出再释放端口/删除目录；失败先归档脱敏 DB/trace，再清理，记录未清理资源，禁止 kill-all/network 全局规则。

## 7. 三 profile 兼容矩阵

O=原版 Phinix，R=未升级 Rework，M=现代 Rework；下表是目标测试预期，未实现字段为不支持，不能把 O/R 补成 M。全部 profile 均用真实 wire，supported 范围在 P2/P5 定稿；必要兼容要证明支持和拒绝两条路径。

| 维度 | O | R | M |
|---|---|---|---|
| version/feature/schema | 无现代 hello，Legacy profile/明确限制，不伪协商 | 当前 V2 capabilities，不等于 schema/security 保证；过渡规则只在 adapter | min/max 与 schema 交集，unsupported 明确拒绝 |
| auth 失败/过期/撤销 | 旧 ClientKey 映射 gate；旧失败包可读 | 旧登录映射/迁移，现代失败不静默降级 | 自动凭据/可信通道，expiry/revoke 后无入站与订阅 |
| 多连接/epoch | profile 映射后统一规则；无旧端 epoch 也不能绕过 server epoch | 重连不能用陈旧 token/等待关联确认新请求 | epoch 固定，多连接按已定策略，无重复有效消费 |
| 普通聊天/历史 | 旧 Chat 包与字段方向准确；支持或可读拒绝 | 当前 Chat/Framework 路径与真实包验证 | 正常发送、分页/完成边界、畸形隔离 |
| 回复/提及 | 不能表达就拒绝，不能删语义字段伪成功 | 仅依据实际双方能力，缺 feature 明确拒绝 | optional feature/schema 缺失/不兼容均拒绝 |
| Trade/可靠交付 | 尽力交付：离线待交付、已发送未知保留；无现代 ACK 不合成，不盲重发/退款；不可表达的资产功能拒绝 | 不因 V2 标签默认 durable ACK；校验实际契约 | operation/query/outbox/delivery receipt 完整，故障幂等 |
| opaque/整批无损 | 不认识 codec/状态则业务前拒绝整批 | 仅真实 codec 支持；哨兵转发不证明解码 | hash/preview/codec 验证，游戏物化另验 |
| mixed room | O↔O/O↔R/O↔M，普通兼容及能力拒绝 | R↔R/R↔M；各方向/token/recipient 正确 | M↔M/M↔R/M↔O，禁止双方能力不足还成交 |
| Legacy adapter 关闭 | 旧入口明确拒绝，无宿主隐藏旧分支 | 过渡 profile 不再接受，不能改走 M 欺骗身份 | 现代业务保持正常；未降级权限/回执标准 |

组合不只是客户端对数：另测身份/feature不对称、消息方向、满历史、断线/重启、重复 ACK、关闭 adapter 前已接纳未决操作的恢复政策。关闭 adapter 不直接删除持久资产；迁移/撤销遵统一准入与恢复策略。

架构约束从 P0 就规划，在新边界建立后更新基线：检查 csproj/编译图与 compiled IL 的类型/成员依赖，禁止 domain 引用 Legacy packet/DTO、旧凭据类型、ProtocolMode/Legacy gate；允许 adapter 单向引用 domain，共享 DTO 按角色精确 allowlist，不用命名 grep 一概禁用 Utils。源码/语法检查捕获 `isLegacy`/V2 分支和 string 字段，补行为负对照（故意引入违规依赖检查必须失败）。reflection/dynamic/string 路由仍需审查，静态规则不能证明绝无泄露。现有领域/transport 尚混合，SV-03/TQ-01 建当前违规 baseline，批准例外有 owner/撤销任务；不让无限 allowlist 变成通过标准。P5 退出 legacy-specific domain 违规清零，SV-54 同业务意图双 profile 的 oracle 必须一致。

## 8. CI、执行与归档

以下执行入口/runner 为待实现规划，本轮不改 `.github/workflows/docker.yml`。保留现有 publish 流程，未来增加质量 jobs 作为 required gates；不能把相关提交测试失败后仍发布镜像当安全交付。

| lane | 时机/内容 | 环境、建议初始上限及门槛 |
|---|---|---|
| Quick | 每次涉及 Server/Common/契约/adapter 的提交及 PR；L1 固定+小 seed、L2 快场景、架构、secret 检查 | hosted Linux net10 固定 SDK；目标 5～10 分钟，job 暂定 15 分钟，测量后调；exit 非零阻断相关合并 |
| Stage | 每次阶段合并候选；全 L1/L2 + 当期真网络 L3 + 短 kill/controlled故障 | hosted loopback/private ports，真实 publish 子进程/临时 SQLite；暂定 20～30 分钟，不能继承现镜像20分钟假定所有套件塞得下 |
| Extended | 定期或手動；更大 seed、30/70 混合、积压、重复终止与网络故障 | hosted 能稳定的短子集；长期压力/特权 netem/专用卷转自托管或本地隔离 VM；建议 run 60～120 分钟上限，单场景 deadline 独立 |
| RC | 固定 SHA 制品完整自动矩阵 + 真实 O/R/M 客户端、游戏保存/交付/工坊迁移 + 系统故障/压力 | 专用环境，与 hosted 报告汇合；必须版本签名/证据齐全，不要求 GitHub runner 跑 GUI；不能把预算限制当跳过理由 |
| P7 rollout | 实际发布前重验候选清单/备份迁移，发布后受控测试身份 smoke | 部署者/专用环境，禁止测试污染玩家账本；异常停切换，依既有回退方案 |

P1 已运行短 host/日志故障，P3 已运行定点强杀/事务恢复，P4/P5 已运行身份/混合协议；P6 只是扩大样本与真实环境，不首次测试这些。网络端口、CPU/内存/PID/磁盘预算在 SV-05 固定：30/70 是场景不是容量承诺。timeout 与 OOM 报失败/环境故障，不能标 skip/pass；无实际 listener readiness 不靠 sleep 假定启动成功。

初期可用现有 console build+execute 入口，未来标准 runner 选定再声明 dotnet test；不会静默忽略 Exe 项目。fixture build 先行且 exact pin，Linux/net10 lane 与必要 Windows/net472 producer/实际客户端分开，不编辑 protobuf global.json 解决 SDK 错误。恢复测试 SQLite WAL/synchronous/schema 与制品相同，不改成 memory DB 提速后仍声称 durable。

artifact manifest：testId/layer/profile、Server/Common/Client/plugin SHA及dirty diff hash、SDK/runtime/OS/transport/protocol/schema、binary/fixture hash、seed+shrunk steps+schedule、initial-state hash、faultId/命中/进程exit、结果/skip理由、timeout/资源指标、脱敏 logs/JSONL/JUnit或TRX、必要 DB/WAL快照与 query oracle。多 seed/fixture 失败聚类按最小 trace 重放。所有日志/DB 只含合成身份；密钥不入 artifacts；secret 扫描必须 fail。保留期/容量在 TQ-00 定，失败与RC保留长于普通成功，归档失败也可见，不能缺证据标通过。

flaky：首次失败保持失败，允许一次诊断重放但后续通过不抹掉原失败；登记 owner、原因、issue、截止阶段、失败 seed/trace。环境抖动与产品 race 分开，更新环境须重验。资产、身份、协议降级/边界项不得以 quarantine 绕过阶段/RC；其他非关键项可限期隔离且报告覆盖缺口，截止后恢复 gate。测试用 barrier/逻辑时间降低 flake，真实 timeout/并发用记录 trace；不能无界 retry 或放宽安全断言。

## 9. 关联 RoadMap 的测试任务

任务不复制 SV 功能实现：SV 仍是产品/原审计任务，下表 TQ 是配套测试交付与退出证据。所有任务待实施；自动化 A=CI、B=自动专用环境、C=真实客户端人工步骤+机器证据。阻断 Y=对应阶段退出及 RC；Y-RC=发布前完成，Y-when=对承诺的环境/能力适用。

| ID | 阶段 | 依赖 | 测试目标 | 实现范围 | 验收标准 | 自动化 | 阻断 | 原任务/审计 |
|---|---|---|---|---|---|---|---|---|
| TQ-00 | P0 | SV-00/01/05 | 基线可执行可追溯 | 现有Exe入口、结果/coverage/manifest、临时进程/目录supervisor、quick/stage设计 | 干净checkout跑旧套件；错误/timeout非零；缺coverage不伪报；框架小试点锁版本 | A | Y | SV-00/01/05、PF-12、FR-07 |
| TQ-01 | P0起、P2/P5扩展 | TQ-00、SV-03 | 独立不变量/架构与属性可复现 | 三属性试点、reference model、seed/shrink、程序集/语法allowlist与违规baseline | 缺陷负对照会失败；独立oracle；可重放；P5核心旧规则违规清零 | A | Y | SV-03/54、FR-01/08/12 |
| TQ-02 | P0 | TQ-00、SV-02/04 | 已知缺陷与独立wire来源 | 将本地资产复现变正式场景，fixture/golden provenance与旧module构建spike | 预修复检测到FR-01/14；修复后相同目标断言变绿；旧wire独立性与游戏依赖检查有证据 | A/B | Y | SV-02/04/50、FR-01/14 |
| TQ-10 | P1 | TQ-00～02、SV-10～19 | 资产/宿主同步回归 | L1/L2批次/owner、public DTO、发送失败；L4日志/保存/EOF/stop/queue短故障 | 缺失/全缺无副作用；增量/全量无secret；排他保存/停止与超预算有确定结果 | A、OS子集B | Y | SV-10～19、PF-01～07/12、FR-01/04～07/14 |
| TQ-20 | P2 | TQ-00/02、SV-20～24/29 | 真网络最小Harness | supervisor + 3 WireProfiles基础，现代协商/拒绝/聊天；原版未实现功能只标拒绝 | 真实published Server/socket，1～3 client smoke，版本/能力/schema反例与记录可CI执行 | A/B旧runtime | Y | SV-20～24/29/50、FR-09/13 |
| TQ-21 | P2 | TQ-01/20、SV-25～28 | 会话和异常gate正确 | clock/barrier计划、expiry/revoke、epoch、多连接、predicate/observer错误 | 过期无订阅，旧epoch不确认；授权异常不放行，未知副作用不继续默认业务 | A | Y | SV-25～28、PF-09/10、FR-08/10/11 |
| TQ-30 | P3 | TQ-01/10/20、SV-30～34/38 | 真实SQLite与模型资产证据 | file DB/多连接、true components、独立读oracle/client持久ledger、AS矩阵 | operation冲突/幂等，state/result/outbox原子，delivery/receipt身份；重启不用内存oracle | A | Y | SV-30～34/38、FR-02～04/06/09/12 |
| TQ-31 | P3起 | TQ-00/30、SV-35/36 | 确定faultId与恢复 | 本文FI稳定点、barrier/supervisor、短强杀/busy、受控IO/重投trace | 每点命中有证据，重启持久核对，固定seed/trace可复现；生产peer不能启用 | A，系统IO B | Y（系统全集Y-RC） | SV-35/36、PF-03/07/12、FR-02～07/12 |
| TQ-40 | P4 | TQ-20/30/31、SV-40～45 | 自动无密码/安全/迁移 | 真安全transport、信任负例、token/UUID、持久前后kill、轮换撤销 | 原UUID保留、不重建；所有业务通道保护；不降级；secret不入制品/fixtures | A/B | Y | SV-40～45、FR-10/14 |
| TQ-50 | P5 | TQ-02/20/30/40、SV-50～54 | 独立Legacy golden及转换对照 | O/R与M 双向 fixture + 真Server，普通功能/准确拒绝、adapter关闭 | golden不从被测adapter生成；核心无旧依赖/特判；不能无损时业务前拒绝 | A/B | Y | SV-50～54、FR-01/12/14 |
| TQ-51 | P5 | TQ-21/31/50、SV-53/54/36 | 混合场景并发/恢复 | 全部O/R/M对向、小混合与30O+70M参数、延迟/replay/重连 | 各profile/方向明确计数；不伪ACK；能力不对称无资产副作用；录schedule与预算 | A小规模/B大规模 | Y（代表规模Y-RC） | SV-33/36/53/54/66、PF-04～07、FR-09～12 |
| TQ-60 | P6 | 全部P0～P5 TQ、SV-60～64 | 候选真实客户端闭合 | O/R/M正式/候选客户端，实际Thing/Scribe/库存/存档，工坊凭据保留 | AS-06/08真实持久证据；完整矩阵支持/拒绝；没有用headless替代游戏验收 | C+B | Y-RC | SV-04/60～64、FR-01～03/09～12/14 |
| TQ-61 | P6 | TQ-31/51、SV-05/66/67 | 负载/真实系统故障发布证据 | trace、专用卷满/损坏、慢插件/worker、强杀与恢复/长压力 | 既定预算/RTO、有界资源、确认资产持久保证证据；类型/平台未验不得声称支持 | A+B | Y-RC/Y-when | SV-66/67、PF-01～12、FR-01～14 |
| TQ-70 | P7 | TQ-60/61、SV-70～72 | 迁移/切换可核对 | exact制品/数据备份，受控身份smoke，版本门槛/退役/演练回退 | 无真实玩家污染、迁移保UUID；未更新端拒绝；异常止切换与数据一致恢复 | B+C | Y发布切换 | SV-70～72、FR-04/09/14 |

TQ-02 的已知失败是基线证据，不把当前缺陷固定成期望成功。P0 可记录带issue的 known failure，在 P1 修复退出必须全部通过；这些资产/secret项不允许延到 RC 再修。框架/测量任务的 Y 不表示所有可选工具强制安装；必需证据用最小可行实现满足即可。

## 10. 阶段退出与发布阻断

| 阶段 | 必须已有证据 |
|---|---|
| P0 | 原套件可重现、已知缺陷目标回归、seed/report/环境清单、legacy来源、模型/架构约束与框架小试点 |
| P1 | AS-01/secret/输入与发送结果 L1/L2、宿主正常停与短故障；无需对外发布 |
| P2 | 真网络Harness可CI，小协商/会话/拒绝矩阵、Ready/expiry/multi-session，架构规则随边界生效 |
| P3 | 真SQLite事务/outbox/client合成持久ledger、稳定故障点逐项强杀、query/ACK与独立oracle |
| P4 | 自动身份与可信全通道、UUID迁移全故障点；普通用户无密码与无静默降级 |
| P5 / B4 | O/R/M必要支持与拒绝、mixed room、关闭adapter、旧规则域泄露清零；前阶段套件累积全绿 |
| P6 | P0～P5套件已具备；实际游戏AS-06/08、代表压力/真实IO、固定RC制品清单与全部阻断关闭 |
| P7 | exact候选再核对、发布切换/回退及受控smoke；工坊已发布不等于全员更新 |

阻断：所有资产批次/提交/幂等/恢复、身份秘密/可信通道/降级、兼容拒绝/adapter边界、有效会话、关键配置/worker健康、支持范围下的系统故障，以及相关矩阵缺失/critical flaky。实际游戏持久交付不通过就不能发行可靠资产替换服务。

可延期：非热点 clone/index 微优化、更多随机样本/可选 runner UI、超出声明预算的更大压力、非支持平台长测。延期必须保留上限与理由；核心场景、已知 P0 缺陷、所有受支持 profile 的必要拒绝及最小真实网络不能延期。行覆盖百分比只做风险诊断，P0 定稳定分母/关键分支清单后维护，不拍一个数字冒充保障。

待决问题（不阻塞本轮文档/P0准备）：标准 runner 与FsCheck试点结论/版本；旧net472独立producer的可移植性；SV-40安全transport和服务器信任；测试-only clock/barrier/只读oracle的最小接口；真实客户端 escrow/交付证据设施；SV-05预算、CI专用机/系统故障权限与artifact保留期；发布最低版本/过渡窗口。由相应阶段证据收口，不默认扩大兼容妥协或给普通客户端故障权限。

## 2026-10-16 TQ-00 实施补充

SV-01 空私有缓存完整图与 Coverlet.Console 6.0.4 小试点已实际通过，原 Exe 保留，23 步 stage、限定三安全组件程序集的行/分支分母、包和工具 hash 见 [本轮证据](Server-Upgrade-P0-Restore-Coverage-Evidence-2026-10-16.md)。coverage 缺失/范围错误/空分母会失败；排除 generated/vendor/test，不宣称整个服务端覆盖率，不由百分比代替不变量/持久提交/真实游戏验收。历史“无 collector”的现状不再代表当前入口，其他未实施测试层级仍保持待实施。
