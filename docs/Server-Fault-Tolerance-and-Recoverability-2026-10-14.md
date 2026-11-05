# 服务端容错与可恢复性评估

日期：2026-10-14。范围：服务端 HEAD `5a0508a` 与固定 Common 的静态故障路径。历史编译及 8 cases/13 assertions 属于管线验证；本轮没有真实网络、磁盘故障、强杀或端到端恢复测试。

**结论：当前有局部异常隔离和 pending completion/ACK 基础，尚不能承诺已确认资产在崩溃后安全恢复。** 捕获异常、进程还活着、可靠有序发送，都不等于业务可恢复。

本文件补充 [现状评估](Server-Assessment-2026-10-14.md) 与 [静态性能审计](Server-Static-Performance-Audit-2026-10-14.md)，实施接入 [RoadMap](Server-Upgrade-Roadmap.md)。统一业务语义和无密码身份约束仍以 [协议与身份文档](Server-Protocol-and-Passwordless-Identity.md) 为准。

## 1. 当前已有保护

- NetCommon 对读取包和 handler 异常有 catch；PipelineRunner 隔离多数处理器、观察器、item predicate 和 outbound predicate 的异常。
- NetServer.TrySend 返回 bool 并记录发送异常；鉴权/登录的若干关键回执发送失败会断开连接。
- 会话认证校验 connectionId + sessionId + expiry；用户入口校验连接与 UUID。
- Trade 有整份状态保存、pending completion、重登录补投、completion ACK 和局部 store lock。
- Auth timer/NetServer 具备 Stop/Dispose 路径；旧 Thread.Abort 已移除。

这些是可继续利用的基础。部分保护会被日志失败、时序或资源问题打断，不能宣称全部边界已闭合。

## 2. 故障清单与影响

| ID | 优先级 | 源码依据 | 确定事实、风险与恢复缺口 | RoadMap |
|---|---|---|---|---|
| FR-01 | P0 | `PhinixFrameworkTradeServerService.cs:183`、`:205`；`PhinixFrameworkTradeStore.cs:50` | 缺引用只日志并接受部分报价；解析即消费后才校验交易。此前真实 DLL 合成探针已复现；失败不能可靠恢复原完整请求 | SV-10 / SV-11 |
| FR-02 | P0 | `Server/Config.cs:54`；`Server/Server.cs:149`；`PhinixFrameworkTradeStore.cs:166` | 内存更新与成功回执没有持久 COMMIT；定时文件保存。强杀可丢已确认状态，具体端到端资产后果未实机复现 | SV-31 / SV-35 |
| FR-03 | P0 | `PhinixFrameworkTradeStore.cs:95`、`:113`、`:122`；`PhinixFrameworkTradeServerService.cs:271` | active trade 删除与 pending completion 创建是两个 store 操作，之间可被保存抓到；ACK 移除 pending 也未立即持久提交 | SV-32 / SV-35 |
| FR-04 | P1 | `ServerAuthenticator.cs:257`；`ServerUserManager.cs:222`；`PhinixFrameworkTradeStore.cs:166`；`PhinixFrameworkChatService.cs:218`；`Config.cs:165` | 直接 truncate/覆盖，无临时完整文件、原子替换或一组状态的 generation commit；中断可留下残缺文件，各模块时点不同 | SV-17 / SV-30 / SV-34 / SV-38 |
| FR-05 | P1 | `Server/Server.cs:82`、`:149`、`:174` | AutoReset timer 无排他/在途排空；先存 credentials 再 users 再 extensions，前项失败会跳过后续；Stop 不证明回调已经结束 | SV-17 / SV-38 |
| FR-06 | P1 | `PhinixFrameworkServer.cs:35`、`:69`、`:97` | Activate/收集能力先于 Load；资产 Load 失败只捕获日志，未见切只读/撤回能力；Save 失败也没有 durable health/result 反馈 | SV-15 / SV-38 |
| FR-07 | P1 | `Server/Connections.Server/NetServer.cs:111`、`:126`、`:250`；`Server/Logger.cs:55` | poll thread 顶层没有保护，连接事件及日志 I/O 可抛；异常 catch 内日志再抛可能逃出包隔离；Stop Join(1000) 不检查成功，Dispose 设 server=null | SV-14 / SV-16 / SV-18 |
| FR-08 | P1 | `ServerPipelineRunner.cs:64`、`:106`、`:161`、`:203` | message/command 的 Can* predicate 在 LINQ Where 中、处理 try 外；一项 throw 会终止本次管线。handler throw 后继续下一候选，若已副作用会形成重复执行风险 | SV-28 |
| FR-09 | P1 | `BuiltInTradeServerExtension.cs:48`；`PhinixFrameworkServer.cs:184`；`ServerPipelineRunner.cs:574` | pending 在 OnLogin 尝试发；此时尚无 Framework Ready，会被出站能力筛选；hello 完成未再触发恢复 | SV-23 / SV-33 |
| FR-10 | P1 | `ServerAuthenticator.cs:649`；`PhinixFrameworkServer.cs:444` | session cleanup 只移除 sessions，未直接清理 peer/user/capabilities；入站鉴权会失败，但出站广播只按登录连接/能力筛选，存在过期主体仍订阅的边界 | SV-25 |
| FR-11 | P1 | `ServerUserManager.cs:315`、`:108` | 断线处理考虑同 UUID 多连接，但 TryGetConnection 用 Single，多个匹配直接失败；Trade 依赖该函数可能跳过快照/交付 | SV-27 / SV-33 |
| FR-12 | P1 | `PhinixFrameworkTradeStore.cs:147`；`PhinixFrameworkTradeServerService.cs:115` | 无匹配 pending 也返回 ACK accepted=true，无法区分已持久完成与从未有交付；没有 stable deliveryId/receipt 证据 | SV-32 / SV-35 |
| FR-13 | P1 | `Server/Config.cs:185`、`:208`；`ChatServerConfig.cs:32`；`NetServer.cs:106` | 通用范围检查在 OnDeserializing，成员反序列化后没有同等 Validate；聊天容量无正向上限；server.Start 结果未检查，配置地址未传入该调用 | SV-29 |
| FR-14 | P0 | `ServerUserManager.cs:153`、`:197`、`:572`、`:603`；Common `UserManager.cs:74` | user.Username 存旧 ClientKey；全量 Sync clone 后清空，但增量 broadcastUserUpdate 直接打包原 User，存在 bearer 凭据外发源码路径 | SV-12 / SV-43 / SV-44 |

