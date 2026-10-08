# 客户端兼容边界与故障恢复约束

本文件约束 Rework 客户端连接原版服务端及 Framework 服务端时的实现。原版协议以同级 `Phinix/Common/Trading/ClientTrading.cs` 和 `ServerTrading.cs` 为本轮核对依据；其他服务端变种须提供独立证据，不得以猜测改变通用业务语义。

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
7. 原版服务端不支持的功能由 adapter 明确拒绝或有说明地降级。失败时不得由 UI 自动换协议发送。
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
