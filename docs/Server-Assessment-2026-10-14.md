# Phinix Rework 服务端评估 — 2026-10-14

本评估按传统专用服务端处理：网络、协议、会话、鉴权、事务、恢复、插件与运维。客户端只用于核对协议及业务回执要求。没有修改产品源码、切换分支、更新子模块、迁移玩家数据或启动真实服务。

> 2026-10-14 用户补充约束：所有旧协议兼容妥协只留在 Legacy adapter，新业务保持统一语义；普通玩家身份升级保持自动、无密码，不引入强制账户/密码流程。服务端仓库正式补充文档为 [服务端协议边界与无密码身份升级评估](Server-Protocol-and-Passwordless-Identity.md)。本报告此前泛化的密码/账号讨论不作为默认产品方案；以该文档的自动匿名身份、无密码迁移与 adapter 边界为准。

## 1. 决策结论

1. **当前服务端可编译，已是 C# / .NET 10。** 无需再用 .NET 重写；建议在现有宿主上分阶段重构。
2. **客户端与服务端没有整体 wire 契约断裂，但保障能力已经脱节。** Chat/Trade contracts 与 FrameworkPacket/FrameworkSerialization 当前文件一致；服务端交易整批校验、持久提交、恢复触发和结构化拒绝不足。
3. **应该引入服务端 Legacy adapter。** 当前旧鉴权/用户模块仍在，但原版 Chat/Trading 处理器没有装配；名为 LegacyServerTradeRuntimeAdapter 的文件只是存取包装，且未纳入 Server.csproj Compile 列表，不是可用的旧协议入口。
4. **应该把现有 hello/capabilities 升级为会话协议契约。** 现有 Version=2 包字段未在服务端入口检查，Session 不保存协议版本/能力；能力拒绝主要只有日志。
5. **建议引入 SQLite，首个切片是交易操作结果与待交付通知的原子事务。** 仅把 XML 换成 SQLite 再每分钟保存不能解决问题。
6. **鉴权需要升级，但不应误判 ClientKey 的既有语义。** ClientKey 是放在 Username 中的 512 位随机 bearer 密钥，Password 为空；不是“忽略普通账户密码便能任意登录”。主要风险是 bearer 密钥在传输、日志和存储中的暴露，以及缺少分层授权与滥用控制。

## 2. 基线与实际验证

| 项目 | 本次依据 |
|---|---|
| 服务端 | Phinix-Rework-Server HEAD `5a0508a`，首次代码评估时工作区干净；后续另有文档修改 |
| 服务端 Common | 固定子模块 `67f243d9edced77dc3fe669f8a435b1f14b59199` |
| 客户端 | Phinix-Rework HEAD `13412fe`；存在大量原有修改与未跟踪文件，未动它们 |
| 客户端 Common | 固定子模块 `be978cfbb3f9299c24fa3fa068ccc109262efc22`，另有原有本地修改 |
| 独立 Common | HEAD `85ecb18`，不能当成消费者当前自动使用的版本 |
| 老 Phinix | HEAD `ce82b8f`，Server 为 .NET Framework 4.7.2 |
| 本机运行时 | SDK 10.0.112 / Microsoft.NETCore.App 10.0.12，Linux x64 |

执行：

```sh
cd /home/hunyuan2333/Phinix/Phinix-Rework-Server
dotnet build Server/Server.csproj -c Release -p:BuildInParallel=false -m:1 --no-restore
dotnet build Tests/Phase35ServerRuntimeTests/Phase35ServerRuntimeTests.csproj -c Release -p:BuildInParallel=false -m:1 --no-restore
dotnet Tests/Phase35ServerRuntimeTests/bin/Release/net10.0/Phase35ServerRuntimeTests.dll
```

- 服务端编译成功，0 errors，最终 summary 2 warnings：vendored protobuf 的 NETSDK1212 和旧 Items 字段 CS0618。
- 官方 Chat/Trade 服务端 DLL 与各自 contracts 出现在输出 Extensions 目录。
- 现有测试：`PASS ServerFull 8 cases; 13 assertions`，只验证管线拦截、观察器隔离、出站处理等，未覆盖鉴权、交易重启恢复、实际 UDP 或旧客户端。
- 首次 `dotnet run --no-restore` 只返回 build failed，无详细原因；显式串行 build 后直接执行 DLL 成功。不能把 README 那条 run 命令也宣称验证通过。
- 隔离 probe 使用真实编译 DLL、合成输入，未启动网络监听。复现：缺一个引用返回部分物品；真实 store 接受该部分报价；合法引用在后续交易校验前已消耗；全局缓存同 ID 可覆盖。另确认 ClientKey 的 Password 确实不参与既有 bearer 认证，这项是契约确认，不是密码绕过漏洞。
- 编译依赖现有缓存，未验证全新 clone/restore、Docker 构建或实际游戏联机。probe restore 的 NU1900 表示漏洞源不可达，不是安全审计通过。

