# P5：原版交易网络入口与混合交付

日期：2026-11-04（Asia/Singapore）。关联 SV-03、SV-50～53、TQ-01/组件及真实网络回归、FR 的提交/未知交付保留要求。继承 11-03 用户授权的有损兼容例外，不把原版能力包装成现代可靠交付。

## 本轮结果与范围

显式启用后，原版 `Trading` 创建、报价、tokenless Reset、当前报价确认、零报价取消、完成事件、全量/增量同步和离线 Pending 恢复已接入统一 SQLite 服务。原版↔原版和原版↔现代 Framework 的声明流程通过真实 UDP/独立进程检查。现代双方保留完整载荷和报价版本确认。

客户端仅修改 `Phinix-Rework-P3-Business` 的 `codex/p3-trade-business`：增加一次连接内首次基础条款提示及中英文文案，不改布局、不切换协议、不因提示确认或退还资产。工坊/UI 分支没有同步这些业务改动。Server → P3 客户端的新增依赖只是一项可忽略的 Framework 快照 metadata；未改 Common gitlink、协议 schema、vendored protobuf 或 SDK pin。

**仍需实机验收：** 原版与 P3 Rework 的游戏物品物化、库存、保存/读档、Windows/Waylith、ECS 环境。本轮未上传、部署、重启云端容器、发布工坊或推送分支。P5 声明的该自动化子集完成，不将完整历史 Rework/任意 Mod 物品/所有平台矩阵计为通过。

## 行为和边界

1. 原版包只在 `OriginalTradeAdapter` / `OriginalTradeWireCodec` / `OriginalTradeOfferCodec` 解析，固定依据为 thomotron/Phinix `ce82b8f2cd57718f05de86458eebd3320f35c4d8`。Create/Status 原包没有 operation ID，每次到达由服务器分配操作身份；不承诺其跨重连应用幂等。报价有 token 时继续稳定映射、冲突拒绝；原版 tokenless 空报价按原意清空。
2. Adapter 不另建账本。Trade 插件提供 `ITradeEndpointRegistration` / `ITradeEndpointRouter` 中立端口，注册有配对释放；宿主不增加 Trade 专属启动或恢复分支。核心只保存通用交易条款，协议判断留在边界。未伪造 Framework Ready、能力或旧端报价已读版本。
3. 登录本身无法区分原版和等待 Hello 的 Rework。登录只建立临时只读路由并同步列表；真实且已认证的原版 Trading 请求才确认该连接。首次可由原版发起交易；完成识别后，现代端也可主动发起。**现代端不能直接向完全未识别的旧端发起首笔交易。** 不能以“没发 Hello”宣称精确识别二进制版本。
4. 未识别连接不提前尝试有物品的 Pending，以免正在升级/握手的现代端忽略原版通知后，账本却已记作尝试。原版重连后首次真实 Trading 活动触发有界恢复；完全无操作时，有物品的待交付继续保留 Pending。空物品取消可立即通知未报价的另一侧。现代 Ready 永远优先，不能用 raw Trading 绕回旧协议。
5. 全量同步使用 `SyncTradesPacket`；单笔增量使用原版报价及状态更新包，不用会清空整个列表的全量包代替。输出先整批编码，再发送。
6. 原版没有状态失败回执。失败不会伪装为 `CompleteTradePacket.Success=false`，因为那代表真实取消并可能触发返物；保留失败诊断并关闭受影响连接。真实完成/取消仍在业务、结果、outbox 同事务提交后发送。
7. 有损投影发生在报价提交前，终态和待交付使用已经保存的基础物品。保留种类、数量、材质、品质、耐久和嵌套物品；额外 Scribe 状态/metadata 丢弃。不按协议静默过滤整件物品。无有效基本表示、恶意请求、错误主体、配额不足或持久化失败仍不能制造成功。
8. 原版无持久 ACK。已尝试发送保留 `OutcomeUnknown`，不自动重投/退款；未尝试的离线交付保持 Pending。Rework 自己的持久接收/receipt 流程继续生效，不能用一侧回执替代另一侧证据。禁用适配器不删除账本或重解释历史条款。

## 实际修复

