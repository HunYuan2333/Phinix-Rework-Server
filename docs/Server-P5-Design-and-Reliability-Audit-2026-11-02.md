# P5 服务端设计哲学与可靠性审计（2026-11-02）

## 结论与范围

对照 `AGENTS.md`、`Design-Philosophy.md`、`设计哲学.md`、`Compatibility-Boundaries.md` 与 P5 收口记录，复核 P5 Adapter、宿主管线、通用服务/插件生命周期、UDP、聊天、交易事务/恢复及测试证据。发现并修复下列问题后，当前 UDP 声明范围的自动化门槛重新通过。**“架构扫描债务为0”不代表所有架构/可靠性问题都不存在**，本轮实际发现了扫描不能捕捉的运行时问题。

没有修改客户端/UI/Common/gitlink/vendor/SDK/依赖版本，没有合并工坊 hotfix，没有部署云端或发布。这不是所有服务端文件逐行无缺陷证明、依赖漏洞情报更新、全矩阵安全认证或真实游戏资产验收。P5 原版完整交易不支持的限制继续有效。

## 确认的问题与修复

| ID | 严重性 | 实际问题、位置与触发 | 修复/证据 |
|---|---|---|---|
| P5-AUD-01 | P1 | `ServerRuntime/ServerPipelineRunner.ProcessIncomingItem`：predicate 异常被当作不匹配；interceptor/handler 抛错后继续下一候选，可能重复已有副作用。选中的 codec 出错也继续其他 codec，CanDecode 异常可逃逸，Decode 返回 null 仍报 Handled | 与 Message/Command 的请求隔离对齐：错误后 StopPropagation，不重试候选；明确 false/Continue 仍可正常串联；后续独立请求可用。29项独立预期检查覆盖各阶段及恢复 |
| P5-AUD-02 | P2 | `observeMessage/observeCommand/observeItem`：观察者抛错后再调用失败日志，日志也抛错会使已处理请求逃逸；同时完整异常字符串可含任意插件内容 | 六处观察者异常路径调用既有安全 LogFailure，仅记录类型，仍通知其他观察者。失败处理不构造成功、退款或 NotSent；属于同一29项回归 |
| P5-AUD-03 | P2 | `LegacyChatServerExtension` 未声明 `builtin.chat` 依赖，只凭类型/API碰巧先注册；无法保证提供者先激活、消费者先停止和缺依赖时隔离 | 使用现有 DependsOn 元数据。真实依赖图检查包括顺序、禁用/缺失 provider、独立原版交易拒绝插件不受连带影响；未为宿主加业务特判 |
| P5-AUD-04 | P1 | `PhinixFrameworkChatService.AddMessage`：历史已修改，日志订阅者随后抛错，使原版 Adapter 尚未来得及登记关联结果；重试可能再次写入 | 日志逐订阅者 best effort 隔离，不能改变已存消息、阻止其他日志消费者或中断业务通知。真实服务/Adapter回归验证相同请求仅存一条，原回执ID稳定。日志全部损坏时不承诺日志送达，不伪造持久聊天提交 |
| P5-AUD-05 | P2 | `OriginalChatAdapter.Stored`：CanExpress 仅判语义，超过旧包256 KiB的消息在 Broadcast 编码时抛错，被外层事件隔离后跳过，未明确拒绝对应原版路由 | 对有实际原版接收者的消息先完整编码一次；大小或语义不支持统一明确拒绝，已识别原版连接关闭，无扁平化/截断。没有原版接收者时不编码、不误报。两个确认原版连接均受检，Framework消息保留 |
| P5-AUD-06 | P2防泄露 | `OriginalChatCodec.Request` 的 record 默认 ToString 包含 SessionId 和文本。未发现产品当前直接记录该对象，不声称玩家日志已泄露 | 显式脱敏 ToString，回归保证会话/正文均不出现；wire和内存关联字段不变 |
| P5-AUD-07 | P1 | `NetServer` 直接 Invoke PollCompleted；订阅者异常在后台线程未处理，独立测试进程实际崩溃。连接事件也可阻断后续清理者；错误日志订阅者可再次抛错 | 通用事件逐订阅者隔离，仅记录固定上下文和异常类型。建立连接阶段有消费者失败则关闭该连接；关闭仍通知后续消费者，Poll仍服务下一连接。日志订阅者单独隔离。真实UDP9项回归证明失败连接不提交、下一连接可提交；不改变业务Unknown/退款语义 |
| P5-AUD-08 | P1配置安全 | 固定LiteNetLib只有 Start()/Start(int port)，`NetServer.Start` 原来忽略 Endpoint.Address，配置Loopback仍被接受而按wildcard监听 | 不改backend/vendor/reflection bind，显式拒绝无法兑现的指定地址，在监听前失败。默认0.0.0.0保持可用。新增失败前/后回归；真实UDP测试显式声明Any，客户端仍只连接127.0.0.1；未把监听全部接口伪称为只绑定loopback |
| P5-AUD-09 | P2架构/生命周期 | `Server.cs` 在 Framework 构造/插件激活后才注册 IFrameworkServerBroadcaster，第三方 Activate 无法解析。Framework关闭未注销模块与断线lambda；模块注册失败发生在原cleanup try外，会遗留已激活模块 | 由 Framework 在通用注册/激活前提供同一个中立端口；初始化未完/已停止时明确拒绝派发。具名订阅与owned标记成对清理；注册失败清理插件，不移除其他所有者的处理器。无Chat/Trade专属启动分支。真实Framework fixture验证激活、重复停止、重建及失败构造；旧源码和删清理调用两个负对照分别捕捉 |

