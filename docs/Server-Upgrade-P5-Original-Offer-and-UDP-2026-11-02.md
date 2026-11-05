# P5 / SV-52：原版报价转换与 UDP 断线竞态（2026-11-02）

## 本轮范围

Server dev `5a0508acb703319e60dd5600e3e0facd0397b294` 加既有未提交修改。继续 P5，完成原版 ProtoThing、报价 token/批次转换到现有 P3 服务和真实 SQLite 账本的组件切片，并修复阶段检查实际暴露的 UDP 后端竞态。原版报价 bridge 尚未注册公开 `Trading` 模块，不宣称完整原版交易可用。客户端业务线、工坊/UI 线、本地原版 clone 均未修改；Common/protobuf gitlink、vendor DLL、SDK pin 未变，没有新增 NuGet 依赖。TLS/现代身份切换仍 TODO。

关联 SV-50/52 子集、SV-54 边界、TQ-30/50、FR-01/09/12/14。整批拒绝、主体绑定、稳定 operation/fingerprint、同事务状态/结果、提交后才报成功及 Unknown 不退款保持。

## 物品与报价实现

- `OriginalTradeItemCodec` 按 thomotron/Phinix `ce82b8f` 的 Thing/Quality proto 做严格无损映射到 `core.item.vanilla`。原版品质 Awful=0…None=7，新版 Unspecified=0、Awful=1…None=8，不能直接强转。嵌套物品、材料、数量、耐久与原列表顺序/重复堆均保留。
- 根/子项值、重复 scalar、未知字段、非法品质、空/负数量、嵌套深度（16）及尺寸（1 MiB）明确拒绝。新版未知 codec、额外 metadata、JSON/bytes 混用或 unspecified 品质不能悄悄降成普通物品；无法无损表示则整批拒绝。
- `OriginalTradeOfferCodec` 读取原版 UpdateTradeItemsPacket 的 session/UUID/trade/token/items，拒绝把出站 OtherPartyItems 字段当作客户端权威。至多200根项、整包1 MiB，完整验证与回执表达/尺寸预检查在业务调用之前完成。
- 非空稳定 token + 主体 + tradeId 经带长度边界的 SHA256 形成 operationId，不把 session/凭据写入持久 ID。重连/重启复用同一 operation；指纹由同一 P3 服务计算，token 相同、报价不同明确冲突。原版 API 的空 token 本轮不能安全识别重放，明确拒绝。
- `OriginalTradeOfferBridge` 校验中立主体授权及 Framework Ready 选择，调用相同 `IFrameworkTradeServerApi`，不直接持有 store/DB、独立事务或退款分支。只有真实服务返回的报价结果才能编码旧回执；其他状态输出交给调用者提供的显式路由，不在 bridge 静默吞掉。
- IO/提交不确定/发送异常不转换成“报价失败”。特别区分业务执行前的 OperationConflict 与回执 callback 抛出的同类型异常，防止已经提交却又发失败包导致旧端返物。后者保留原结果，重复请求查询/复用。

持久性前提是实际宿主已完成既有 Trade LoadDurable；隔离检查使用该真实持久服务，未以 mock 成功代替事务。Bridge 是当前内部转换子集，创建、登录快照、收件主体映射、取消及 Pending/Unknown 交付路由尚未串成公开旧端入口。

## 从固定原版源码确认的两个限制

1. `Common/Trading/Packets/UpdateTradeStatusPacket.proto` 只有 Accepted/OtherPartyAccepted/Cancelled，没有报价版本。`TradeWindow` 的确认动作也没有完整对方报价指纹。不能把“最后发过快照”伪装成客户端“已观察该版报价”。本轮没有给旧包补字段、版本或 ACK；回归要求无版本确认继续被现有核心拒绝，不改变报价或同意状态。
2. 原版 `Client/Source/GUI/Windows and panels/TradeWindow.cs:283` 先 DropPods 返还旧报价物品，`:293` 才发送不带 token 的空报价。服务端不能通过拒绝该请求撤销已发生的本地返物。本轮不把这种客户端流程包装成满足资产保证，也不因此放宽核心确认、幂等或交付标准。

因此原版交易公共入口继续不注册。原版普通聊天仍是显式开启的独立支持子集；完整原版资产流程需要能满足上述约束的客户端/协议能力或明确拒绝范围。原版尽力交付的既有政策只处理离线 Pending/可能发送后的 Unknown，不自动豁免客户端返物和报价观察问题。

## 阶段实际失败与 UDP 修复

首次49步阶段检查，报价66项和 P3 业务/恢复探针均已运行，但 recovered 阶段服务端随后崩溃：`NullReferenceException → LiteNetLib.NetPeerCollection.RemoveAt → NetManager.RemovePeerAt → UpdateLogic`，外层因服务端退出报 BrokenPipeError。该轮整体明确失败，未因业务断言通过而算成功。

对固定 netstandard2.0 LiteNetLib（SHA256 `320526331c04be0218341e68412b51df5752aa5381842c7c9d26e414c217eca0`）检查实际 IL：UpdateLogic 在 `_peers` monitor 内读取/移除；DataReceived 命中断开包时，读取 peer 后已释放同一 monitor，再 SetDisconnectedState/Remove。存在可与索引遍历交错的无锁删除路径，与观察堆栈一致。不声称由日志还原了实际全部线程调度，也不宣称后端所有竞态均已审计。

