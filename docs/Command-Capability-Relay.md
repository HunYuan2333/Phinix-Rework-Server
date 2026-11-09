# Command Capability Relay 服务端实现

Framework 通过 core.command-relay.v1 协商客户端中继协议。普通 Command 缺省执行服务端 Handler，显式 CommandRoute.Mode 为 Relay 的请求通过统一 Command Pipeline 的 Interceptor 后进入能力 Router。

CommandCapabilityRouter 维护当前 Ready 连接 epoch 的完整能力快照及反向索引。路由键为 PluginId、CommandType、Version，按版本精确匹配。声明与客户端 Handler 独立。

## 连接与投递

Hello 的能力响应在双方协商 Relay 后附带 relay_epoch。客户端通过 command-capabilities 提交完整快照，服务端通过 command-capabilities-ack 返回 epoch、revision 和错误。旧 revision 无法覆盖新状态，相同 revision 的内容冲突被拒绝。断线删除索引，重连使用新 epoch，令牌轮换保持连接 epoch。

目标为空时广播到声明对应能力的 Ready 连接，默认排除发送连接。目标为玩家 ID 时向该玩家的所有合格连接各投递一次。零匹配广播返回 NoRecipients，玩家离线返回 TargetOffline，旧协议目标返回 UnsupportedRelay，未声明能力返回 NoCapability。

Command Interceptor 的 Continue 继续，Replace 重新验证最终报文，Blocked 拒绝，Handled 终止路由并反馈 Handled。模式切换被拒绝。认证上下文与原始请求关联保存在插件可变报文之外。Router 使用认证连接的 PrincipalId，逐接收者替换 SessionId。

投递复用 ServerPipelineRunner.DispatchOutboundWithResult。出站拦截器可缩小接收集合，发送前检查授权、接收声明、发送者身份与已预留大小预算。CommandRelayResult 在 Observer 执行前设置。Status 为 Submitted 仅确认本地网络提交，Partial 和 Unknown 保留逐连接不确定结果。Framework 不自动重试业务命令。

## 组合配置

组合根可在创建 PhinixFrameworkServer 前注册以下服务。

```csharp
context.AddService(new RelayLimits
{
    MaxRecipients = 128,
    CommandsPerSecond = 10,
    BurstCommands = 20,
    FanoutBytesPerSecond = 1024 * 1024,
    BurstFanoutBytes = 2 * 1024 * 1024,
    GlobalFanoutBytesPerSecond = 8 * 1024 * 1024,
    GlobalBurstFanoutBytes = 16 * 1024 * 1024
});
context.AddService<ICommandRelayAuthorization>(authorization);
```

默认允许认证玩家在预算内进行普通玩家单播与能力广播。授权策略依据可信 principal 和路由字段作判断。系统命名空间 core、builtin、framework 及点分子命名空间保留。

能力上限为每连接 256 项，快照与 Relay 信封编码后最多 64 KiB。控制消息每连接每秒 4 次，突发 4 次，超额在业务载荷解码前丢弃。有效状态替换间隔至少 250 ms。

IRelayTransport 提供有界中继提交。UDP 在新增 Relay 前检查接收连接可靠有序队列 2048 包和全局 8192 包水位，最后一次提交与可靠窗口数据构成额外余量。TLS 复用既有有界出站队列。既有 UDP 分片与收包队列保护继续生效。

这些预算针对中继的准入与放大。客户端收包前分片与队列保护已接入同一共享实现，实际固定 net35 DLL 的网络预算测试通过。长时压力及进程峰值内存仍需独立验收。历史 UDP/ClientKey 的认证和链路安全限制继续适用。

## 验证记录

2026-11-09 在当前工作树运行以下验证。

- 完整服务端阶段回归通过 50 项检查，证据为 /tmp/phinix-command-relay-stage-final/manifest.json。
- 真实 UDP 测试通过认证、登录、协商、快照、能力广播、发送者身份、接收会话、旧客户端排除及单播错误。服务端未加载对应业务插件。
- 最终 Phase35ServerRuntimeTests 通过 12 组、95 条断言，包含旧 Command characterization、声明所有权、ACK 重试与重连、路由、权限、预算、异常隔离及冻结旧 JSON 信封测试。
- 阶段回归覆盖既有 Chat、Trade、Adapter、身份、TLS、持久化、模型、架构与独立进程恢复。

复现入口如下。

```sh
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-relay-new-evidence --timeout 120
dotnet build Tests/Phase35ServerRuntimeTests/Phase35ServerRuntimeTests.csproj -c Release
dotnet Tests/Phase35ServerRuntimeTests/bin/Release/net10.0/Phase35ServerRuntimeTests.dll
dotnet build Tests/ServerLifecycleTests/ServerLifecycleTests.csproj -c Release
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --relay-network
```

本次尚未进行真实游戏联机、长时洪峰或真实旧发行版交叉部署。Common 协议修改位于两个消费方的子模块工作树，发布前需形成共享提交并统一精确 gitlink。两端新增 CommandRelay.cs、ClientCommandRelay.cs 的内容应保持一致。完整插件 API 与客户端行为说明位于 Phinix-Rework 的 docs/Command-Capability-Relay.md。

## 共享预算后续实现

2026-11-09 将 PinnedUdpReceiveBudget 提取到 Common/Connections，两端按各自固定 LiteNetLib DLL 校验后复用。服务端原有同步回调与收包资源行为保持。提取后的服务端阶段回归再次通过 50 项检查，专项 UDP RECEIVE BUDGET 测试也通过。证据为 /tmp/phinix-relay-shared-budget-stage/manifest.json。

客户端 net35 测试覆盖真实 1 MiB 载荷、异常分片、合包、分片组及事件数/字节水位、排空、轮询故障和重连。两个后端的资源预算水位继续包含末次重组与可靠窗口余量。

## R0–R4 关闭与 R5 交接

R0–R4 已完成核对，最新 Pipeline 白盒通过 13 组、109 条断言。本次阶段回归通过 50 项，证据为 /tmp/phinix-relay-r0-r4-closeout/manifest.json。客户端仓库 docs/roadmaps/Command-Capability-Relay-R0-R4-Closeout.md 记录调用链和共享交付方式，Command-Capability-Relay-Source-Baseline.json 记录关键源码指纹。

R5 白盒代码包含 RelayWhiteBoxTests.cs 的并发声明及真实模块回滚断言。客户端仓库 docs/roadmaps/Command-Capability-Relay-R5-Tests.md 提供白盒命令及执行结果，并提供由用户执行的 B01–B18、C01–C04、P01–P05 和最终验收清单。真实发行版交叉部署、游戏内验收及最终包指纹保持待执行。