英文/中文设计哲学 §3.5 及审查清单的“异常后一律继续下一候选者”已修正：服务端故障隔离是**停止当前不确定请求，继续后续独立请求**；独立观察者可逐项继续。停止/消费不是成功、交付、回滚或安全重试的证明。宿主调试文本也不再把 consumed 说成 processed successfully。

## 设计哲学逐项核对

| 原则 | 核对结论与边界 |
|---|---|
| 插件平等/中立宿主 | 修复通用服务可用阶段和生命周期配对。实际PE检查：PhinixServer无Chat/Trade/LegacyAdapter.Server引用；ServerRuntime无Chat/Trade实现引用；存储层无业务、Autofac/Stateless引用。官方插件构建/分发引用不等于宿主业务依赖 |
| 接口与依赖顺序 | Adapter通过API共享Chat/Trade服务；Chat提供者依赖现已显式。接口当前部分物理定义在业务Server程序集内，仍有打包耦合，未来可独立契约化；本轮不跨Common/gitlink重构 |
| 旧协议边界 | 原版包/字段/品质值/token/表达性在命名Adapter；冻结插件ABI结果在PluginResultAdapter；交易核心不解析旧包。原版交易拒绝为独立插件，不是宿主启动/恢复分支 |
| 单一权威与资产安全 | 复核完整批次引用验证、结果与状态/outbox同事务、提交后响应、重放、recipient/epoch/receipt与Unknown。完整stage真实SQLite和强杀恢复复验；未新增退款或自动投递。内部OriginalTradeOfferBridge未注册公共Trading入口，不能由其组件通过推导原版交易可用 |
| 显式所有权/失败清理 | 修复Framework模块/订阅和失败构造；原版Adapter启动失败不注销别人的模块，停止在释放自身gate后注销，避免handler-lock倒置。真实注册冲突回归检查原handler保留；通用API为借用服务，不由插件Dispose |
| 隔离与日志 | Item、观察者、Chat及UDP通知在具体现有边界修复；无全局日志框架/Trace迁移。日志故障不改变已接受业务结果，不用错误文本伪造结果；诊断sink不可用时不能保证日志留存 |
| 相称依赖与侵入 | 没有新包/框架。PinnedUdpPeerSynchronization是受控私有API兼容shim：精确DLL SHA/layout、仅线程启动前安装、明确fail closed，真实gate屏障与既有负对照仍通过。不因使用反射就认定违规，也不声称支持任意backend/AOT/单文件发布；升级时必须重审/移除 |
| 有界与交付诚实 | 旧聊天请求/回执/历史大小、原版报价深度/全批预算已有边界；无原版接收者不重复编码。未完成全面洪峰/长时资源测量；未把HTTP/发送API返回或DB物品条数当游戏到账 |

## 验证与失败纪律

Linux x64；SDK10.0.114、运行时10.0.14、Mono6.12。Server基础HEAD仍为 `5a0508acb703319e60dd5600e3e0facd0397b294`，累计dirty变更按source hash标识。Common gitlink `67f243d9edced77dc3fe669f8a435b1f14b59199`、protobuf `4b0c3aacf0657fbf38253b38918d3358dd4319ec` 未变，老Phinix参考仓库保持干净。

实际命令通过既有 `.../p4-work/run` wrapper 执行，工作目录为服务端根目录：

