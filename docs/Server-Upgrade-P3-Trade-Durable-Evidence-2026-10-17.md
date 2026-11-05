# P3 正式 Trade 事务保存与重启恢复切片（2026-10-17）

实现 SV-31/32/33/34/35/38、TQ-30/31 的声明子集，P3 仍实施中。正式宿主的 Trade 已从周期 XML 权威切换到逐操作 SQLite 事务；完整源码/配置/锁图 hash、基础 SHA、命令、退出码、故障及制品见 [机器证据](Server-Upgrade-P3-Trade-Durable-Evidence-2026-10-17.json)。客户端产品未改，本轮仅扩展其独立测试项目；原有各仓库 dirty 修改保留。未部署或推送。

## 产品实现与不变量

正式插件的持久 Load 在监听之前运行，打开/导入 SQLite。每个有效 create/offer/status/ack 通过已认证稳定 principal 与原 Framework `MessageId` 关联操作，指纹来自种类及 typed canonical payload，不包含 session、connection 或凭据。重复请求重放原结果，冲突请求抛明确 OperationConflict，停止本次处理；不会把旧成功改成失败/退款，也不会再次发出完成资产事件。现代客户端若要应用重试，必须保留该 MessageId；本轮没有替客户端建设持久意图日志。

事务从权威 bytes 构造独立候选，完整验证后，在同一 SQLite transaction 写入报价/确认/终态/收件方 outbox 与操作结果；只有 COMMIT 后才更新内存视图、消费合法引用和发送成功。失败批次不消费引用。新增混合 inline Items 与 refs 拒绝，避免此前忽略 inline 内容。原有历史 inline 入口仍是 P5 Adapter 迁移债务，本轮没有新增协议特判。

本轮物理布局是 `state.trade-ledger-v1` 的完整业务聚合 XML blob，包含 ActiveTrades、TerminalTrades、recipient PendingCompletionNotifications，结果/指纹在 operations 表。outbox 是聚合内的权威交付记录，**不另用通用 outbox 表维护第二份 Trade 权威**；同事务保证不依赖周期 Save。整份状态及每结果现有 4 MiB 上限，因此不是可扩展分页布局或生产容量承诺。终态保留，active 移除不丢双方报价和交付数据。

新交付具有稳定 DeliveryId、recipient、payload；离线或未 Ready/未协商所需能力时仍为 Pending。发送前先以事务记录独立 AttemptId 并转为 OutcomeUnknown，再进入网络。重复 Ready、同操作重放或进程重启均不能盲重发 Unknown；保守窗口可能记录已尝试但实际未发送，当前没有 NotSent 自动回 Pending。现有弱 ACK 只持久记录 AckObserved，不提升为 Confirmed、不删除 payload。没有伪造游戏保存证据或自动退款。

恢复订阅属于 Trade 插件：Ready 后发安全快照并尝试确切 Pending；宿主没有业务专属分支。核心业务存储不判断 Legacy/现代 profile，也不接收旧 credential 字段或兼容开关。

## XML 导入与权威切换

本轮只覆盖**当前 Rework Trade 的历史 XML 快照**，并非原版 Phinix protobuf/全部导入矩阵。保存原 `trade-state.bin` bytes，整批验证身份、参与者、版本、重复交易/接收者及 opaque payload，不过滤坏记录。创建 `.sqlite3.importing`，在事务中写完整状态、源 SHA256 及导入结果，随后原子发布 `.authority` hash 标记，再 rename 已提交的候选为 `.sqlite3`。

DB/标记只存在一个或导入候选残留等异常一律失败关闭，保留证据；不重新导入旧 XML 生成空/过时账本。源 hash/标记验证后才接受权威；源不再继续更新，Save 只核对切换证据。旧 pending 缺乏可靠尝试历史，导入时全部保守设为 OutcomeUnknown，不能猜测未发送。失败 Load 不允许退回未初始化内存写入。`framework-extensions/` 已加入忽略规则。

启动期间无监听，因此导入处于停写边界；中途崩溃可能留下 marker/candidate 并阻断下次启动，需要检查保留证据。本轮没有自动灾备/恢复该窗口或已验证的反向迁移。新 DB 写入后，不能部署旧 binary 并读保留 XML；旧文件只作封存导入/备份证据。

## 联动修复

真实强杀会暴露另一前置：原凭据/UUID 映射依赖周期保存。现在，已 Load 持久路径的 UserManager 在发布成功登录/连接映射之前，按顺序保存 credential 和 user 快照；失败明确拒绝登录，不发 Ready/业务权限。仍保持自动无密码、同 key 同 UUID，凭据不进入 Trade 核心。两个文件不是一个 SQLite 事务，但没有两份快照都完成前的成功登录/资产接纳；不承诺断电/跨卷一致恢复。未 Load 的旧组件 API 保留内存契约，仅用于既有无宿主组件检查，不能据此宣称持久登录或交易。

独立客户端还捕捉到另一方收到请求方 session 的既有发送缺口：修复前真实 UDP 检查退出 1，白名单诊断 `Cross-recipient envelope`，不输出 bearer。中立发送边界现在复制 packet 并使用实际接收者当前 Ready session；协商/control 未 Ready 时只允许匹配认证 session。原 packet 不被改成另一方 bearer；这是通用 envelope 保护，无 Trade 特判。逐目标二次编码暂未做性能优化。

