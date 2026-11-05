# 客户端兼容边界与故障恢复约束

> **2026-11-04 聊天兼容修复：** 原版无法承载的回复/艾特展示上下文在 Adapter 内转为带标记的普通文本，核心消息/作者/ID/时间保留；不伪造旧端的跳转或提醒能力。历史加载失败不再阻断独立新消息的真实回执/live 广播，也不伪造空历史。此前关于所有结构化聊天一律断开/拒绝的记录是历史行为，详见 [热修复记录](Server-P5-Legacy-Chat-Hotfix-2026-11-04.md)。

> **2026-11-04 实施更新：** 显式启用原版 Trading 后，网络入口与统一账本已接通；登录仍为未分类状态，真实已认证原版 Trading 请求才确认旧协议。首次可由原版发起交易，确认识别后双方均可发起。未分类会话不提前尝试有物品的 Pending；原版无 ACK 的 Unknown 不自动重投或退款。普通 Rework 双方不降级。参见 [最新网络实施记录](Server-Upgrade-P5-Original-Trade-Network-2026-11-04.md)，后续“公开入口未接通”文字是 11-03 切片历史状态。


> **2026-11-03 用户授权的原版交易例外（优先于下文历史无损/拒绝要求）：** 含原版对端的交易允许 Adapter 丢弃原版无法表达的附加属性，使用基础物品表示及当前报价确认。普通 Rework 双方交易不得因此降级。领域只保存通用交易条款，不判断 Legacy/V2；没有单独的原版账本，也不补造报价已读版本或交付 ACK。无法取得真实物品的基本种类/数量等数据时不能伪造物品或过滤整件资产。实现分切片推进，当前公开原版 Trading 路由尚未接通，详见 [实施记录](Server-Upgrade-P5-Basic-Trade-Terms-2026-11-03.md)。


本文件约束 Rework 客户端连接原版服务端及 Framework 服务端时的实现。原版协议以同级 `Phinix/Common/Trading/ClientTrading.cs` 和 `ServerTrading.cs` 为本轮核对依据；其他服务端变种须提供独立证据，不得以猜测改变通用业务语义。

## 服务端边界补充（2026-10-14）

本文件上方及后续历史验收记录主要针对客户端。服务端新增协议、身份和 adapter 的约束以 [服务端协议边界与无密码身份升级评估](Server-Protocol-and-Passwordless-Identity.md) 为准；该文档是待实施方案，不表示功能已完成。

**所有旧协议兼容性妥协局限于 Legacy adapter；adapter 只充当翻译官。** 旧包、字段缺省、token、物品格式与旧凭据语义只能在此归一化。新业务层不判断 Legacy/V2，不降低整批原子性、权威确认、持久提交或幂等交付标准；所有协议使用同一业务服务与权威存储。

Adapter 无法无损翻译、准确关联回执或满足功能要求时，必须明确拒绝。禁止伪造 ACK、部分成功、静默过滤、自行提交/退款或失败后切协议重试。通用会话可持有所选协议，业务只接收统一意图、稳定身份与授权事实。旧弱登录映射亦属于 adapter，不能让现代身份验证接受旧格式例外。

## 职责边界

| 层 | 负责 | 不应负责 |
| --- | --- | --- |
| Legacy 协议 adapter | 旧模块与包类型、字段方向、缺省 UUID、旧物品格式、请求与回执关联、能力限制 | 提前确认交易、静默丢弃物品、修改主体成功/失败含义 |
| Framework 传输 adapter | 新协议包、物品引用、发送结果与领域结果的转换 | UI 状态和地图物品所有权 |
| 交易主体 | 待确认物品托管、操作状态、确认后的提交/恢复、幂等交付 | 解析旧包、依据 Legacy/V2 改变业务成功标准 |
| 物品主体 | 编解码、保留不可解码数据、完整性检查 | 将不支持的物品静默过滤后继续交易 |
| UI | 展示状态、提交用户意图、异常安全的缓存和 GUI 清理 | 决定协议、把本地预测当作服务端确认 |

兼容代码主要收敛于 `Extensions/LegacyAdapter/Client` 及明确命名的传输 adapter。现有公共兼容接口可以作为迁移桥梁保留，避免破坏附属 mod 二进制兼容；桥梁内部应调用协议无关的状态更新/操作结果入口。设置迁移与连接协商不等同于业务语义泄露。

## 不变量

