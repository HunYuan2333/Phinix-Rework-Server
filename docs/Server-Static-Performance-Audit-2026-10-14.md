# 服务端静态性能审计

日期：2026-10-14。范围：Phinix-Rework-Server HEAD `5a0508a` 与固定 Common `67f243d9edced77dc3fe669f8a435b1f14b59199` 的源码。产品源码未修改；已有文档改动不影响所查执行路径。

**结论：先处理同步网络路径、锁内 I/O、资源预算与历史发送，再做局部优化。** 当前没有压测、CPU/GC trace 或延迟测量，无法给出支持人数、吞吐或 p99。默认 MaxConnections=1000 是配置值，不是容量验证。

本审计补充 [现状评估](Server-Assessment-2026-10-14.md)，实施接入 [RoadMap](Server-Upgrade-Roadmap.md)。性能优化必须遵守 [协议与身份边界](Server-Protocol-and-Passwordless-Identity.md)：Legacy 妥协只在 adapter，不能丢资产、提前确认或减少校验来提高吞吐。

## 1. 方法与已知条件

静态核对线程、锁、发送/序列化、快照复制、集合查找、持久化、日志及预算。下表“已确认”表示代码路径存在，实际延迟/内存影响仍需测量。“待测”表示可能风险，不宣称已经出现线上瓶颈。

- 单一 Thread 调用 PollEvents，再 Sleep(10)；业务 handler 同步在收包回调执行。
- AutoReset 保存 timer 默认 60 秒；会话清理 timer 10 秒；存在独立线程与 timer 并发。
- Chat 历史默认 40 条，Notice 默认 50 条；配置没有可见正向上限。
- Trade item cache 限 200 项，但没有总字节/每 owner/TTL 预算；活跃交易及 pending completion 没有可见容量/存活期限制。
- 本轮未启动真实服务器、修改限额或运行压测。此前编译/管线回归不视作性能基准。

## 2. 热路径与优先级

| ID | 优先级 | 源码依据（相对仓库） | 已确认路径与影响 | 建议与 RoadMap |
|---|---|---|---|---|
| PF-01 | P1 | `Server/Connections.Server/NetServer.cs:111`；`Dependencies/Phinix.Common/Common/Connections/NetCommon.cs:118` | 单 poll thread 执行业务；handlerLock 覆盖 handler.Invoke。慢插件/日志/锁等待影响其他连接和模块 | 锁内仅取得 handler，锁外执行；有界调度、保持会话/操作顺序，SV-15 / SV-18 |
| PF-02 | P1 | `Server/Logger.cs:55`、`:71` | 每条消息格式化；过滤前已构造文本；Console.Write 与锁内 File.AppendAllText 同步执行。DEBUG 字符串可能在调用前已构造 | 有界批量日志与异常 fallback，避免 DEBUG 无用开销；日志不能代替资产日志，SV-16 |
| PF-03 | P1 | `Extensions/Trade/Server/PhinixFrameworkTradeStore.cs:166`；`Server/Authentication/ServerAuthenticator.cs:257`；`Server/UserManagement/ServerUserManager.cs:222` | 持业务全局锁序列化及全量写文件，时间随状态量/磁盘增长。Chat Save 常规路径先 clone 再锁外 I/O，不能一概写成所有插件锁内 I/O | 事务增量持久化；过渡文件写入单 owner 与一致快照，SV-17 / SV-30～35 |
| PF-04 | P1 | `Extensions/Chat/Server/BuiltInChatServerExtension.cs:111`；`PhinixFrameworkChatService.cs:143` | 一次请求 clone 全历史并连续发送所有历史/notice，末尾立即发送完成标记；没有分页/每 poll 配额 | 批次、字节预算、背压与真实完成边界；默认条数小不等于任意配置安全，SV-19 / SV-66 |
| PF-05 | P1 | `Server/Framework/PhinixFrameworkServer.cs:444`、`:476` | 每广播取连接快照与过滤；每接收者重复 SerializePacket、分配 writer；发送受同一 sendLock 串行化 | 测量 N 接收者 × B 字节的分配/CPU；仅最终 envelope 相同且 interceptor 无个性化时复用编码，不绕过插件，SV-26 / SV-66 |
| PF-06 | P1 | `Server/UserManagement/ServerUserManager.cs:572`、`:603`；`Server/Config.cs:42` | 用户更新在 connectedUsersLock 内循环发送；TryLogIn/Out 还可能持 userStoreLock。每登录 full sync clone 全部注册用户，成本随 U 与连接数 N 增长 | 分离脱敏 public DTO/连接快照，锁外发送；目录同步预算与身份注册配额，SV-12 / SV-18 / SV-19 |
| PF-07 | P1 | `Extensions/Trade/Server/PhinixFrameworkTradeServerService.cs:42`；`PhinixFrameworkTradeStore.cs:14` | cache 仅按数量限制，单项 byte[] 可很大；active/pending 随未完成交易/未 ACK 增长 | 包/codec/metadata/整批字节、每主体/全局预算；pending 持久分页，不直接淘汰资产，SV-11 / SV-36 |
| PF-08 | P2 | `Extensions/Trade/Server/PhinixFrameworkTradeStore.cs:355`、`:365`、`:417` | GetItemsFor 先 CloneItems；ToSnapshot 又 CloneItems(GetItemsFor)。PayloadBytes 被多次复制；保存、广播、pending clone 增加分配 | 在不可变/所有权保证下消除确切重复 clone；测量大 payload 与 LOH/GC，不共享可变数组，SV-37 |
| PF-09 | P2 | `Dependencies/Phinix.Common/Common/UserManagement/UserManager.cs:41`；`Server/Authentication/ServerAuthenticator.cs:138`；`Server/UserManagement/ServerUserManager.cs:315` | username/sessionId/uuid 反向查找使用 Single 扫描；Trade 按 participant 扫描 active，pending ACK/查询扫描 List | 依据负载建立一致索引，保留多连接语义与冲突检查；SQLite index/query plan，SV-27 / SV-37 |
| PF-10 | P2 | `Server/Framework/PhinixFrameworkServer.cs:306`、`:396`；`Server/ServerRuntime/ServerPipelineRunner.cs:64` | 每请求远端能力排序+ToArray、服务器能力 ToArray、context/delegate 分配，按 handlers 线性探测 | 会话/激活版本固定不可变能力快照；仅测得热点再缓存分派，SV-26 / SV-66 |
| PF-11 | P2 | `Extensions/Chat/Server/PhinixFrameworkChatService.cs:125`、`:192`；`ChatServerConfig.cs:32` | 有界 List 满时 RemoveAt(0) 平移；Load 不按容量立即裁剪。默认 40/50 小，不能当作首要性能瓶颈 | 高配置有证据再选环形缓冲；校验配置与加载后容量，SV-19 / SV-67 |
| PF-12 | P1 | `Server/Server.cs:108`；`Server/Connections.Server/NetServer.cs:116`；`Server/Server.cs:82` | stdin EOF 紧循环；固定 Sleep(10) 带调度间隔但不能据此算出实际 RTT；AutoReset 保存可能重叠 | EOF/生命周期已在 SV-14；保存排他 SV-17；轮询策略需 trace 后选择，SV-66 |