| 文件 | 修改与原因 |
| --- | --- |
| `Extensions/LegacyAdapter/Server/OriginalTradeAdapter.cs` | 原版认证、会话选择、报价 token 关联、真实输出、登录/关闭订阅与释放。提交后的发送错误不重写成拒绝；冲突 token 在尚未观察结果时返回真实失败。 |
| `OriginalTradeWireCodec.cs` | Create/Status 字段与方向校验；收件人视角的完整同步、报价、状态、完成/取消编码。 |
| `OriginalTradeOfferCodec.cs` | 显式允许原版 tokenless 到达，保留原有严格入口及带 token 的稳定映射。 |
| `OriginalTradeRefusalAdapter.cs` | 保留历史模块 ID；默认关闭时保留拒绝 guard，显式开启时启动新适配器；声明 Trade 提供者依赖。 |
| `Extensions/Trade/Server/TradeEndpointRouting.cs` | 插件拥有的替代路由、配对注册、有界恢复及快照条款标记；不修改宿主特权。 |
| `TradeNegotiationTerms.cs` / `PhinixFrameworkTradeServerService.cs` / `BuiltInTradeServerExtension.cs` | 生产组合注入投影器；实际端点选择创建条款，输出路由与 Pending 尝试保持统一。创建请求指纹不依赖对端稍后变化的在线/握手状态，重放不因协议选择变化而改写。 |
| `Server/ServerRuntime/IServerPrincipalAuthorization.cs` | 增加中立的本地已认证会话查询；不暴露 ClientKey 或凭据映射。 |
| `Server/Authentication/ServerAuthenticator.cs` / `Server/UserManagement/ServerUserManager.cs` | 返回仍有效且主体已登录的会话事实；实验性现代身份不走旧 wire bootstrap。修复真实原版测试确认的登录 `AcceptingTrades` 字段被忽略问题，对所有协议应用已认证登录意图。 |
| P3 `Extensions/Trade/Client/BuiltInTradeClientExtension.cs` 与两份 `TradeExtension.xml` | 从明确的条款 metadata 提示有损属性及弱交付，主线程执行、连接/关闭生命周期清理，不修改窗口布局。 |
| `tools/configure_trade_limits.py` | `--original-trading on/off` 生成新配置候选，保留其他设置；拒绝重复/损坏节，不覆盖输入。 |

## 实际验证

Server 基线：`dev` / `5a0508acb703319e60dd5600e3e0facd0397b294`，包含原有及本轮未提交修改。P3 客户端基线：`53c92ce8eb314dcb8ca422ae4b6891a007931128`，亦为脏工作树。完整源码身份以证据清单逐文件 SHA-256 为准，不能只用 HEAD 代表构建。

Server Common 固定 `67f243d9edced77dc3fe669f8a435b1f14b59199`；客户端 Common 固定 `c165f61d749189f700ca896d63d9366d5c71dbf7`。原版参考、protobuf 固定来源和真实原版依赖 hashes 由网络运行器核对；Pastel 1.3.1 仅为独立原版模块构建的测试依赖，未加入产品。

- **104 项组件检查通过**：真实服务/序列化器/SQLite；包括前轮 94 项和本轮会话授权、Ready 拒绝、重复激活隔离、配对释放及重入边界。
- **原版/混合真实网络 88 项通过**：原版普通 42、重启 23、混合原版 15、混合现代 8。覆盖双方创建方向（识别后的现代发起）、对方可见报价、当前确认、真实完成载荷、零报价取消、tokenless Reset、同 token 冲突拒绝及不改报价、离线取消后正常停启恢复、Unknown 不重投；还验证启用适配器时现代双方载荷保持原样且无版本确认被拒绝。预期由固定原版 `ClientTrading` 和独立现代 wire 探针解释，非服务端 Adapter 生成。
- **独立只读权威检查通过**：原版网络测试结束、服务端停止后，Python sqlite3/XML 读取真实文件账本，确认指定离线交付仍保留一份 `OutcomeUnknown`，无伪造 ACK。此记录数只证明去重/状态证据，不能证明游戏资产守恒。
- **原版聊天与默认交易关闭回归通过**：原版初始/重启/混合与现代侧合计 56 项，沿用既有明确拒绝边界，不用新 Trade 配置影响独立 Chat。
- **最终阶段 49 步、62 个发布文件检查通过**：L1/L2、独立模型/架构、发布及 P3 独立进程 UDP 强杀/重启链。原版专项本身验证正常停启恢复，不把 P3 其他 profile 的强杀检查称作原版强杀验收。
- **配置检查通过**：7 项 Python 回归；实际 Config provider 读取候选、默认 off、on/off 往返及其他设置保留检查通过。
- **P3 客户端 1.6/1.7 双版构建及 95 文件包审计通过**；1.7 组合 320 项、wire 34 项通过。首次组合检查缺少 Host 引用，只是测试引用闭包不全；用同次构建的完整 runtime-assets 闭包补齐后执行通过，未修改预期或替换游戏行为。提示的真实 UI 表现仍待实机。