```text
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p5-audit-stage-final-20261102 --timeout 120
# PASS 49 steps / 62 published files；architecture baselineDebt=0，5负对照
# Item失败边界29；UDP事件9；绑定拒绝；Framework生命周期；原版Chat组件48
# 保留原报价66、插件ABI边界61、UDP peer gate6及真实事务/强杀恢复检查

dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --item-failure-boundary
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --udp-event-audit
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --udp-bind-policy
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --framework-lifecycle-audit
# 对应目标在失败前复现或源码负对照中拒绝，并在最终stage调用通过

python3 tools/quality/original_chat_acceptance.py --original-source /home/hunyuan2333/Phinix/Phinix --pastel-package /tmp/phinix-p5-original-packages/pastel.1.3.1.nupkg --framework-root /home/hunyuan2333/.codex/visualizations/2026/10/15/01a13ec4-18b2-7860-931d-fd14d2747341/p4-work/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build --server /tmp/phinix-p5-audit-publish-final-20261102/PhinixServer.dll --config-writer /home/hunyuan2333/Phinix/Phinix-Rework-Server/Tests/ServerLegacyChatTests/bin/Release/net10.0/ServerLegacyChatTests.dll --modern-probe /home/hunyuan2333/Phinix/Phinix-Rework-Server/Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-p5-audit-mixed-final-20261102
# PASS 独立原版真实模块21+11+16，Framework混合8，共56项网络检查

python3 tools/quality/historical_rework_probe.py --client-source /home/hunyuan2333/Phinix/Phinix-Rework-P3-Business --common-source /home/hunyuan2333/Phinix/Phinix-Rework-P3-Business/Dependencies/Phinix.Common --protobuf-source /home/hunyuan2333/Phinix/Phinix-Rework-P3-Business/Dependencies/Phinix.Common/Dependencies/protobuf --server /tmp/phinix-p5-audit-publish-final-20261102/PhinixServer.dll --config-writer /home/hunyuan2333/Phinix/Phinix-Rework-Server/Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-p5-audit-historical-final-20261102
# PASS 同一最终published Main，历史自动登录/脱敏公开同步/真实服务端重启UUID保留
```

模型seed `20261015`/`20261017`；新通知/生命周期回归采用固定屏障、实际事件/注册状态和精确错误匹配，无随机调度seed。P3故障点 `p3.kill-after-offer-response`、`p3.kill-with-offline-pending`、`config.trade.malformed-section` 保留。

失败前五组组件回归分别复现Item失败继续、日志中断接受结果、大小无法表达、缺依赖、record诊断暴露；另独立进程复现UDP poll崩溃及不支持绑定被接受。Framework以冻结P5实际旧源码和移除清理的源码负对照验证。初次生命周期测试误把ServerAuthenticator当作IDisposable，编译失败，未当成产品回归通过；随后改用真实Stop并重新构建。开发中编译/权限失败与中间stage保留，不把陈旧测试二进制误记作该项故障证明；最终仅完整通过轮次记为通过。

## 兼容性变化、余项与下一步

- UDP默认 `IPAddress=0.0.0.0` 保持可用。指定loopback、具体IPv4/IPv6等地址现在明确失败，因为固定backend不能兑现绑定；需要真正绑定指定接口，须单独升级/替换backend并重审shim。本轮不把过滤远端地址冒充绑定，也不静默改用TLS。不要再依赖旧代码忽略地址的行为。
- 正常明确Continue/不匹配仍可串联；第三方插件不能再用抛异常表达“交给下一个”。错误请求不自动回滚/退款。一个观察者失败仍不妨碍其他观察者。
- wire、持久存储、资产/身份关联和客户端包均不变。旧数据无需迁移，不清库存、账户或原操作结果。
- 原版完整资产交易仍明确拒绝；TCP+TLS实验性、正式身份迁移TODO。历史全Framework/Trade和所有混合版本矩阵未验。客户端/游戏实机不在本轮自动化覆盖内。
- 固定Common的通用raw packet wrapper仍可能输出完整插件异常，任意第三方raw处理器的日志内容控制不是本轮已验证范围；未发现本轮测试泄露真实玩家凭据。后续Common/可观测性切片应覆盖该入口，不能修改固定子模块来伪装已解决。
- Windows/Waylith、ECS运行时10.0.13、容器、长时网络与系统I/O故障仍待专用环境/P6；没有因本轮绿灯宣称这些通过。

审计证据/源码快照/候选位于工作区 `Deliverables/Phinix-Rework-P5-Server-Audit-2026-11-02/`。本候选替代上一P5候选用于后续测试，旧归档不重写。下一步仍为P6冻结联合清单与实机目标；本轮不自动发布、上传或重启云端。
