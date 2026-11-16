# Phinix Rework 服务端插件开发指南

本指南适用于 `codex/command-capability-relay` 分支。服务端插件使用 `Utils.Framework` 的公共契约，经统一注册、拦截、默认处理和观察流程参与业务；部署方的通用授权及资源策略在组合根配置。客户端插件接入见[客户端指南](https://github.com/HunYuan2333/Phinix-Rework/blob/codex/command-capability-relay/docs/插件开发指南.md)。

## 选择 Command 执行位置

| 业务需要 | 入口与职责 |
| --- | --- |
| 服务端维护状态、查询历史、验证或保存业务数据 | 普通 Command，注册 `IServerDefaultCommandHandler`，由服务端业务服务执行 |
| 目标客户端插件处理不需要服务端业务权威的消息 | 客户端调用 `IClientCommandRelay.SendRelay`；服务端 Framework 按显式能力声明中继 |
| 部署方限制中继权限、审计或改写路由 | 组合根 `ICommandRelayAuthorization`，或统一 Command/Outbound Interceptor 与 Observer |

缺少 `CommandRoute` 的历史 Command 默认使用 `Server` 模式。显式 `CommandRoute.Mode == CommandProcessingMode.Relay` 才进入能力 Router；未知普通 Command 不会因存在其他在线玩家而转发。Trade 创建、报价、确认、取消仍由服务端维护权威状态，Chat 历史仍由服务端读取；它们后续发送客户端事件不改变请求的执行位置。

客户端声明接收能力与客户端 Handler 注册相互独立。服务端只索引 `PluginId/CommandType/Version`，不扫描或安装对应客户端业务插件。PluginId 是业务命名空间，不证明发送者身份，也不赋予权限。

## 注册、生命周期与处理顺序

服务端模块实现 `IPhinixExtensionModule`，用 `[PhinixExtension]` 声明模块 ID，在 `Register(IExtensionBuilder)` 中注册公共扩展点。有激活资源时实现 `IActivatablePhinixExtensionModule`，在 `Activate` 获取服务，在 `Shutdown` 配对退订、停止任务并释放本模块拥有的资源。构造保持被动，不启动业务线程或网络请求；借用的 Host 服务不由插件 Dispose。

| builder 方法 | 扩展点 |
| --- | --- |
| `AddServerInboundCommandInterceptor` | `IServerInboundCommandInterceptor`：服务端与 Relay 共用的入站拦截 |
| `AddServerDefaultCommandHandler` | `IServerDefaultCommandHandler`：Server 模式业务默认处理 |
| `AddServerCommandObserver` | `IServerCommandObserver`：最终处理结果观察 |
| `AddServerOutboundPacketInterceptor` | `IServerOutboundPacketInterceptor`：出站信封与接收集合处理 |

Relay 顺序为：Ready 认证身份与准入预算 → Command Interceptor → 最终报文校验 → 能力 Router → Outbound Interceptor → 发送前资格/大小校验 → 有界投递 → Observer → 路由结果反馈。Relay 跳过业务 DefaultHandler。Priority 相同时保持稳定注册次序。

入站拦截返回 `Continue` 继续；`Replace` 替换后重新验证，原地修改也会重新验证；`Blocked` 拒绝；`Handled` 表示服务端插件消费并停止路由。Server/Relay 模式切换被拒绝。拦截器异常终止本请求；Observer 异常隔离，不阻断其他观察者。出站拦截器可以缩小接收集合，不能通过扩大集合或改写命名空间、版本、目标来绕过授权、能力或预算。

认证主体、连接 epoch、原始请求 ID 与初始模式保存在可信上下文中。业务授权使用 `context.SenderUuid` 或组合根传入的可信 principal，不能信任插件可变信封中的 `SenderUuid`/`SessionId`。Framework 投递时使用接收连接自己的 SessionId。

## 观察 Relay 结果

```csharp
using Utils;
using Utils.Framework;

[PhinixExtension("author.relay-audit")]
public sealed class RelayAuditModule : IPhinixExtensionModule, IServerCommandObserver
{
    public string ExtensionId => "author.relay-audit";
    public int Priority => 100;

    public void Register(IExtensionBuilder builder)
        => builder.AddServerCommandObserver(this);

    public bool CanObserveIncomingCommand(FrameworkPacket packet)
        => packet.CommandRoute?.Mode == CommandProcessingMode.Relay;

    public void ObserveIncomingCommand(FrameworkPacket packet,
        ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
    {
        RelayResult result = context.CommandRelayResult;
        if (result == null) return;
        context.Log?.Invoke("Relay status=" + result.Status +
            ", error=" + result.Error + ", recipients=" + result.Deliveries.Count,
            LogLevel.INFO);
    }
}
```

`CommandRelayResult` 在 Observer 前设置，旧 `terminalAction` 投影仍可使用。记录关联 ID、错误码和数量即可，避免记录令牌及业务载荷。入口在进入 Pipeline 前拒绝的请求不保证触发 Command Observer；该扩展点不是全部网络拒绝的审计入口。

同时检查 `Status`、`Error` 和逐连接 `Deliveries`。`Submitted` 仅代表本地网络提交；`Partial` 保留部分提交或不确定结果；`NotSent`/`Unknown` 分别表示未提交及结果不确定。`Handled` 表示服务端插件消费。Framework 不为业务建立自动重试或业务 ACK；插件自行定义响应、幂等和去重，不得据路由提交结果确认交易、交付物品或退款。

## 能力路由、授权与资源边界

接收方必须是当前 Ready 连接、协商了 `core.command-relay.v1`，并声明精确能力。广播默认排除发送连接；玩家单播选择该玩家的所有合格连接，每连接一次。版本使用正整数并精确匹配。

| 结果 | 含义 |
| --- | --- |
| `NoRecipients` | 能力广播没有合格接收连接 |
| `TargetOffline` | 目标玩家离线 |
| `UnsupportedRelay` | 目标在线但无支持 Relay 的连接，或发送路径未协商 Relay |
| `NoCapability` | 目标有支持 Relay 的连接，但没有精确声明 |
| `Forbidden` | 授权策略拒绝 |
| `RateLimited` / `PayloadTooLarge` | 频率、扇出、接收数量或编码大小预算拒绝 |
| `DeliveryFailed` | 至少一个接收连接未正常提交，继续查看逐连接结果 |

在创建 `PhinixFrameworkServer` 前向 `ExtensionHostContext` 注册 `RelayLimits` 和 `ICommandRelayAuthorization`。授权接口位于 `ServerRuntime`，由部署方针对可信 principal 与 `CommandRoute` 作判断；默认允许认证普通玩家在预算内单播和广播。系统命名空间 `core`、`builtin`、`framework` 及其点分子命名空间保留。完整配置示例和所有预算见 [Relay 服务端参考](Command-Capability-Relay.md)。

快照在 epoch 内按递增 revision 原子替换，幂等重发不会重复声明，失败保留上一份有效快照；断线删除连接索引，重连使用新 epoch。业务插件不直接操作索引或伪造客户端 ACK。

当前限制包括：能力最多 256 项、编码后 64 KiB 信封、每连接 10 请求/秒（突发 20）、最多 128 接收连接及发送者/全局扇出预算。`IRelayTransport` 提供有界提交：UDP 单连接 2048/全局 8192 包水位，TLS 16 帧及 4 MiB 出站预算。自定义传输需实现这个可选接口；缺失时不能声称支持有界 Relay 提交。水位不是进程 RSS 硬上限，最后一次提交与可靠窗口存在余量。

## 构建、配套与验收

客户端、服务端 Common 精确引用为 `911047cf8a6dd34e661e107e182e9c921b8e9586`。保持固定 gitlink，不执行 `submodule update --remote`。两端协议及客户端插件应成套构建；客户端插件不得附带 Host/Common、游戏或 Harmony DLL。服务端插件引用匹配服务端/共享契约，不引入 Verse/Unity 游戏依赖。

网络协议协商与插件二进制加载是两层检查。`Could not load type 'Utils.Framework.RelayResult'` 表示实际加载的 Host/Common 缺少契约，发生在插件运行与网络协商前；应完整替换匹配客户端并排除残留 DLL。旧端网络组合应得到 `UnsupportedRelay`，不能用它解释本地类型加载失败。Example 的故意抛错模式会产生 `Example Relay handler fault injection.`；旧红包 HTTP 轮询超时属于另一个中继实现。

2026-11-10 用户反馈黑盒全部通过，并授权交叉与压力场景以白盒作为本次验收标准。21 组/628 条 Pipeline 断言、50 项服务端阶段检查通过，覆盖新旧冻结组件 × JSON/protobuf、虚拟持续路由、慢接收者、广播放大、100 轮重连、分片与实际 UDP/TLS 队列预算。记录边界及复现入口见 [R5 最终核对](Command-Capability-Relay-R5-Final-Verification.md)；这些结果不声称执行过真实旧发行进程混部或实机长时峰值内存测试。