所有命令经既有 `p4-work/run` 环境包装器执行，未更改 vendored SDK pin。完整参数保存在机器证据清单，主要入口为：

```sh
dotnet build Tests/ServerOriginalTradeAdapterTests/ServerOriginalTradeAdapterTests.csproj -c Release -m:1 -nr:false -p:BuildInParallel=false -p:NuGetAudit=false
dotnet Tests/ServerOriginalTradeAdapterTests/bin/Release/net10.0/ServerOriginalTradeAdapterTests.dll
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-original-trade-stage-verified-20261104 --timeout 120
python3 tools/quality/original_chat_acceptance.py --trade ... --modern-probe ... --output /tmp/phinix-original-trade-native-verified-20261104
python3 -m unittest discover -s tools/quality -p test_trade_configuration.py -v
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --trade-config
python3 tools/compatibility/build-dual-host.py --game16 ... --game17 ... --harmony17 ... --output /tmp/phinix-original-trade-client-dual-20261104
```

seed：上述确定性协议流程无随机调度 seed；身份为隔离合成值，不保存或公开凭据。阶段故障点含 `p3.kill-after-offer-response`、`p3.kill-with-offline-pending`；不声称断电、磁盘损坏或所有 Windows 行为通过。

## 失败记录与修复，不通过重试绕过

原版专项首次失败于实际 `NotAcceptingTrades`，确认服务端忽略登录字段后修复。离线核对测试曾错误使用断线后已被原版清空的本地 UUID，已改用断线前保存的稳定身份；并按固定后端 30 秒期限提供 40 秒有界等待，不改产品超时。

最终阶段曾失败于测试客户端 `LiteNetLib.NetPeerCollection.RemoveAt` / `UpdateLogic`：接收回调已采用固定 backend 的 peer gate，探针主动 `DisconnectPeer` 却未共用 gate。现发送/主动断线也使用同一 gate，Stop 不持锁等待线程。新增确定性 held-gate 检查，验证断线在锁释放前不能完成，之后能完成；隔离副本移除此锁时精确失败于 `Probe disconnect bypassed the pinned peer mutation gate`。没有修改 DLL、跳过测试或无限重试。失败阶段证据与后续通过证据分别保留。

## 配置、回滚与下一步

默认仍为 UDP / ClientKey；TCP+TLS 实验性状态不变。原版交易默认关闭。用下列工具生成候选，停止唯一实例后审阅/替换并重启；本轮没有对 ECS 执行它：

```sh
python3 tools/configure_trade_limits.py --input /opt/phinix/data/server.conf --output /opt/phinix/data/server.conf.next --original-trading on
```

关闭用 `off`，不删除历史记录，也不能恢复已经有意丢弃的属性。完整 Server 程序及 Extensions 必须作为一个组合交付；P3 客户端双版包独立，不合并工坊/UI。测试/发布副本回滚须保留新账本及源导入证据，不能拿测试前旧数据覆盖已提交交易。

现在可安排 P6 双端实机：先备份测试存档；原版先发起识别，验证 Rework 能看到报价与兼容提示；双方小额报价/确认、零报价取消、A 已确认但 B 未报价时取消、原版 Reset；然后测试一侧离线取消、服务端重启和接收者重新进行交易活动后的待交付。记录完整 Player.log、Server 构建身份和时间。真正保存并重启读档核对双方库存，不以网络探针的 ProtoThing 数量替代。没有实际执行的场景不计通过。