本机原始构建日志、测试日志与合成探针保留于工作区 `local-compatibility-analysis/server-assessment-20261014/`，不作为仓库内已附带的测试套件或发布依赖。该处的 probe 使用真实 DLL 与合成数据，未启动监听；实施阶段需将必要场景转成可在干净检出运行的正式测试。

## 3. 客户端与服务端对齐程度

核对了客户端 `docs/Compatibility-Boundaries.md`、设计哲学、Plugin-Development、R44 semantics/correlation/save gate 和 1.7 迁移记录；也阅读服务端对应文档。当前客户端真实工程入口是 `Client/Source/Client.csproj`，FrameworkClient 与 Output 中副本经对比一致。不能把 Output 文件一律当作真实 source。

| 组合 | 静态评估 | 本次验证范围 |
|---|---|---|
| Rework 客户端 → Rework 服务端 | Chat/Trade contracts、FrameworkPacket、FrameworkSerialization 一致；已有 hello/capabilities | 文件对照及服务端编译，不是联机通过 |
| Rework 客户端 → 老 Phinix 服务端 | 客户端 Legacy adapter 与兼容文档已存在；3 秒未协商成功进入 Legacy | 源码与文档，不是本次端到端通过 |
| 老 Phinix 客户端 → Rework 服务端 | 鉴权/用户模块保留；旧 Chat/Trading 模块未接入，不能承诺业务可用 | Server.cs 与老 Server.cs 装配对照 |
| 1.6/1.7 客户端运行时 → 服务端 | 引擎/CLR 版本不等于 wire 协议版本；不能因此要求服务器换语言 | 迁移文档明确服务器改造不在该路线范围 |

客户端与服务端 Common SHA 不同，客户端 FrameworkTypes 已有 provider/lifetime 等本地演进；这是 SDK/API 漂移证据，不自动等于网络不兼容。应建立契约兼容矩阵与共同测试，按迁移批次更新固定 gitlink，不执行 `submodule update --remote`。

## 4. 已发现问题，按优先级

### P0：整批物品引用被部分接受

`Extensions/Trade/Server/PhinixFrameworkTradeServerService.cs`：

- CacheItemPacket 是全服务 Dictionary，键仅 packetId，无 owner/session/trade scope；容量 200，缺 TTL/每会话预算。
- TryGetCachedItemPacket 成功就 Remove。
- ResolveOfferItems 对 missing ref 只日志并继续；全部 missing 会得到空报价。
- HandleOfferUpdateRequest 先解析/消耗引用，后检查交易与参与者，随后可能给部分报价发送 Success。

后果：缺失、淘汰、重复引用、无效交易可损坏请求语义；不同会话重复 ID 可覆盖载荷。跨会话任意读取还需要知道 ID，本次没有验证真实网络利用，但源码缺少 owner 校验是确定事实。

修复：先校验交易/参与者，再以 `(owner, session epoch, operationId, packetId)` 读取并验证完整集合、数量、大小、重复、codec/preview 约束；一项缺失整批拒绝，不修改报价/消费引用。整批提交后再消费，拒绝带原 operation/correlation ID。把 opaque 状态原样保存，服务端不加载游戏 DLL、不解释 Thing。

### P0：回执早于持久提交

TradeStore 使用内存 Dictionary/List，Save 为 DataContractSerializer XML 全量覆盖，尽管文件名是 `trade-state.bin`。Config.SaveInterval 默认 60000ms。报价成功、成交/取消及 completion ACK 都修改内存并发送结果，未在响应前持久提交。

后果：服务端已确认但未保存时崩溃，可能丢报价/待交付；客户端已到账、ACK 尚未保存时重启可能再次发通知。客户端幂等可以降低重复交付风险，但不能替代服务端持久权威；本次未实机复现崩溃结果。

成交时 TrySetStatus 删除 active trade，再以另一次锁获取 QueueCompletionNotifications。save timer 可在两者间抓到既无 active trade 又无 pending notification 的状态。这是源码可见的原子性缺口。

修复：业务状态更新、operation result、completion/outbox 同事务；数据库 COMMIT 成功后才发权威成功。投递失败保留 outbox，客户端持久幂等交付后 ACK，ACK 也事务提交。