优先级反映修复顺序，不代表已测得的瓶颈比例。PF-07 的输入预算属于边界防御，应先建立；PF-08～11 的局部优化不能在没有测量时扩大成全面重写。

## 3. 序列化与网络成本

Framework 的外层 SerializePacket 使用 DataContractJsonSerializer；部分内层 protobuf payload 再作为 byte[] 包在 JSON 中，另有 PayloadJson 字符串。不能据 `.proto` 文件存在就认为整条链路是直接 protobuf。

`Dependencies/Phinix.Common/Common/Utils/Framework/FrameworkSerialization.cs:173` 缓存 serializer 实例，但每次仍创建 MemoryStream 与 ToArray；item state 在快照中深复制。载荷编码膨胀、复制次数、每消息分配与峰值内存都需按真实大小测量；不编造包大小上限。

若以后并发 worker 复用 serializer，当前只保护缓存字典的锁不能证明实例并发安全。实施前核对所用类型与实际 runtime 行为，必要时隔离实例；本轮没有确认并发序列化 bug。

优化原则：先统一数据所有权与字节预算，再复用最终不可变编码；按 peer 改 session/envelope 或 interceptor 改 packet 时重新编码。不跳过认证、能力、授权或其他插件的出站链。

## 4. 锁与资源关系

| 锁/资源 | 所覆盖操作 | 优先收缩边界 |
|---|---|---|
| handlerLock | 注册查找与完整业务处理 | 查找/生命周期所有权；锁外 handler |
| userStoreLock / connectedUsersLock | 用户查询/登录，部分广播与全量保存 | 数据快照/索引更新；锁外序列化发送及日志 |
| sessionsLock / credentialStoreLock | 鉴权、部分响应与凭据文件写入 | 短状态转换；持久事务与出站分别执行 |
| Trade syncLock | 全交易 state、snapshot clone、XML I/O | 同事务业务原子性优先；限制长时间复制/全文件 I/O |
| sendLock | 全 peer Send 调用 | 先测发送序列化与调度，不随意并发调用旧库 |
| writeLock | 每条日志打开/追加文件 | 有界 sink/batch，明确满队列和 I/O 失败策略 |

未据此断言死锁已经发生。缩锁或引入 worker 后必须补并发一致性、取消与 epoch 测试，不能把代码顺序的偶然串行丢掉。

## 5. 动态验证计划与发布门槛

SV-05 在 P0 确定测试机/磁盘/runtime/版本、合理工作集、目标活跃连接数、包和交易大小、负载分布及可接受预算。本次不指定未经测量的支持人数。

| 场景 | 采集指标 | 验收关系 |
|---|---|---|
| idle / stdin EOF / 单连接 | CPU、poll interval、线程活性、空闲分配 | 无忙循环，基础基线可复现 |
| N 连接聊天、同时历史/用户同步 | p50/p95/p99、每 poll 工作量、send 队列/字节、分配 | 预算内同步不长期阻塞其他操作 |
| 大物品/并发报价/重连补投 | heap/LOH、GC pause、锁等待、outbox backlog | 完整性不变，内存有界，恢复有进展 |
| 慢磁盘、日志洪峰、保存/写库 | commit latency、poll delay、日志 drop/失败、writer 队列 | 不提前发成功，不静默丢资产；队列有界 |
| 超预算/异常插件 | 拒绝数、CPU、活跃会话、故障恢复进度 | 单个主体不能耗尽全局；策略拒绝可解释 |

SV-66 做联合 trace/压力；SV-67 对照 SV-05 固定预算决定发布，不以平均延迟代替尾延迟和恢复指标。连续失去 writer/poll 活性、无界队列、不可解释 asset 丢失都不能发布。压测结果在实施时补入，静态审计不自动标记为“性能通过”。

## 6. 性能与容错的共同约束

不允许为速度跳过 COMMIT、减少整批校验、批次部分成功、丢 pending completion、异步排队后伪造 success，或无限重试掩盖失败。持久资产 outbox 的积压通过拒绝新增/分页恢复/容量管理处理；只允许对非权威日志采用明确可观测的丢弃策略。

相关故障状态与恢复判据见 [容错与可恢复性评估](Server-Fault-Tolerance-and-Recoverability-2026-10-14.md)。