上表简称文件：ServerAuthenticator 在 `Server/Authentication/`，ServerUserManager 在 `Server/UserManagement/`，Config/Logger/Server 在 `Server/`，PhinixFrameworkServer 在 `Server/Framework/`，ServerPipelineRunner 在 `Server/ServerRuntime/`，Trade 文件在 `Extensions/Trade/Server/`，Chat 文件在 `Extensions/Chat/Server/`，Common 在 `Dependencies/Phinix.Common/Common/`。行号对应本轮源码，以函数名确认未来位置。

**FR-14 修正此前鉴权风险范围：** “全量用户同步会剥离 Username”仍正确，但不能代表所有用户广播均安全。TryLogIn/TryLogOut/UpdateUser 将带 Username 的 User 交给增量广播，packet 构造没有剥离它。此项来自源码，没有抓取线上流量，也不声称历史环境一定已被利用。需要先统一 public user projection，并评估原凭据轮换；仅关闭日志不能覆盖外发风险。

## 3. 不同异常需要不同结果

| 故障类型 | 允许行为 | 禁止行为 |
|---|---|---|
| 非权威 observer / 诊断失败 | 隔离并记录，其他业务继续 | observer 被错误当成授权/提交必需步骤 |
| 认证/授权/整批校验失败或异常 | gate 拒绝；证据足够时标记未改变 | 安全拦截异常继续默认业务，Legacy 绕过 gate |
| 业务执行未提交 | 原子回滚，返回确定失败 | 已消耗引用/改报价后还声称完全未执行 |
| 已提交但回执发送失败 | 保存原结果，可按相同 operation 查询/补投 | 返回“未提交”，客户端自动退款或不同 ID 重试 |
| 业务副作用阶段未知 | 保留未知状态，核对同 operation 与事务证据 | 接着执行另一个默认 handler 或盲重试 |
| Asset Load/DB integrity 失败 | 资产组件进入可观察不可写状态，保留文件，明确拒绝；无关服务可继续 | 空状态继续确认、覆盖坏文件、把健康状态写成 Ready |
| 日志 sink 失败 | 非递归 fallback/计数/告警，与领域 durable 结果分开 | 日志异常再次日志形成逃逸；把日志成功当数据库提交 |

不要把所有插件异常一律改成拒绝；保留非权威 observer 的既有隔离测试。安全与提交 gate 必须是明确职责，generic handler 也不能凭 catch 推断没有副作用。这属于统一容错语义，不是兼容例外。

## 4. 定时保存与停机的失败链

源码存在保存与 shutdown 竞态：Stop/Dispose timer 后仍可能有在途回调；shutdown 先停扩展/保存，再 logout/Stop transport，业务收包尚未明确停止并排空。Chat Save 常规路径锁外写文件，多个保存者可能写同一路径；Config Save 没有文件级排他。Auth/User 的锁虽串行各自 Save，不保证跨模块一致。