## 实际命令与证据层级

环境已更换为 SDK **10.0.113**，命令使用 `MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks`、`DOTNET_CLI_HOME=/tmp/phinix-dotnet-113`、`NUGET_PACKAGES=/home/hunyuan2333/.nuget/packages` 和串行 `-m:1 -p:BuildInParallel=false`。没有修改 vendor SDK pin。缺失目标引用包经授权 restore；没有新增业务包或空缓存/漏洞审计通过声明。

| 层级 | 实际入口 | 结果 |
|---|---|---|
| L2 真 Trade/SQLite | build `Tests/ServerTradePersistenceTests`；`python3 tools/quality/trade_persistence_check.py Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll` | 真实序列化/业务/文件 DB 的导入、重启、缺批、幂等、冲突、回滚、终态/outbox、离线/Unknown/弱 ACK 和受控强杀通过 |
| 独立持久预期 | 上述 wrapper 的 Python sqlite3 + XML 读取 | 核对原始状态字节结构、双方终态/recipient/独立 ID/Unknown/ACK evidence、操作记录与 source hash；不将记录数当游戏资产证据 |
| L1/L2 阶段 | `python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-trade-durable-final-stage-20261017` | **33 步/59 制品通过**；22 安全组件、3×256 模型 seed 20261015/20261017、架构原 8 处债务及原控制台/保存检查保留 |
| L3 声明子集 | `tools/quality/trade_network_probe.py`，published Server + 客户端仓库独立 probe | **2 peer、3 阶段通过**：真实自动登录/Hello/UserUpdate/建交易/ref 报价；强杀后重启原 UUID/报价/原结果恢复，无 refs 补造；运行中身份快照路径失败拒绝登录 |
| 发布宿主 | `host_smoke.py` / `host_load_smoke.py` | **3+9 项通过**；新增 DB 损坏、DB 缺失、marker 缺失均监听前拒绝、原文件不被修改；封存 XML 不被最终保存重写 |
| UDP 会话回归 | `dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --ready-network` | 身份/版本/关联拒绝、过期/重连、正式插件快照及 ambiguous pending 保留通过 |
| 客户端控制回归 | 独立 probe 默认 `--server-fixture Tests/Fixtures/FrameworkControl/normalized.json` | 原 **17 项通过**；客户端产品组件未修改或重新宣称完整游戏构建验收 |

发布入口 `/tmp/phinix-trade-durable-publish-20261017/PhinixServer.dll`，由 `dotnet publish Server/Server.csproj -c Release --no-restore ... -o <path>` 及复制官方 Extensions 得到。所有完整命令/输入输出 hash 在机器 manifest；运行数据与 synthetic key 文件只在临时目录，并最终删除，不加入证据制品。客户端 Common 仍 `c165f61d...`，服务端仍 `67f243d...`，两端 protobuf `4b0c3aa...`，未移动 gitlink。

确定性故障：`trade.operation.insert-result.abort` 是测试 DB 的专用 SQLite abort trigger，确保状态写后、结果写前全回滚；`trade.commit-before-response.kill` 在真实业务 COMMIT 后、测试发送回调前 barrier 强杀子进程，重开取原结果；`trade.server.kill-after-offer-response` 是独立发布宿主真实 SIGKILL，发生于 60 秒 timer/最终保存前；`identity.snapshot-save.path-directory` 为真实 OS 路径异常。没有普通客户端可触发的生产 fault hook。新 Trade 场景无随机 seed，固定操作顺序；模型 seed 不冒充故障调度。

保留失败：最初 SDK 首次运行目录/目标包限制及工作区旧测试 deps 缺 SQLite 经明确还原/重建处理；未跳过回归。第一个 probe 因真实历史 Login 不应用 AcceptingTrades 而拒绝建交易，已修正测试发送真实 UserUpdate，不改变服务端缺省；随后跨 recipient envelope 真实反例确认并修复。最终 stage 与发布/网络检查在产品修复后通过。早期通过的阶段不作为后来修改的最终证据。

## 剩余和下一依赖

本轮证明服务端**已接纳的报价/交易**在声明的进程重启范围持久保留，不证明客户端游戏重启后的物化/托管/存档窗口。下一前置是确认意图绑定明确的已观察报价版本、现代端稳定操作日志/未知结果查询及 UI，再完善候选包。当前旧 request 没有该版本字段，迟到的旧确认保护仍未完整，不能标全部交易状态机/资产语义通过。

没有 modern durable receipt/Confirmed、全局操作/outbox 生命周期预算或分页、完整 durable health/卷 generation、断电/磁盘满/硬件/网络卷矩阵；Whole state 4 MiB 是开发保护，超过时拒绝新提交，不删除已保存记录。冲突/存储异常当前停止且不发伪造失败/退款许可；客户端显式 Unknown 展示/查询仍待补齐。

原版 Phinix server Adapter、未升级 Rework 全 profile、旧 token/字段迁移、客户端 GameSave/库存/真实 RimWorld 收件证明及完整插件打包未验。本轮黑盒是自动 Headless 协议子集。**目前不需要用户人工测试；完整联合候选和隔离用例准备好时再通知。** P3 与 P6 不整体验收。