### P1：握手未形成会话级约束

PhinixFrameworkServer.handleHello 在鉴权和登录后记录远端自报 capabilities，再返回服务器全部 capabilities；不是明确版本范围与能力交集。入站 connectionHasCapability 检查远端声明，未单独形成“服务器支持且策略允许且远端支持”的会话集合。Packet.Version 未检查；重复 hello 可覆盖记录；Session 无协议状态。

被拒绝的 message/command/item、未知 handler 主要写日志 return，客户端可能保持等待；发送失败也没有返回业务调用方的通用结果。

### P1：重连补投发生在协议 Ready 之前

BuiltInTradeServerExtension 的 OnLogin 立即 HandleUserLoggedIn，尝试发 pending completions；但 Framework hello 在登录之后才发，出站管线要求 isConnectionFrameworkCapable，可能过滤这批通知。handleHello 后没有再次触发 Trade 恢复。

修复：通用 SessionReady/ProtocolReady 事件，在认证+用户+协议确定后触发。插件各自订阅恢复；宿主不写 Trade 专属分支。必须用实际网络顺序测试证明重连补投。

### P1：安全与授权

ClientAuthenticator.generateClientKey 使用 CSPRNG 64 字节，Base64 后放在 Username；服务端忽略 ClientKey.Password 是符合客户端契约的。普通 UsernamePassword 模式则直接字符串比较并以 protobuf 保存原始 Password。

- ServerAuthenticator 的成功/失败日志记录 Username；ClientKey 模式下这就是 bearer 密钥。还会记录 SessionId，应脱敏。用户同步有剥离 Username 的处理，不能声称密钥直接在用户列表公开。
- 所查 NetServer/NetCommon 没有配置加密；发送为模块字符串+载荷，连接标识 `Phinix` 不是安全认证。没有抓包验证，但现有代码不能提供加密保证。
- 未见完整的每 IP/身份限流、注册配额、权限模型、密钥撤销轮换和安全审计。
- 会话绑定 connectionId + sessionId 与到期检查已经有，应保留。

### P1/P2：宿主与运维

- Console.ReadLine 在无 stdin/EOF 时返回 null，while 立即 continue；容器后台运行存在忙循环风险。
- 退出处理覆盖控制台命令与 CancelKeyPress，未见明确 Docker SIGTERM 生命周期；不要假设 SIGTERM 会执行保存。
- shutdown 顺序尚未先停止入站/等待处理结束再保存，最终保存与并发回调仍需审查。
- NetServer 接收 IPEndPoint，但 Start 调的是 server.Start(Endpoint.Port)，Address 是否有效绑定须进一步确认 LiteNetLib 行为。
- NetCommon 持 handlerLock 执行业务 handler；慢业务/保存、未来同步 SQLite 写入会阻塞网络 poll，需缩短锁范围并引入有界处理队列。
- 扩展 Load 失败被日志捕获，模块可能继续提供能力；涉及资产状态应进入失败/只读保护，不能空状态继续确认。

## 5. 推荐架构：沿用 .NET，逐步整顿宿主

保留 LiteNetLib 和现有协议标识作为兼容入口，先不全面换 HTTP/WebSocket。可以引入 .NET Generic Host，统一 DI、配置、结构化日志、BackgroundService、CancellationToken 和优雅停止。诊断/健康检查可以是独立管理端点；管理权限与玩家能力分开。

目标依赖：

```text
UDP transport
  → session/auth/protocol gate
  → Framework adapter 或 Legacy adapter
  → 插件自己的领域 API（Chat / Trade / 其他）
  → 插件持久化 repository + operation result/outbox
  → 对会话协议编码出站结果
```

主宿主提供中立会话、授权、发送结果、存储事务设施；Chat/Trade 继续作为平等插件。没有必要先上 CQRS 框架、微服务、Redis、Kafka 或整套 Web Identity。

## 6. 协议握手与明确拒绝

沿用现有认证/登录时序以保护历史客户端；新增版本范围握手时，需要能用现有 V2 envelope 解析 bootstrap，不能凭空让旧端读未知格式。

建议会话状态：Connected → Authenticating → Authenticated → ProtocolNegotiating → Ready；拒绝/超时进入 Rejected/Closed。旧端是显式 Legacy Ready 分支。客户端 3 秒 Legacy fallback 与服务器分类必须共同制定规则，不能各自猜测。

会话固定：account/user ID、connection ID、session epoch、protocol family、selected wire version、negotiated features/versions、security level、server policy revision、Ready 状态。运行时/.NET/API 版本单独记录，不当作协议级别。