1. 普通快照更新、乐观预览和操作确认是不同事件。写入仓库不能凭空确认请求。
2. 只有真实的、与请求关联的成功回执，才能提交该请求托管的物品。重复/过期回执不能再次提交或返还。
3. 明确拒绝或确定未发送可以恢复；已经发送但回执丢失属于结果未知，不能直接自动返还，否则可能复制物品。结果未知须保留记录并通过同步核对。
4. 物品转换必须整批成功或整批失败。不支持的 codec、损坏 payload、缺失物品引用都不能变成部分成功。
5. 原版 `UpdateTradeItemsResponsePacket` 的 `Success/Token/Items` 是报价操作结果；`UpdateTradeItemsPacket` 是接收者视角状态，`Items` 为本方、`OtherPartyItems` 为对方。空列表是合法的空报价。
6. 发送失败必须通过显式结果或异常传递到业务调用方；处理器声明 Handled 只表示已处理，不应掩盖失败。
7. 原版服务端不支持的功能由 Legacy adapter 明确拒绝。只有不改变用户意图和业务结果的显示投影允许适配；不得降级成功、交付或完整性语义。失败时不得由 UI 自动换协议发送。
8. 缓存重建成功后才能清除失效状态；锁、滚动区域和 GUI 全局状态必须在异常路径释放/恢复。
9. 异步动作的异常处理在动作实际执行处建立。日志不能替代待交付记录和业务恢复。

## 首轮修复与验收

首轮已实现原版报价确认链修复，保持现有公开接口可用，通过新增 `IFrameworkTradeUpdateResultApi` 提供协议无关的请求登记和结果入口：

- 删除发送后的本地预测成功；原版真实成功/失败回执归一化为主体操作结果。
- 普通 legacy 状态刷新不能清理请求 token 或制造操作成功。
- 原版空报价按权威状态应用。
- adapter 出站物品转换失败时停止发送整批请求，并反馈原 token 的失败；发送失败不能继续乐观写入。
- 主体编码器不再过滤失败项；一件物品编码失败则整批抛出异常，交由已有调用方恢复。
- 未提供 token 的调用在 adapter 中生成唯一 wire token，防止旧回执确认后续操作。
- 明确未连接会通知操作失败；其他传输异常保留 pending，等待真实回执，不能认定服务端未收到。
- 新客户端用 `core.item.scribe-v1` 保存完整 `Thing`/`ThingComp` 状态；基础物品预览与 opaque 状态分离，服务端不解释状态载荷。
- 原版服务端路径由 Legacy adapter 将状态载荷封装在旧服务端能够原样保存、转发的 `InnerProtoThing` 哨兵节点中。哨兵格式、深度/大小限制和还原逻辑不得移出 `Extensions/LegacyAdapter/Client`。
- 入站状态载荷必须与旧协议可见的物品预览一致；缺失 codec、载荷损坏、缺失 Mod/Def 或预览不匹配均拒绝交付，不能降级为重新 `ThingMaker.MakeThing` 的空白/随机物品。
- 从客户端库存加入报价时，Trade 通过 `IInventoryReservationApi` 先原子预留，再让库存使用条目自己的 codec 整批物化为不落地的临时对象；Trade 转换后立即销毁临时副本。只有与请求 token 匹配的成功回执会提交预留；拒绝恢复，派发后异常标记为结果未知。Legacy Adapter 只负责把旧回执翻译成同一操作结果，不得直接操作库存。
- 库存发红包使用红包自身的中继发布确认：整批状态分片先于 create，稳定事件 ID 重试，真实接受回调在游戏主线程移交库存所有权。发送者模板与领取余额在红包插件存储中先刷盘；过期返还须等中继历史完整消费，断线/重启后的历史尚不自动核对，未决记录继续托管。HTTP 发送排队不等于成功，不修改服务端或 Host 专用业务接口。

验证：`Tests/LegacyTradeRuntimeTests` 使用编译后的真实 adapter、交易服务和 protobuf 包，注入内存传输，9 组场景通过：发送不提前确认、拒绝回执、未连接、整批转换拒绝、空报价、唯一 token、整批编码拒绝、发送结果未知后收到回执、状态物品经旧服务端消息形状往返。包含重复/未知回执不覆盖状态、普通快照携带 token 仍不得确认、状态载荷逐字保留及 UI 快照继续持有状态的断言。