.NET 官方文档说明 Timer 的同步 Elapsed 异常可能被抑制、回调可并发或在 Stop 后发生，见 [System.Timers.Timer](https://learn.microsoft.com/en-us/dotnet/api/system.timers.timer?view=net-10.0)。本项目同步 saveHandler 不能依赖运行时保证“失败会醒目暴露”。此外未处理的 managed thread 异常通常可终止应用，见 [Managed threads exceptions](https://learn.microsoft.com/en-us/dotnet/standard/threading/exceptions-in-managed-threads)。本轮未执行故障注入，实际终止行为待测。

目标顺序：停止接收新工作 → 标记 draining/拒绝新增 → 等待已接纳操作达确定事务边界 → 停止并等待后台保存/恢复 worker → 保存必要非事务状态 → shutdown 插件 → 停传输并确认 worker 已结束 → 释放资源。超时终止留下可恢复记录，不宣称全部操作完成；SIGKILL 不走优雅路径，仍依靠已提交状态。

## 5. 恢复状态机与幂等证据

操作结果至少区分：未接纳、已接纳未提交、已提交、响应待投递/已投递；客户端交付另有未交付/已持久交付/ACK 待确认。Ready 与 Completed 不混用。

- 稳定 `(principalId, operationId)` + request fingerprint：相同请求重试读取原结果，同 ID 不同内容拒绝。
- 稳定 deliveryId + recipient：outbox 记录提交先于投递，客户端持久去重先于 ACK，服务端持久 receipt 证明 ACK 终态。
- pending 消失本身不能证明已完成，FR-12 必须用 durable receipt/既有操作结果区分未知 ACK。
- 接入层将所有协议归一到同一结果，旧端缺证据就明确拒绝相关功能；不能合成 delivery ACK。
- Ready 后分页补投，失败保留；队列满/资源限制拒绝新增，不能删除未决资产。
- 会话到期/撤销立即结束有效订阅与授权，用户 presence/多连接投递策略保持一致。

## 6. RPO、RTO 与数据损坏

**当前 RPO 不能写成“最多丢 60 秒”。** 60 秒只是定时计划，保存可失败/重叠，各文件非同一事务，已确认结果还可能早于存盘。正确起点是最后一次成功且一致的持久证据；当前没有这个跨模块一致点保证。RTO 未测量。

目标为：在经验证的磁盘/SQLite durability 配置下，已返回成功的资产事务 RPO=0；不把硬件故障、丢失持久卷或不一致备份包含在这个保证内。客户端持久托管/交付未完成则仍不能宣称端到端 RPO=0。

RTO 由 P0 定预算、P6 实测：数据库验证/迁移、启动、有效 SessionReady、积压补投与查询恢复分别计时；“进程监听端口”不代表业务恢复完毕。

处理规则：区分首次创建与预期文件缺失；关键资产数据缺失/损坏先保护和诊断，不自动创建空账本覆盖原始证据。损坏文件只读隔离；已有正确备份必须验证 generation/identity/asset hashes 与迁移版本。实际强杀/盘满/断电恢复演练后再提出运行承诺。

## 7. 必须补的故障注入矩阵

| 场景 | 检查判据 | 任务 |
|---|---|---|
| 缺引用、重复/跨 owner、无权报价 | 整批未改变且原引用仍可核对 | SV-02 / SV-10 / SV-11 |
| 更新提交前/后强杀，回执丢失 | 同 ID 核对原结果，不提前成功/复制返还 | SV-31 / SV-35 |
| active 移除/outbox 创建、ACK 删除强杀 | 资产始终有单一 durable owner 与交付证据 | SV-32 / SV-35 |
| 文件截断、坏 checksum/schema、缺失持久卷 | 保留原文件；资产不写；恢复动作可解释 | SV-34 / SV-38 |
| 慢盘/盘满、timer 重叠、停机同时保存 | 不重入同文件；失败可观测；不漏后续关键状态 | SV-17 / SV-38 |
| 日志文件不可写、异常记录再抛 | 单个 sink 故障不逃逸 poll，fallback 不递归 | SV-16 / SV-18 |
| predicate throw、handler 副作用后 throw | 非权威隔离；授权不放行；未知副作用不再执行下一业务 | SV-28 |
| session 过期/撤销、多连接、重连 Ready | 不再接受/广播给无效主体；投递策略确定且幂等 | SV-25 / SV-27 / SV-33 |
| 增量 UserUpdate 与全量 UserSync | 两者都不包含合成 credential；原身份绑定保留 | SV-12 / SV-43 |
| 工坊更新/迁移半途重启、旧凭据退役 | 新 secret 已保留、原 UUID 不变，不偷偷建新身份 | SV-44 / SV-45 / SV-71 |

通过静态审查只完成评估；本表运行结果待实施补充。RoadMap 发布门槛应同时引用本表和静态性能审计的动态负载矩阵。
