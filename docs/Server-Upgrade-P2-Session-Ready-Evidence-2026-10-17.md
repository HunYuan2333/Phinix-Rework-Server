# P2 会话绑定与中立 Ready 首个切片（2026-10-17）

SV-20/21/23/25、TQ-20/21 和 FR-09/10 的声明子集。Server 基础 `5a0508acb703319e60dd5600e3e0facd0397b294`，dirty 源码 hash、命令/退出码、仓库/gitlink、制品及失败记录见 [机器证据](Server-Upgrade-P2-Session-Ready-Evidence-2026-10-17.json)。P2 仍实施中，不能宣称现代安全身份或交易交付验收。

## 实施与安全边界

新增中立 ServerSessionRegistry：Negotiating → Ready → Rejected，连接关闭移除绑定；绑定 connection、稳定 UUID、session、固定 Version=2 与不可变能力交集。epoch 为每次新连接绑定的独立随机值，当前只在服务器内部使用。只在本次能力响应 Submitted 后发布一次 Ready；重复等价 Hello 仍可回能力响应，保留原 epoch，不重复执行 Ready 订阅。换 UUID/session/能力集、非法或超量能力、不支持版本不能改写原绑定。Ready 是本地协商决定，不是客户端 ACK。

IServerSessionReadiness 在插件发现前注入 ExtensionHostContext，任意插件平等订阅，单订阅者异常隔离且不记录异常载荷。入站必须匹配 Ready 绑定，出站能力 gate 重新检查当前认证与登录主体；观察到过期/失效的 epoch 不会因后来又有效而自行复活，必须断开重建。没有等待 transport 偶然断开才禁止已过期主体收包。

真实网络补测发现，旧 Auth 允许同连接换凭据重新认证，而 UserManager 登录映射尚未改变，单独检查 IsAuthenticated+IsLoggedIn 会保留旧 UUID 权限。修复新增 UserManager.IsAuthenticatedPrincipal，把当前认证映射核对到稳定 UUID 再与连接登录记录比较；credential key 留在既有身份边界内部，不进入 registry/业务 API/日志。还移除了 Framework 拒绝 command 日志中原有 session 值。

Trade.Server 新增对 ServerRuntime 的工程引用，仅使用服务器中立 Ready 契约，插件不引用宿主程序集。Trade 改在 Ready 后恢复**报价状态快照**，不再登录时提前尝试发出恢复事件；新宿主/ServerRuntime/Trade.Server 需同候选一起使用。Common gitlink、wire DTO、客户端和包版本未改。

现有 pending completion 不区分从未发送/可能已发送，直接接到 Ready 会造成盲重发。因此本轮没有启用自动资产补投：这些记录保留，不能用快照恢复替代到账。未来 P3 必须先引入持久 dispatch attempt / OutcomeUnknown / recipient receipt 和迁移规则，再让明确未发送的 outbox 订阅 Ready。测试中的空取消通知只检验不误删/不重放，不证明游戏资产守恒。

## 实际执行及证据层级

- 显式串行 restore Server 和 Model（`dotnet restore <project> -m:1 -p:BuildInParallel=false -p:NuGetAudit=false -p:RestoreLockedMode=false`）更新 Trade→Runtime 工程图；没有新增包版本。生命周期控制台仍可执行，新增 registry 独立预期，不新建测试框架。
- `MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet build Tests/ServerLifecycleTests/ServerLifecycleTests.csproj -c Release --no-restore -m:1 -p:BuildInParallel=false`：最终通过。阶段内一次 Phase 重构残留 Entry.Ready 导致编译失败，已修复，记录保留，不算通过。
- 默认控制台 `dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll`：新会话绑定/交集冻结/重复 Ready/冲突拒绝/订阅异常/过期不能复活/重连旧 epoch 失效预期与原保存、发送、配置、pipeline 检查全部通过。
- `dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --ready-network`：固定真实 LiteNetLib 回环 UDP、真实 Auth/User/Framework 和正式 Trade 插件，认证与登录后未 Hello 不 Ready；错身份/错版本不通过；能力响应先于恢复快照；重复 Hello 不重复恢复；冲突能力不覆盖；控制过期后出站 NotSent；同 key 重连 UUID 保持、epoch 更新；旧 pending 未被盲重放；同连接重新认证不得保留旧主体权限。客户端使用实际 Common protobuf/Framework 编码，不由服务器 Adapter 生成预期。
- 同连接重新认证的反例在修复前实际退出 **134**：`Credential switch retained old principal authorization before relogin`；修复后同入口通过。其他正常路径的较早 stage 已通过，但不作为该缺陷修复后的候选证据。
- `python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p2-ready-final-stage-20261017`：最终 **27 步全部通过，33 个制品检查通过**。包括 21 项真实安全组件、3×256 独立属性（seed 20261015/20261017）、原 Server/Common 控制台、架构（8 处既有债务未增加）、生命周期/快照已有回归；最终输入 hash 与工作区一致。
- `dotnet publish Server/Server.csproj -c Release --no-restore --no-self-contained -m:1 -p:BuildInParallel=false -o /tmp/phinix-p2-ready-final-publish-20261017`（相同 MSBuildSDKsPath），复制 build Extensions 后：host_smoke.py 对发布入口 **3 项通过**，host_load_smoke.py **6 项通过**。完整参数在机器证据；均使用原生命周期工具生成隔离配置，不读取玩家状态。

受控 faultId：auth.expire-before-outbound、auth.identity-switch-before-relogin、session.ready-subscriber.throw。过期用测试私有反射修改合成 session；没有生产 fault 命令，没有强杀或真实系统故障声明。网络检查是同进程独立对象经真实 socket，**不是独立进程 published Server 协议 Harness**；发布宿主 smoke 也不能替代协议/游戏检查。

## 剩余与下一依赖

当前只支持既有 Framework Version=2；尚无新版 bootstrap 的版本范围/feature-schema 协商、关联拒绝控制通道及客户端明确处理。拒绝当前仅在服务器 gate/日志中完成，历史客户端的超时 fallback 尚未联合升级，不能把本轮称为完整现代 profile。客户端运行目录仍有其他未提交工作，本轮未改动或宣称其组合兼容。

SV-25 只完成入站/出站有效性 gate：完整 presence 清理、凭据撤销/轮换 API、已授权在途业务排空仍未做。TTL/断线缓存回收、operation 绑定留待后续；epoch 不是持久 operationId。没有 SQLite/业务结果/outbox 同事务，没有真实 RimWorld 物化、库存、存档或持久交付验证。端口/地址限制沿 P1：宿主只支持通配地址配置。

下一切片先补 P2 关联拒绝/控制通道、客户端处理和独立进程最小网络证据，再推进 P3 持久交易状态机/SQLite 与明确未发送的恢复。P0/P1 加速最小前置状态保持，P2 不整体退出。
