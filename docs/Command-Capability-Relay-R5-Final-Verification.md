# R5 最终白盒核对与分支交付

日期：2026-11-10。用户已反馈黑盒全部通过，并明确授权交叉兼容、压力场景全部以白盒通过作为本次验收标准，完成后推送 GitHub 远端分支。本次不以真实旧发行包部署或实机长时间压测作为额外门槛。

## 实际通过的验证

| 范围 | 结果与边界 |
| --- | --- |
| 新旧组件兼容矩阵 | 四种客户端/服务端组合 × JSON/protobuf 两种载荷；使用冻结旧报文定义、冻结旧 ServerPipelineRunner、当前序列化器、当前客户端 Relay 服务及当前服务端 Pipeline。普通命令、未知命令、Observer、请求关联及旧服务端 Relay 拒绝均通过 |
| P01 持续路由 | 注入时钟推进 1800 秒，18000 请求、27000 逐连接结果；状态表大小稳定。这是虚拟时钟压力，不是实机 30 分钟浸泡 |
| P02 慢接收者 | 注入受限投递边界；1000 次部分提交均保留健康接收者进度，解除限制后恢复 |
| P03 广播放大 | 多发送者耗尽全局字节预算、拒绝发生在交付之前、补充预算后恢复；129 接收者广播整体拒绝，无静默截断 |
| P04 生命周期 | 100 轮撤销、断线和重连，旧 epoch 拒绝；最终 entries/index/controlBudgets/commandBudgets 四张表均为空 |
| P05 请求与收包洪峰 | 64 条等待结果上限、超限发送前拒绝、超时全部 Unknown、清理后恢复、无业务自动重发；实际 net35/netstandard 分片与收包水位回归通过 |
| UDP 出站积压 | 5 个真实本机 UDP 连接，在固定 LiteNetLib 后端检查 2048/连接、8192/全局积压拒绝；真实 ACK 排空后可继续 Relay |
| TLS 出站积压 | 注入会话边界，运行实际有界 Channel 与生产 admission；16 帧、4 MiB 字节上限、超大载荷拒绝、排空恢复通过 |
| 服务端阶段回归 | 50 项通过，含真实 UDP Relay、TLS/身份、聊天、交易、持久化、架构与进程恢复 |
| Pipeline | 21 组、628 条断言通过 |
| 客户端 | 精确暂存内容导出后，源链接回归 7 组、97 条断言；实际 net35 收包预算与生命周期通过（另含 18 条线程所有权断言）；聊天 20 场景通过 |
| Example | 最终候选 Host 上 140 条断言、五轮激活/关闭通过 |
| 构建 | 精确暂存客户端与 Example 在 1.6/net472、1.7/net10.0 下均构建通过 |

## 共享引用与工作区范围

两端 Common 精确引用统一为 911047cf8a6dd34e661e107e182e9c921b8e9586。以客户端现有 c165f61 为后续能力基线，合并服务端 67f243d 基线上的 Relay 提交；保留两条来源历史，统一后的服务端阶段回归通过。未改 vendored protobuf 固定引用。

客户端按本次任务范围暂存 Relay、必要的传输线程所有权保护、测试、示例和文档。既有回调隔离、库存、商店、UI 和历史文档整理等工作区修改保留；没有全部打包提交。暂存源码单独导出至 /tmp/phinix-relay-r5-final-source/client 后编译和测试，避免依靠未提交改动才能构建。

2026-11-09 用户黑盒候选来自当时完整工作树；最终分支提交筛选了本任务范围，故不声称用户逐字节验收过最终提交。两者的 Relay 逻辑一致，最终提交范围已独立完成上述白盒与双版构建。

Example 继续使用独立分支 codex/command-relay-test-panel；该候选分支的 ci/config.json 固定到实际 Host 源码提交 9c0c5aecd8dd6214bf4d6427f0879fc74de2d28e，使远端候选可取得对应 Relay 契约。main 正式发行配置、产品版本和索引未更新。本次不发布 GitHub Release、不打 tag、不推送 main、不替换已交付 ZIP、不重新部署测试服务器。

## 可复核入口与证据

- 服务端 Tests/Phase35ServerRuntimeTests/RelayAcceptanceTests.cs；Fixtures/LegacyServerPipelineRunner.cs 头部记录冻结源提交与原始摘要，只隔离类名。
- 服务端：python3 tools/quality/check.py --suite stage --isolated --output /tmp/<fresh-output> --timeout 120。
- 最终阶段证据：/tmp/phinix-relay-r5-final-stage-20261110-c/manifest.json。
- 双版本构建：/tmp/phinix-relay-r5-final-build16/results.json、/tmp/phinix-relay-r5-final-build17/build.log。
- 客户端：/tmp/phinix-relay-r5-final-client-tests.log、/tmp/phinix-relay-r5-final-transport-lifetime-c/manifest.json、/tmp/phinix-relay-r5-final-chat-regression-c/manifest.json。
- 持久化的交付摘要与源码指纹见同目录 Command-Capability-Relay-R5-Delivery.json。

按用户本次标准，R5 验证通过；分支交付以 GitHub 远端引用核对为准。真实实机 RSS 峰值、长时间浸泡和真实旧版本进程混部未作为已执行项报告。