新握手提供 min/max wire version、requested features + feature schema ranges；服务器返回 selectedVersion、negotiated features、限制/拒绝原因与最低要求。能力取客户端支持、服务器实际激活支持、策略允许三者交集。能力不是授权；每个高权限操作还要 RBAC/策略校验。确认一次后不得同连接任意改级别，重新协商需受控新 epoch。

建议通用拒绝消息（名称为设计草案，尚未实现）：

```json
{
  "kind": "operation.rejected",
  "operationId": "原请求 ID",
  "code": "FEATURE_NOT_NEGOTIATED",
  "requiredProtocolVersion": 2,
  "requiredFeatures": ["trade.delivery.ack.v1"],
  "retryable": false,
  "stateChanged": false
}
```

还需 NOT_AUTHENTICATED、NOT_AUTHORIZED、UNSUPPORTED_PROTOCOL、INVALID_PAYLOAD、MISSING_ITEM_REFERENCE、RATE_LIMITED。拒绝本身属于基础控制协议，不应因业务能力缺失又被当前能力出站过滤。未知 peer 上响应要限流，避免放大。`stateChanged=false` 只用于确实没有提交的拒绝；已提交但发送失败不能伪造成安全失败。客户端未知结果继续保留托管，不自动返还/改协议重发。

高级功能门槛按独立 feature 判定，如 request correlation、delivery ACK、status reconciliation、opaque item codec、回复/提及；不能只凭 protocolLevel>=N 就全开放。双方跨协议交易还要计算双方交付能力与物品表示的交集。

## 7. 老 Phinix 服务端 adapter

参考本地 Phinix/Common/Chat/ServerChat.cs 和 Common/Trading/ServerTrading.cs 的真实包形状及字段方向。不要直接把旧 ServerTrading 与新 TradeStore 同时运行成两个账本。

- 入站：旧 protobuf Any/module → 校验身份与 Legacy profile → 翻译成现有业务意图。
- 出站：统一领域结果 → 旧包；保留旧 Token 与请求对应关系，空报价合法，重复/未知 token 不制造成功。
- 同一权威 Chat/Trade store；adapter 不直接裁决物品所有权、不自行退款。
- Legacy 没有新 hello 时，只能确认“连接使用 legacy wire”，无法可靠知道原版客户端 build。可用已知 legacy 包形状锁定 profile；握手超时分类仅在配置允许时发生，非法新握手不得降级成 legacy。
- 可采用同端口模块路由；若安全策略差别大，可给 Legacy 独立端口，明确旧端限制。不能由 adapter 自行穿透现代安全 gate。
- 旧端不认识新通用 error，要用它能解析的旧失败回执；无可表达的响应就事先禁用该路径或拒绝会话。不要承诺任意旧客户端都能显示升级提示。
- opaque 完整物品状态无法被真正旧客户端理解；跨现代/旧端交易应明确拒绝无法无损交付的资产，不能发送哨兵后当作旧端能恢复。现有新客户端向旧服务器的哨兵转发路线不等于旧客户端具备 codec。

建议先做 Legacy chat，再做有限 Legacy trade，再测现代与旧端混合交易。不把历史客户端缺乏 durable ACK 的功能包装成现代可靠交付。

## 8. SQLite：建议引入，先用于资产一致性