测试程序集按 mod 的 net472 依赖编译，在 .NET 10 下运行无 GUI 场景，避免 Windows Framework 测试进程与游戏依赖的 netstandard 版本冲突。它不替代 RimWorld/Unity 内的联机测试。

从仓库根目录执行（Visual Studio MSBuild；`SolutionDir` 必须为仓库绝对路径并带末尾分隔符）：

```powershell
MSBuild Tests/LegacyTradeRuntimeTests/LegacyTradeRuntimeTests.csproj /t:Build /p:Configuration=Release /p:MSBuildEnableWorkloadResolver=false /p:SolutionDir=<repository-root>/
dotnet exec --runtimeconfig Tests/LegacyTradeRuntimeTests/runtimeconfig.json Tests/LegacyTradeRuntimeTests/bin/Release/LegacyTradeRuntimeTests.exe
```

若 protobuf 的旧 SDK 固定版本阻止构建，可在当前命令进程中将 `MSBuildSDKsPath` 指向已安装 SDK 的 `Sdks` 目录；不要修改 vendored protobuf 的 `global.json`。

部署需同时更新产物 `09-TradeExtension.dll`、`10-LegacyAdapter.Client.dll`、`12-TradeExtension.Client.dll`，避免新 adapter 与旧契约/实现混用。

## 库存日志恢复边界

库存按存档路径分开记录日志，切换文件名期间的操作可能使某份日志出现历史序号间隔。当前存档快照已覆盖的间隔不再误判为损坏；历史记录仍必须通过校验和、账本状态、严格递增序号及包头与载荷序号一致性检查。同序号记录必须与存档内容完全一致。快照之后的首条记录必须为 `snapshot + 1`，后续必须连续，且仍须玩家确认恢复；损坏、重复序号、内容冲突或未来缺号继续只读保护。

日志追加只接受下一提交序号。崩溃留下完整载荷但缺少换行时，保留原文件备份并补齐分隔符；真正残缺的尾部在验证快照一致性后才隔离。未修改日志格式、存档字段、幂等回执、预留或服务端确认标准，不重发奖励，不自动归还结果未知的物品。对应英文说明见 [Inventory.md](Inventory.md)。

## 后续修复清单

以下是已发现但不能以首轮修复完成为由宣称解决的工作：

- 将待确认物品从窗口迁出，支持关窗、断线及结果未知后的核对。
- 重置报价等待确认后返还；完成/取消物品交付保留可恢复记录。
- Framework 发送失败传播及缺失引用整批拒绝。
- 将现有 `ApplyLegacyTradeSnapshot/RemoveLegacyTrade/CompleteLegacyTrade` 兼容桥梁迁移到统一领域入口，清理重复的 native/legacy 状态通知实现。
- 旧 JSON 物品解析移入 adapter；保留原始 payload，避免 UnknownItem 过滤造成数据丢失。
- 统一聊天意图入口，协议路由和回复/提及能力降级收敛至 adapter。
- 用户侧栏缓存、锁、GUI 清理、扩展能力筛选和回调隔离。

后续迁移须逐项更新本文件的完成情况，不能通过关闭功能、吞异常或降低确认标准换取表面兼容。


## 2026-11-02 原版交易能力核对补充

固定 thomotron/Phinix ce82b8f 的确认包没有报价版本，原版客户端Reset在本地返物后才发送无token空报价。服务端Adapter不能代填观察版本，也不能撤销已发生的本地返物；旧端尽力交付的授权不代表这些资产约束被豁免。当前只完成原版报价编解码/内部bridge及真实SQLite组件证据，未注册原版Trading公共入口。完整支持范围须在SV-52/53收口，不能由已通过的原版聊天联机推导。见 Server-Upgrade-P5-Original-Offer-and-UDP-2026-11-02.md。


## 2026-11-02 服务端审计补充

P5 Server请求隔离与生命周期修复见 [专项记录](Server-P5-Design-and-Reliability-Audit-2026-11-02.md)。Item predicate/handler/codec失败停止当前请求，不能借异常继续另一个候选；消费/停止不是权威成功或退款。通用广播服务各插件在注册/激活时均可解析，但初始化未完成或已关闭时不得派发。Framework模块、会话替换/断线订阅按所有权成对释放，注册失败不注销他人模块。

固定UDP backend仅支持wildcard Start(port)；不支持的指定IPAddress现明确拒绝，默认0.0.0.0保持。无wire/持久化格式迁移，旧资产/身份/Unknown不清理。原版资产交易与现代身份/TLS的既有限制不变。