`PinnedUdpPeerSynchronization` 在服务端包装层、socket/logic 线程启动前，校验确切 DLL hash/字段/回调所有者，再将 socket 回调包装在后端既有 `_peers` monitor 内。`NetServer` 的对外 Send/Disconnect 也共用该 gate；保留 queued callbacks，使业务仍在 poll worker 执行。反射仅启动时使用；每包调用已绑定强类型 delegate，没有 DynamicInvoke/日志格式化/重试。Stop 不在该 gate 内等待后端线程，避免 Join 锁倒置。没有修改 vendored 文件、wire 或 SDK pin，没有吞掉异常继续使用损坏连接集合。

这是窄范围 private-API 兼容 shim：不同 DLL/布局明确启动失败，不能静默省略同步；未来更换 LiteNetLib 必须同时复核或退役 shim。当前只验证标准 DLL 布署、Linux .NET10，未验证 native Windows、AOT/裁剪或单文件发布（无 Assembly.Location 时明确不支持）。此修复不修改用户数据，也不激活 TLS。

新增6项确定性回归检查安装目标与实际 peer gate、queued callbacks、held logic/receive barrier 和回调执行时确实持有锁；在独立源码副本中删去这一个锁，测试精确失败于 `Receive callback ran outside peer mutation gate`。不是无限重跑等待偶然通过。生产没有远程 fault 开关。

## 实际检查

- 新增 `Tests/ServerOriginalTradeAdapterTests`：**66项通过**，真实序列化器、真实 P3 服务、真实 SQLite 文件。外部身份/输出端口仅隔离测试环境，不宣称这组完成真实认证或联机验收。
- 覆盖全部8种品质、嵌套、未知 codec/字段/metadata、重复 scalar 与深度/数量、固定原版 golden 的三条请求实际物品 bytes、顺序和重复堆。Golden hash 核对，不能空循环冒充通过。
- 真实账本检查：成功回执 callback 用独立 DB 连接确认 operation 已可见；重开服务/换 session 同 token 复用原结果；冲突、非法尾项、空 token、跨 owner/错误 session、Ready 切协议拒绝而报价不变；无版本确认不放行；空报价在带有效 token 时保留真实清空含义。
- 提交后 callback 丢响应（受控异常）重开后复用原结果；SQLite 真实 trigger 在 operations INSERT 前 abort，要求没有成功/失败回执、没有部分报价，独立 SQL 中 operation 不存在、ledger blob hash 原样，重开后仍一致。区分受控异常与强杀，不把它写成系统断电测试。
- 修复后完整 stage **49步 / 62发布文件通过**：报价66、原版聊天组件38、UDP同步6、既有安全/模型/架构/SQLite及P3独立进程检查。P3两次强杀和 recovered 正常退出均通过，保留失败候选记录。架构八项旧结果归一化债务仍未清零。
- 对最终实际发布目录，再执行固定上游真实 Mono 客户端 → Server Main 的原版普通聊天 **21+11=32项**，覆盖双向聊天、拒绝、重启后身份/历史，全部通过。这32项验证 UDP 修复后的聊天兼容，不是新报价 bridge 的 socket 或完整原版交易证明。
- 两仓库/子模块的固定状态核对，Server CRLF-aware diff check；未改客户端，不为本次服务端切片重复双版游戏构建。

实际命令入口：

```sh
python3 tools/quality/check.py --suite stage --isolated --output <isolated> --timeout 120
# 阶段入口会构建并执行：
dotnet Tests/ServerOriginalTradeAdapterTests/bin/Release/net10.0/ServerOriginalTradeAdapterTests.dll
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --pinned-udp-synchronization
# 最终发布目录按既有规则补齐 Extensions，再运行：
python3 tools/quality/original_chat_acceptance.py --original-source <fixed-clone> --pastel-package <fixed-nupkg> --framework-root <net472-pack/build> --server <publish>/PhinixServer.dll --config-writer <ServerLegacyChatTests.dll> --output <isolated>
```

SDK10.0.114 / runtime10.0.14；固定 stage seed 及具体命令/DLL/fixture/source hash 保存于工作区 `Deliverables/Phinix-Rework-P5-Original-Offer-UDP-2026-11-02`。新报价受控故障 ID：`original.offer.sqlite-before-operation-insert`、`original.offer.after-commit-before-response`；新增 UDP barrier：`held logic`、`receive`。未运行覆盖率、新原版资产 socket/游戏物化/存读档、Windows/Waylith、云端 runtime10.0.13 或完整压力。

开发中第一次报价故障测试误期望底层 SqliteException，实际持久层按既有契约包装为 IOException；修正异常类型后，仍独立检查回滚、无回执和持久状态。首次阶段失败的 UDP 崩溃如上记录，最终只在代码修复和负对照后重跑通过，不抹去失败。没有发布或部署本轮候选。

## 下一依赖

原版 Trading 公开接入前仍需创建请求无 operationId 的关联策略、登录/快照路由、不可表达操作的明确拒绝、取消与双方交付证据以及完整新旧混合验证。已确认的原版确认/Reset 限制不通过默认值填补。P5可并行顺序推进 SV-54 宿主八项旧结果归一化债务，避免现代 UDP 主线被不满足声明范围的旧客户端资产能力拖住。P5整体保持未完成。