单机专用服务器、本地持久卷符合 SQLite 使用方式。建议 Microsoft.Data.Sqlite + 明确 repository，先无需完整 ORM。WAL 支持读写并发但仍只有一个 writer，不用网络文件系统；这些限制依据 [SQLite WAL 文档](https://www.sqlite.org/wal.html)。事务写入与失败重试参考 [Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)。

初始存储边界：identity/credentials、users、trades/offers、operation_results、delivery_outbox/receipts、schema_migrations。Chat 历史按持久性与容量需求单独选；临时会话不因引入数据库就全部永久保存。

核心约束：

- `(principalId, operationId)` 唯一；另保存 request fingerprint，相同 ID 不同请求拒绝。
- `(deliveryId, recipientId)` 唯一；交付与 ACK 身份绑定，重复 ACK 幂等。
- 交易版本做并发控制；更新状态和结果/outbox 在同一数据库事务，不能跨多个插件数据库文件假定全局原子。
- COMMIT 成功后发成功；丢响应重试读取已保存结果，不重新执行。
- 本地磁盘、短写事务、busy timeout、有界 worker；WAL checkpoint/容量告警、正确备份及 schema 迁移。资产强持久确认建议评估 synchronous=FULL 与实际磁盘开销。
- 初步按单进程写入管理，吞吐需实测；多实例跨机写或明确高写入需求才另评 PostgreSQL。

迁移：停写维护窗口 → 备份并校验旧 protobuf/XML → 导入临时新库 → 核对玩家/交易/待交付数量及 payload hash → 完成 schema/version 标记 → 原子切换；旧文件保留只读。坏记录隔离并给明细，不静默丢资产。不要长期让旧文件和 SQLite 双写形成两份权威。

SQLite 只保障服务端本地事务；客户端物品交付仍需自己的持久幂等与恢复协议，不宣称端到端 exactly-once。

## 9. 鉴权升级方案

保留旧 wire bearer 模式为受限制 Legacy security profile；不能直接把 ClientKey.Password 改成必填，否则现有客户端会全部失效。

现代端优先使用成熟安全传输提供服务端身份验证、加密与防篡改，具体 UDP 安全方案需要独立兼容与维护评估，不自造密码协议。身份与秘密分离：稳定 principal ID 不可当登录凭据，设备凭据可轮换/撤销，短时 session 绑定连接及 epoch。若采用公钥 challenge，仍需服务端身份与安全传输，签名本身不加密通信。

- bearer 随机 token 存储 verifier/hash，不明文当 Username 记日志；普通密码使用成熟 PasswordHasher、版本化参数与 rehash 策略。微软对新密码登录推荐 PasswordHasher，见 [官方密码哈希文档](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/password-hashing?view=aspnetcore-10.0)。
- 旧 ClientKey 已可能存在于历史日志；迁移只保存 hash 不能让已泄露 token 自动恢复安全，需撤销/轮换。日志与用户映射迁移分开，避免破坏稳定 UUID。
- IP/账户失败限流，注册配额，握手时限，输入长度与消息预算，登录审计脱敏。
- 玩家、moderator、admin 等权限及插件 operation policy；客户端自报 capability 不授予权限。
- 管理接口用独立高权限凭据与审计；不因引入 SQLite 就解决鉴权。

## 10. 实施次序和验收

| 阶段 | 交付 | 必须验收 |
|---|---|---|
| S0 风险止血 | 整批引用校验、owner scope、日志脱敏、明确发送结果 | 缺引用/重复 ID/非法参与者/全缺引用均无状态改变；日志无合成密钥 |
| S1 会话协议 | profile + version/feature negotiation + structured rejection + Ready event | 不兼容版本明确拒绝；无 hello 不做新业务；重复 hello/重连 epoch/拒绝结果关联；Ready 后补投 |
| S2 事务存储 | Trade operation result/outbox SQLite 切片 + 迁移工具 | 各提交/发送/ACK 时间点强杀与重启；同请求幂等；损坏数据不空状态继续服务 |
| S3 Legacy adapter | Chat + 有限制 Trade；共享领域 store | 老包 fixture/真实老端、新旧混合能力拒绝、token/空报价/全批转换 |
| S4 安全与宿主 | 现代安全传输/凭据、授权、Generic Host、运维 | 日志秘密检查、限流/过期/撤销、SIGTERM、stdin EOF、慢 DB/队列压力 |
| S5 联合基线 | 固定 Common cohort、CI 契约与端到端矩阵 | 干净 clone restore/build/publish/Docker、正式客户端与候选客户端分别联机 |

S0/S1 应先行；安全通信设计可同时开展。Legacy trade 以 S2 的单一权威为基础，别先复制一套旧交易服务。建议先实施这些可审查切片，后续是否替换 LiteNetLib 由安全传输与负载证据决定。

本次是评估与隔离复现，不代表上述升级已实现，也不代表线上服务已通过安全或恢复验收。

实施拆分参见 [服务端升级 RoadMap](Server-Upgrade-Roadmap.md)。本文件是历史评估，不替代 RoadMap 的当前进度。

## 补充审计（2026-10-14）

新增 [静态性能审计](Server-Static-Performance-Audit-2026-10-14.md) 与 [容错和可恢复性评估](Server-Fault-Tolerance-and-Recoverability-2026-10-14.md)，已接入 RoadMap。后续完整性能/恢复承诺须以两份审计的动态矩阵验收。

鉴权范围补正：全量 UserSync 会清空 Username，但 ServerUserManager.broadcastUserUpdate 直接打包原 User，TryLogIn/TryLogOut/UpdateUser 路径仍带旧 ClientKey。存在增量用户包外发秘密的源码路径，列为 P0（FR-14/SV-12）；本轮无线上抓包或利用验证。不能以全量同步剥离字段推导所有用户包安全。
