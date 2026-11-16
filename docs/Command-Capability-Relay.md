# Command Capability Relay 服务端实现

插件接入、扩展点示例及执行位置选择见 [服务端插件开发指南](Plugin-Development.md)。

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

## 验证与共享交付

2026-11-10 两端 Common 精确引用统一为 `911047cf8a6dd34e661e107e182e9c921b8e9586`。共享预算位于 Common/Connections/PinnedUdpReceiveBudget.cs；两端按固定 net35/netstandard DLL 校验哈希及反射布局后复用。不要恢复为两份独立预算实现或跟随移动分支刷新子模块。

用户反馈黑盒全部通过，并授权交叉兼容、压力场景采用白盒作为本次验收标准。最终 Pipeline 通过 21 组、628 条断言，服务端阶段回归通过 50 项。真实 UDP Relay 验证身份、登录、协商、快照、广播、单播、可信身份、接收会话及旧协议连接排除，服务端未安装对应业务插件。阶段回归包含 Chat、Trade、Adapter、身份/TLS、持久化、架构和进程恢复。

压力白盒包含虚拟时钟 1800 秒/18000 请求/27000 逐连接结果、1000 次慢接收者部分提交、全局广播放大预算、129 接收者整次拒绝、100 轮重连清理、64 条待决请求上限和超时恢复。真实 UDP 后端验证 2048/连接及 8192/全局出站积压拒绝与 ACK 排空；TLS 验证实际 16 帧 Channel、4 MiB 字节预算及排空恢复。两端实际固定 DLL 的收包预算包含 1 MiB 载荷、异常分片/合包、重组、事件/字节水位和恢复。

```sh
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-relay-new-evidence --timeout 120
```

证据、冻结旧组件矩阵及验收边界见 [R5 最终核对](Command-Capability-Relay-R5-Final-Verification.md) 和 [交付摘要](Command-Capability-Relay-R5-Delivery.json)。虚拟时钟、注入边界和冻结旧组件不等于真实旧发行进程混部或实机长时 RSS 测量。完整客户端 API 说明见 [客户端 Relay 参考](https://github.com/HunYuan2333/Phinix-Rework/blob/codex/command-capability-relay/docs/Command-Capability-Relay.md)。
