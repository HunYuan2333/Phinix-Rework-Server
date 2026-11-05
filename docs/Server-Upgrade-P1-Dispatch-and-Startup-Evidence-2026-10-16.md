# P1 发送结果贯通、pipeline 异常保护及启动检查（2026-10-16）

本轮合并推进 SV-13、SV-28、SV-29 的必要子集，对应 TQ-10/TQ-21、FR-08/FR-13。基于 Server `5a0508acb703319e60dd5600e3e0facd0397b294` dirty 工作区；每文件输入、仓库/gitlink、命令/退出码、seed、制品 hash 见 [机器证据](Server-Upgrade-P1-Dispatch-and-Startup-Evidence-2026-10-16.json)。没有提交、推送、部署或改 sibling 仓库。

## 完成内容和不变量

**SV-13：** ServerRuntime 增加可选 `IResultServerPacketDispatcher` / 不可变 `PacketDispatchResult`，由中立 ExtensionHostContext 提供给任意插件；报告原目标、改目标后实际 handoff 结果及整体不确定性，不把重定向后的发送误算到原主体。ServerRuntime 新增对中立 Connections.Server 的工程引用以复用 TransportSendOutcome，不增加包或改变固定 Common ABI/gitlink。Stateless/Autofac 精确版本不变，业务插件与宿主无新增业务类型耦合。

Framework、unicast/broadcast、dispatcher 使用新结果路径；原 void Send/Configure 保留，void callback 只能报告 Unknown。能力筛选、拦截阻止为 NotSent；发送开始后错误为 Unknown。拦截器的 delivery callback 也过同一能力 gate；同一目标重复 handoff 不发送第二份；任一拦截/predicate 未知副作用终止处理，报告不能证明未发送，未列目标也不能在整体失败时误读为安全未发送。报告发布与 handoff 使用同一锁，发布后 scoped delivery callback 关闭；拦截器保留的晚到调用明确不发送，异步插件必须另外发起一次 dispatch 取得新结果。结果服务不自动重试、换协议或退款。旧 Common SendMessage 仍为 void，插件需要显式选用新的服务取得报告；这不是给旧协议补造 ACK。

**SV-28 子集：** message/command 的 Can* predicate 在各自 request gate 内执行，抛错拒绝继续该请求，不跳过权限判断去尝试下一个候选；interceptor/handler 抛错后不继续第二个业务处理器。终止通知使用 StopPropagation，未生成“业务未变更”证明或成功/失败退款回执。后续独立请求可继续；新异常日志仅含类型。原 observer 隔离逻辑保留。item handler 的未知副作用策略和旧 void outbound 路径的历史异常行为尚未整体迁移；真实宿主出站已切到严格结果路径，原控制台旧 API 回归保持可执行。

**SV-29 子集：** Config 在反序列化后校验端口/连接容量/保存间隔/显示名长度；非法值明确失败，显式 Port=0 保留作临时端口。装配根也校验程序构造的 Config。NetServer 检查固定 LiteNetLib Start 的 bool，失败立即抛 IOException，不启动 poll worker；日志使用实际 LocalPort，避免 Port=0 还显示监听 0。

固定 DLL 只提供 wildcard Bind(int,bool)，不支持配置指定 IP。正常宿主现在仅接受 Address=0.0.0.0；其他限制地址明确 NotSupportedException，不再静默扩大监听范围。测试写配置工具改用通配地址。**需要只监听 loopback/某网卡的现有部署不能直接使用此版本**，须先更换明确支持的传输实现或在后续授权范围实现绑定；没有修改 vendor、绕过 SDK pin 或声称已经实现限制地址绑定。通配地址也不代表仅 IPv4，底层地址族行为未新增承诺。

## 实际验收

- 先加入配置与端口冲突预期：`dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --config-validation` / `--send-network` 修复前均非零（134），分别捕捉非法 Port 仍加载、Start 失败仍报告成功；未把错误行为改成预期。
- 串行 build：`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet build Tests/ServerLifecycleTests/ServerLifecycleTests.csproj -c Release --no-restore -m:1 -p:BuildInParallel=false` 通过。工程引用改变后分别对 Server 与 Model 做 `dotnet restore <project> -m:1 -p:BuildInParallel=false -p:NuGetAudit=false -p:RestoreLockedMode=false` 更新工程图，包版本未新增/调整。
- 原 Lifecycle 默认入口新增 dispatcher/插件服务解析、能力筛选、拦截阻止、改目标/重复目标、拦截已发送后抛错、各接收者独立结果、predicate 失败、handler 部分副作用后停止与后续独立请求、敏感异常不进入新日志等检查；保留原 8 项 transport 结果和 6 组 lifecycle。配置另有 5 个非法范围、Port=0 保留、限制地址拒绝检查。
- `dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --send-network`：真实 UDP/临时端口、实际 module+bytes、断线结果、端口占用时无 worker，全部通过。这不是独立进程现代 profile Harness 或游戏 ACK。
- `python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-dispatch-scoped-stage-20261016`：**27 步全部通过，33 个制品检查通过**。含原 Server/Common 控制台、21 项安全组件、3×256 独立模型属性（seed 20261015/20261017）、架构（8 处既有债务未增加）、新增目标检查及快照/强杀已有回归。
- 前序 stage 也通过，但审查又补了 callback 能力 gate、全局未知状态及报告发布后 callback 关闭，最终再次按完整输入构建；证据中前者明确作废为最终候选证据，未把旧输入通过套用到新代码。最终输入 hash 与工作区一致。
- 发布：`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet publish Server/Server.csproj -c Release --no-restore --no-self-contained -m:1 -p:BuildInParallel=false -o /tmp/phinix-dispatch-scoped-publish-20261016`，复制四个官方插件。
- `python3 tools/quality/host_smoke.py --server /tmp/phinix-dispatch-scoped-publish-20261016/PhinixServer.dll --test-tool Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-dispatch-scoped-host-20261016`：3 项通过。相同参数使用 host_load_smoke.py、output=/tmp/phinix-dispatch-scoped-load-20261016：6 项通过，损坏状态拒绝且原文件 hash 不变。

只有本地 Linux 自动化与发布宿主证据；未跑远端 CI/Docker、fresh cache、coverage、Windows 或游戏客户端。所有身份、payload 和状态文件均为隔离合成数据；无生产故障控制接口。

## 阶段结论与下一依赖

按 RoadMap §14 用户确认的加速门槛，P1 最小前置已满足：整批/公开字段保护、缓存准入/发送分类、互斥保存/正常停止、加载失败与启动错误保护均有本地回归。不是 P1 全表清零；TTL/断线回收、epoch/operation 绑定移入 P2 会话切片，item 未知副作用和完整 worker/log/scheduling 矩阵仍需按实际路径扩展。

下一阶段是 P2 的 session/epoch/Ready 中立事件和关联拒绝控制边界，再进入 P3 状态机/真实 SQLite/operation/result/outbox 同事务及重启恢复。当前业务仍存在周期快照与成功回执之间的持久提交缺口，**不能用于宣称交易已经可靠持久化**。旧端尽力交付和未知保留策略不变；真实 RimWorld 物化、库存、存档与交付仍待联合验收。
