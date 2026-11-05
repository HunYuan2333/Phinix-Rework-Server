# P5 / SV-51：原版普通聊天 Adapter（2026-11-01）

## 交付范围

本轮 Server `dev`（HEAD `5a0508acb703319e60dd5600e3e0facd0397b294` 加既有未提交修改）实现原版 Phinix 的普通聊天、原消息 ID 关联回执、历史同步与服务端正常重启恢复。入口默认关闭，由 `builtin.legacy-chat-adapter` 插件配置显式开启。未部署云端、未改客户端/工坊分支、未修改 Common/protobuf gitlink 或 SDK pin，无新增产品 NuGet 依赖。TCP＋TLS / 现代身份仍实验性且不建议使用，正式切换仍 TODO。

关联 SV-50/51、TQ-50、FR-09/13/14。P5 不标整体完成，原版 Trade、持久交付和完整新旧混合仍待。

## 固定来源

本地 `/home/hunyuan2333/Phinix/Phinix` 为重新 clone 的 `https://github.com/thomotron/Phinix`，固定 `ce82b8f2cd57718f05de86458eebd3320f35c4d8`（0.9.7）；protobuf 固定 `dd06cc6c8f906b760ef224a085f45e7694dc426a`。独立测试 archive 原 Common 与对应 protobuf，直接构建真实 Connections/Authentication/UserManagement/Chat，不从 Rework Adapter 生成预期或替换其序列化器。

原版 LiteNetLib net35 DLL 与当前 Common 保存的 net35 DLL 字节一致（SHA256 `c4ad697e3032a221892d894f1f225738a819b4c061b3de7f8ac4437b01b9e0c8`），仅用于测试进程；原版 Pastel 1.3.1 从 NuGet 取得并与历史 fixture 的 DLL SHA `43a7ef0c7dfc76986a3e955472795d0979213f3bfc7f57e2ecf1e5771f4940ed` 核对。不是产品新增依赖。完整 345 个上游输入、独立模块 DLL、SDK/runtime 与包 hash 在 native manifest。

## 产品改动与边界

- `Extensions/LegacyAdapter/Server` 是独立插件程序集。`OriginalChatCodec` 按固定原版 proto 定义读取 `Phinix/Chat.ChatMessagePacket`，限制尺寸、字段、ID 和正文；生成真正旧端可解码的 Response/Broadcast/History。重复、未知或错误 wire 字段明确拒绝，不添加原版不存在的回复/艾特/ACK。
- `OriginalChatAdapter` 用中立授权端口校验 connection/session/principal，并检查登录状态；不能仅相信旧包自报 UUID。Framework 已 Ready 的连接不能再切换原版 Chat 入口。同一连接保留至多 256 个关联结果：相同请求重发原结果，冲突拒绝，满额拒绝而不淘汰旧结果。它是聊天的连接内去重，不宣称跨断线持久 exactly-once，不改变资产 operation 账本。
- 只有调用统一 `IFrameworkChatServerApi.AddMessage` 得到实际结果之后，才构造旧成功回执。回执丢失不重做已接受业务。时间和新消息 ID 由服务端决定。普通聊天的确认仍不代表磁盘 COMMIT 或游戏资产 ACK，既有有界历史/定期保存语义不升级为新的可靠交付承诺。
- Chat 服务只增加协议无关的 `IFrameworkChatMessageEvents.MessageStored` 通知；消息仍进同一历史和持久化路径，不增加 Legacy store。每个订阅者得到 clone，单个观察者错误不能阻断后续订阅者或改变已存消息。
- 宿主只向所有插件提供相同的 `IServerTransport` 与 `IServerPrincipalAuthorization` 中立服务。`ServerUserManager` 用既有的认证主体核对实现后一端口；宿主不引用 Legacy/Chat 插件程序集，不加入 Chat 专属启动/恢复分支。原版转换、模式限制和拒绝都留在 Adapter。
- Adapter 自己订阅/释放登录、断线和消息事件以及 Chat 模块 handler。关停先关闭内部 gate，再在 gate 之外注销 transport handler，防止与 NetCommon 的 handler-lock → Receive 顺序倒置。通过独立 barrier 的并发关停回归，不暴露生产故障注入。
- `Server.csproj` 按现有官方插件复制/发布方式包含新程序集；发布审计必须包含 `Extensions/LegacyAdapter.Server.dll`，测试/游戏 DLL 不进入运行包。

## 启用与已知限制

普通部署保持默认关闭。独立验证启用的是插件段 `builtin.legacy-chat-adapter` 内嵌 DataContract XML，其内容为：

```xml
<LegacyChatServerConfig xmlns="http://schemas.datacontract.org/2004/07/Phinix.LegacyAdapter.Server">
  <Enabled>true</Enabled>
</LegacyChatServerConfig>
```

这段是 `ExtensionConfigs` 字典的字符串 Value，不是直接粘到 `server.conf` 根节点。修改须停服、备份并保持其他配置；本轮未修改任何云端配置，不建议在完成 SV-53 前对既有混合服务器直接开启。

