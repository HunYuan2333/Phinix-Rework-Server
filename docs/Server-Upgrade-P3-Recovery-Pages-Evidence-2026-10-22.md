# P3：有界待交付恢复批次与宿主中立工作队列（2026-10-22）

关联 SV-33/36、TQ-30/31、PF-07、FR-09/11/12 子集。继续 Server-only。无 Common/client/vendor/gitlink、NuGet 包或数据库 schema 改动。

## 已实现

原 HandleUserLoggedIn 会一次深拷贝同主体全部 pending/Unknown，并同步遍历；立即交付和每次 TryBeginDelivery 也会先复制该主体整个列表。新增回归构造 17 条合法离线待交付记录，旧实现一次发送 17 条，以 `Recovery callback sent an unbounded backlog instead of one page` 明确失败。

现改为每批最多 **8 条尚未尝试的 Pending**，按持久 DeliveryId 的 ordinal 顺序续读；OrderBy/Take 在 Clone 前，仅深拷贝本页。立即交付及 attempt 使用单项查找，移除它们的全主体深拷贝。Unknown 和弱 ACK 记录仍保留，不被分页恢复补投。

新增中立 `IServerWorkQueue` / `ServerWorkQueue`：Autofac application scope 持有，所有插件经相同 HostContext 取得。默认排队上限 1024、每次轮询最多执行 4 个已有回调；回调中新排入的工作留到下一轮，FIFO、不重入。NetServer 的中立 PollCompleted 事件接入队列，在既有 poll worker 上执行；不引入 Trade 专用线程、timer、宿主恢复分支或插件优先权。回调异常隔离并只报告异常类型，队列释放时拒绝新工作、取消尚未执行回调。

Trade 插件每页结束排入下一页，保留原 recipient、connection/session epoch 与插件恢复 generation。执行页前、每次发送前重新核对当前 Ready；断线、旧 session、换 epoch 或 Shutdown 后不继续尝试尚未发送的记录。Shutdown 取消本插件 continuation，不停止其他插件队列。首个 Ready 回调处理一页，后续轮询自动续页；既有消息和 capability 未改变，无需给旧端补造 ACK。

每条仍先用原 SQLite 事务将 Pending 变成 OutcomeUnknown、提交 attempt，再发送原完整批次。发送失败或响应未知不自动重试/退款；失败后尚未尝试的记录继续保留 Pending。队列满或旧组件宿主没有队列时，记录明确警告，停止续排；不退回同步无限循环。此时需后续 Ready/重连再次启动恢复，未承诺队列持续饱和时自动进展。

分页游标是持久证据：同主体相同 DeliveryId、Pending 缺失/空 ID、超过可构造 attempt operationId 的长度均拒绝加载，不静默过滤，否则游标可能跳过合法资产。历史未带 ID 且处于未知/旧 schema 的记录仍按已有导入语义保留为 Unknown；不猜测未发送。

## 测试与证据

沿用真实组件控制台，无新增框架。L1/L2 加本机 UDP transport 集成：

- 17 条 Pending 连续按 8/8/1 发送，17 个不同 tradeId；每次 Drain 只推进一页。
- 第一页后断线：余下 9 条持久 Pending 不被消费；过期 session 无动作，重连继续 8 条。
- 插件取消 queued recovery 后最后一条保留，重开组件后仅尝试这一条，既有 Unknown 不重发。
- 队列满明确告警，无同步退化；后续发送抛出受控 IOException，再重开不会把已尝试记录当 Pending。
- 直接修改隔离 XML/SQL fixture 的两条游标为重复/空值，加载明确失败；恢复原测试输入后仍能交付两条，无过滤/覆盖。不是生产数据库修复操作。
- 队列容量、FIFO、重入、下一轮推进、异常脱敏、Dispose 和真实 NetServer poll worker 执行检查。
- 既有 Ready network 集成通过：真实 auth/login/hello/version/replay/conflict/expiry/reconnect/plugin snapshot、ambiguous delivery held/correlated control；使用合成身份和本机临时 UDP，没有真实游戏 ACK。

受限沙箱第一次 socket 测试报 Permission denied，直接生命周期及该次 stage 保持失败；在允许本机临时 UDP 的执行环境中完整 stage 通过，没有跳过测试、反复重试掩盖 flaky。首次有网络权限 stage 通过后，补充游标校验并再次执行最终阶段。质量脚本帮助文字同步改为说明需要临时 UDP，不能继续声称无 listener。

实际命令（Server 工作目录；子进程使用 SDK 10.0.113/runtime 10.0.13，CLI HOME `/tmp/phinix-dotnet-113`，NuGet 缓存 `/home/hunyuan2333/.nuget/packages`，DirectoryBuildTargetsPath 指向 Server 自有 targets）：

```sh
dotnet build Tests/ServerTradePersistenceTests/ServerTradePersistenceTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll
dotnet build Tests/ServerLifecycleTests/ServerLifecycleTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll
# 以下需要允许本机 UDP socket
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-server-recovery-pages-20261022/final-stage
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --ready-network
python3 -m py_compile tools/quality/check.py
python3 tools/quality/check.py --help
```

profile 为 existing-rework-component / existing-rework ready network；分页步骤确定性，无随机 seed。既有模型 seed 20261015/20261017、提交后响应 barrier/强杀恢复及 SQL/XML oracle 随 stage 运行。未新增普通客户端可触发的故障入口。发送异常为受控测试异常，不能称进程强杀；原强杀测试也不等于断电。新队列的真实 poll worker 检查是同进程组件集成，不冒充独立进程 L3。

最终隔离 stage **33 步全部通过**，正式 Trade 输出含 recovery-pages，生命周期输出含 WORK QUEUE RESULT；受影响最终源码逐项匹配阶段 snapshot SHA256。最终版重新构建后的 Ready network 检查也通过。最终检查数、受影响源码哈希和 dirty HEAD 见配套 JSON/持久交付证据。没有玩家身份、凭据、日志或游戏 DLL 入库，未发布构建产物。

## 联合版本及剩余限制

本轮仅 Server 内部依赖：宿主/ServerRuntime/Connections.Server 提供新的中立队列，Trade.Server 使用它，必须以同次完整 publish 的服务端程序集闭包部署；不能单独拷贝新 Trade.Server 到旧宿主。客户端和共享层无需为本批次调度改包。当前未部署本轮代码，也未改云端镜像或挂载数据。

SV-36/P3/P6 保持未完成：仍是单个 4 MiB blob、启动全量加载与内存列表排序扫描，不是 SQLite 行级分页；全量 active snapshot 也未改。8 条是回调计数限制，一条交付保持完整批次，不拆资产，不声称严格网络字节/p99/堆内存预算。每主体配额、安全归档、operation result 表磁盘预算、完整系统故障及独立进程分批恢复网络 trace 仍缺。队列不能抢占耗时插件 callback；下一切片继续主体预算/存储分页与稳定意图/modern receipt 的联合依赖。真实游戏资产/存读档验收未执行，联合候选就绪再通知用户。
