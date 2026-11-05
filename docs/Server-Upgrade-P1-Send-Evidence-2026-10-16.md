# P1 传输发送结果分类证据（2026-10-16）

SV-13 / TQ-10 的传输子集。Server 基础 SHA `5a0508acb703319e60dd5600e3e0facd0397b294` 加 dirty 源码；精确命令、输入/程序集 hash 和固定 gitlink 见 [机器证据](Server-Upgrade-P1-Send-Evidence-2026-10-16.json)。没有新增 NuGet 依赖、跨仓库修改或发布。

## 产品行为

新增 `NetServer.TrySendWithOutcome` / `TransportSendOutcome`，原 Send / bool TrySend ABI 保留。共用参数检查、peer 查找及 module+bytes 编码准备；实际 handoff 前在 sendLock 内再次检查连接，调用一次，不重试、不换协议。分类如下：

| 结果 | 已有证据 | 不能推导 |
|---|---|---|
| NotSent | 参数、peer 或准备失败；或实际 handoff 前已知未连接，Send 未被调用 | 领域操作未提交、可以退款 |
| Submitted | 已调用底层 Send，正常返回且之后仍观察到 Connected | 确认入队、远端收到、游戏保存、业务 COMMIT |
| Unknown（默认值） | handoff 开始后异常，或返回时连接已改变 | 安全退款、盲重发、改协议再发 |

固定 LiteNetLib DLL 的 `NetPeer.Send(byte[],int,int,SendOptions)` 在 Disconnected 状态直接返回，不能用 void 返回等同成功入队。通过本地 monodis 检查实际固定 DLL；未修改 vendor，也没有按网上其他版本假定 API 行为。此版本没有提供可依赖的接收/持久 ACK，故命名 Submitted 明确限制为 API 提交证据。

Framework 当前直接发送路径已调用新 API；失败日志区分 NotSent/Unknown，不再仅记录 bool false。新 API 不记录异常 message、payload 或秘密。既有 bool TrySend 及其他调用方的旧日志未在本轮整体重写。业务状态、操作结果、outbox 没有因此获得持久性。

## 实际验证

- 先加入独立预期：`dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --send-outcome` 在产品改动前退出 134，明确报告没有三态 API。这只证明接口缺口，不冒充复现真实丢包。
- 最终构建：`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet build Tests/ServerLifecycleTests/ServerLifecycleTests.csproj -c Release --no-restore -m:1 -p:BuildInParallel=false` 通过，0 error，保留 vendor NETSDK1212 warning。
- 原生命周期控制台默认入口现在也执行新增 8 项断言；无 UDP 的检查包括参数/未知 peer 拒绝、handoff 前断开不调用、handoff 后异常/连接变化保持 Unknown 且只调用一次、正常返回仅 Submitted。后两种是隔离外部传输调用的受控回调测试（`transport.handoff.throw` / `transport.handoff.disconnect`），不是真实系统故障或玩家可触发的生产 fault hook。原 6 组生命周期检查仍通过。
- `dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --send-network`：真实 NetServer + 固定 LiteNetLib 客户端、回环 UDP、临时端口，连接时 Submitted 且实际收到的 module 与 opaque bytes 逐字一致；服务端移除 peer 后新 API 为 NotSent，旧 bool API 仍为 false。没有 fake Send 成功替代网络。不是独立进程 Harness、现代会话 profile 或应用 ACK。
- `dotnet Tests/ServerSafetyRegressionTests/bin/Release/net10.0/ServerSafetyRegressionTests.dll`：21 项通过。
- `dotnet Tests/ServerArchitectureTests/bin/Release/net10.0/ServerArchitectureTests.dll`：通过，既有 8 处 LegacyFallback 债务未增加，4 个负对照通过。没有使用新增包或移动兼容逻辑。

原控制台已纳入新增基础检查，quick/stage 沿用原 lifecycle 入口；真实网络需显式参数，不强制无网络 CI 启动 UDP。本轮未跑全 stage、publish、coverage、空缓存、远端 CI 或 Docker；没有游戏 DLL/玩家数据/凭据制品。

## 下一依赖与限制

SV-13 未整体完成：Common 的 ServerFrameworkContext.SendMessage、dispatcher、出站 pipeline 仍是 void；Framework 目前消费并记录分类，插件尚不能直接拿到结果。下一切片提供通用 Framework/dispatcher 结果传播并处理拦截/筛选/改目标路径，保持插件平权和固定 Common gitlink。

未知结果不自动退款或重发；Submitted 也不能删除交付记录。SQLite、operationId/事务 outbox、客户端持久接收及真实游戏存档到账都没有实现或验收。捕获到错误分类只是后续账本的输入，不能替代账本。P1 继续实施，再进入最小 P2/P3。