原版没有 Hello/history request，Adapter 在该显式启用的 UDP profile 登录时发送可完整表达的历史；Framework Ready 后不再发送原版广播。登录和 Framework Ready 之间尚不能从共同的旧登录字段可靠区分两类端，这个时间段可能向尚未选择 Framework 的连接提交原版历史。当前只对下述真正原版 profile 声明通过；完整 Rework 客户端的模式选择、避免重复展示/错误 fallback 和双向混合必须在 SV-53 单独验证，不能据当前同服务 API 回归推导混合可用。未来应在 Adapter 收束入口/profile 选择，不在 Chat 核心推断版本。

原版无回复、艾特、Notice 的完整表达能力。带这些语义的消息/历史不截字段、不悄悄过滤：日志明确报 `OriginalChatMessageUnrepresentable` / `OriginalChatHistoryUnrepresentable`；已确认使用原版 Chat 的路由遇到无法表达的新消息会断开，历史无法整体表达时不发送伪造空历史，后续原版请求收到关联失败。旧协议不能承载详细新错误，用户端可能表现为断线或历史同步未完成，应升级或使用声明支持的内容范围。已有原版历史中普通行也不会被错误标记全部已同步。

这是已知功能限制，不把恢复用户正常完整聊天体验视为全部完成。单次入站 32 KiB、文本 8192 字符、出站及历史 256 KiB，有界拒绝而不部分发送。没有 native Windows/Waylith 或真实游戏资产验收。

## 实际验证

### L1/L2 与阶段门槛

`python3 tools/quality/check.py --suite stage --isolated --output <isolated> --timeout 120`：**46 步 / 62 发布文件通过**。新增 `ServerLegacyChatTests` **38 项**，覆盖真实聊天服务与外部 transport/authority ports 的隔离测试：字段、尺寸、不可表达内容、真实结果关联、共享历史、重放/冲突、跨主体/会话/Ready 拒绝、发送 Unknown 后复用原结果、订阅隔离、注销和确定性关停锁顺序。隔离的授权端口不被称为真实认证证明，下面真实网络单独检查认证。

原有交易 SQLite/outbox/独立 SQL oracle、COMMIT/恢复、公开字段安全、P3 独立进程 UDP、模型与架构继续通过。架构债务仍为八项 obsolete result normalization，不隐瞒或清零。固定 golden **12 项**通过。P5 首切片移动报价转换后，相应 mutation 工具的三个锚点已同步到实际边界；新增测试仅在 disposable copy 验证锚点仍可应用，不把它当成 mutation 杀死率。未修改 vendored 源码去消除既有 generated/obsolete 警告。

### L3：真正原版客户端进程 → 发布 Server Main

```sh
python3 tools/quality/original_chat_acceptance.py \
  --original-source <pinned-original-clone> \
  --pastel-package <fixed-pastel-1.3.1.nupkg> \
  --framework-root <net472-targeting-pack/build> \
  --server <complete-publish>/PhinixServer.dll \
  --config-writer <ServerLegacyChatTests.dll> \
  --output <isolated>
```

上游原模块使用 Mono；被测端是 .NET 10 实际发布 Server Main。两名客户端在各自合成私有目录自动产生凭据，不使用玩家目录；原版 ClientChat 真正编码、处理回执与更新本地状态。独立进程首轮 **21 项**：自动登录/初始历史、双方消息/实际 CONFIRMED、关联服务端新 ID、相同请求重放原结果、冲突/伪造 owner/错误 session 拒绝且历史不变。正常停止服务端，重启并重建两个客户端，第二轮 **11 项**验证原 UUID、两条消息/ID 历史完全恢复，以及继续发消息。合计 **32 项**通过。没有在测试结果中保存 credential 文件、认证包或 session 字符串。

最终发布目录（含新 Adapter）逐文件 hash 记在 native manifest；最终候选的 lifecycle 修复已重跑这32项。没有借 original→original 的旧 fixture 冒充 upgraded Server 兼容，没有给原版增加 ACK。

### 失败及证据

首次 native 尝试使用裸 `dotnet publish` 目录，遗漏项目既有单独复制的 Extensions，原版历史超时。补齐发布 closure，并在新 runner 增加事前插件存在校验后通过；失败日志保留，不伪称初次通过。组件测试早期有 ByteString LINQ 类型编译错误（测试代码）及发布形状测试仍预期旧文件数，分别按真实类型/新增必需插件修正。早期阶段候选和最终候选分别保留；不是以无限重试掩盖随机失败。

所有命令、来源/制品 hash、两个服务端启动和 native 日志在工作区 `Deliverables/Phinix-Rework-P5-Original-Chat-2026-11-01`。无故障强杀新增承诺，seed 为 null；barrier 为 test-only `held dispatch` / `unregister`。无 CI 远端、云端部署或真实游戏联机声明。

## 下一依赖

SV-51 普通原版聊天的声明子集完成；仍需 SV-52 原版 Trade 的 token/报价/状态及无损物品翻译、SV-53 真正新旧客户端混合及上述登录/profile 缺口、SV-54 旧 obsolete-result 债务与独立拒绝回归。原版尽力交付必须共用 P3 权威账本，不能为兼容新建旧 store 或伪造持久 ACK。
