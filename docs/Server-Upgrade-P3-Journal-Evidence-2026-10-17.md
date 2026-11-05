# P3 中立 SQLite 事务基础首片（2026-10-17）

SV-30/35、TQ-30 的**基础子集**完成。服务端仍基于 `5a0508acb703319e60dd5600e3e0facd0397b294` 的 dirty 工作区，实际源文件/锁文件 hash、命令、退出码与制品见 [机器证据](Server-Upgrade-P3-Journal-Evidence-2026-10-17.json)。正式 Trade 仍使用内存/XML 周期快照，本轮没有宣称交易持久提交、旧数据迁移或游戏重启丢失已修复。

## 实现与边界

ServerStorage 增加协议中立的 `IOperationJournalFactory` / `SqliteOperationJournal`。Autofac 应用作用域装配，插件通过既有 ExtensionHostContext 平等取得服务，无 Chat/Trade 专属启动/恢复分支。factory 延迟创建资源；明确区分 CreateNew 与 OpenExisting，不因缺库自动生成空权威账本，不覆盖原文件。名称限制阻止路径穿越。新 Linux DB/备份文件 0600，schema/application ID 校验及 quick_check 失败拒绝打开；未知 schema 不自动重建。这里只实现 schema 1 初建，后续升级还需显式迁移。

每次操作在 immediate SQLite 事务内按 `(principal,operationId)` 查询：相同 canonical intent SHA256 返回原 bytes，不调用业务；相同键不同指纹明确冲突。回调写入 opaque 状态及绑定 recipient 的 Pending outbox，结果记录在同事务；只有 COMMIT 返回之后 Execute 才返回成功。回调异常、无效结果或超预算回滚整体，超限被调用方捕获仍不能提交部分写入。交易业务的稳定 ID、规范意图/授权尚需后续正式接线，当前不是 wire MessageId 的持久化实现。

回调接口无 SQL/网络能力；可信调用方仍必须禁止外部发送、内存权威变更或异步工作。交易业务应先完成意图和整批输入校验，再在事务内更新状态，COMMIT 后才发布业务结果。事务 handle 到期/跨线程拒绝。本轮没有用这个设施绕过现有引用批次检查。

同实例 writer 不排队/不重入，SQLite command timeout 1 秒；没有应用层自动重试。存储异常使当前实例拒绝进一步写入，需独立检查后重新打开；不能用新 operation ID 或空库恢复未知结果。回调本身执行时长不由设施硬限制，不能宣称完整有界调度器。开发限值：ID 128 字符，request/result/value 各 4 MiB，每事务 128 次 mutation/8 MiB；不是生产容量或全局 outbox 预算。

备份用 SQLite Online Backup API 写入新文件并重新验证，不复制活动数据库文件。新库使用 SQLite 默认 rollback journaling 与 `synchronous=FULL`，无 WAL/checkpoint 优化承诺。未改变旧 XML 的 Load/Save 或创建第二份 Trade 权威。

依赖仅新增固定 `Microsoft.Data.Sqlite 10.0.13` 与锁定 SQLitePCLRaw 2.1.13 传递依赖，用于真实文件事务和在线备份，无 ORM/新测试框架。Server、ServerStorage、Model 所需锁图已更新；固定 Common/protobuf gitlink、vendored SDK/源码及客户端本轮未改。选型资料见 [存储使用说明](../Server/ServerStorage/README.md)，依据为 [官方事务说明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions) 与 [备份接口](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)。

## 实际执行

- SDK 环境仍是 `MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks`，串行 `-m:1 -p:BuildInParallel=false`。实际 restore 在更新锁文件时才使用 `-p:RestoreLockedMode=false`；最终 stage 使用锁定图。NuGetAudit=false 不等于依赖安全审计通过。
- `dotnet build Tests/ServerJournalTests/ServerJournalTests.csproj -c Release --no-restore ...`，`python3 tools/quality/journal_check.py Tests/ServerJournalTests/bin/Release/net10.0/ServerJournalTests.dll`：真实文件事务的相同操作结果、跨主体独立、冲突不调用业务、callback 失效、整体回滚、局部 writer 与实际 SQLite busy、限额、备份/重开、损坏/缺失/未知 schema/命名边界及 Unix mode 通过。
- `journal.before-commit.kill`：测试子进程写状态/outbox 后、回调返回前写 barrier 并阻塞；父进程强杀，重启后独立 SQL 检查 state/result/outbox 均无半笔。`journal.after-commit-before-response.kill`：子进程 Execute 返回后写 barrier，父进程强杀；重开相同 ID 返回原结果，状态和 Pending 收件主体仍一致。这模拟丢失应用响应，不是实际 Trade 网络响应故障。barrier 等待 10 秒、kill 后等待 5 秒，固定调用顺序，无随机调度 seed，无生产故障入口。
- Python 标准 sqlite3 另以只读模式打开合成备份，以独立字面预期核对确切状态 bytes、4 个操作的主体/ID/SHA256/结果、recipient/Pending payload 及 schema/integrity。记录数只证明结构回滚，绝不当作游戏资产守恒证据。
- `python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p3-journal-final-stage-20261017`：最终 **30/30 步、59 制品检查通过**。增加 journal 3 步，保留原控制台、21 安全组件、3×256 独立属性（seed 20261015/20261017）、架构和快照回归。架构将 journal 分类为中立宿主设施，既有 8 项 fallback 债务未增加；publish 检查 managed SQLite 以及 Linux x64 原生依赖。
- `dotnet publish Server/Server.csproj -c Release --no-restore ... -o /tmp/phinix-p3-journal-final-publish-20261017`，复制既有官方 Extensions：最终 `host_smoke.py` **3 项通过**；最终 `admission_probe.py` **2 场景通过**，独立服务端/客户端真实 UDP。具体完整命令由相应 manifest 记录。宿主 smoke 不代表已经激活交易 DB。

执行中保留的失败：初次沙箱还原 NU1301/proxy permission，授权网络还原成功；过早的测试 build 遇到旧锁/cache 写权限，串行 restore 后解决；初始 CA1416 由实际平台 guard 修复。首个 stage 的 Model 锁图未包含 SQLite，NU1004 后 Model 未构建，随后的架构查找失败；更新 Model 锁后最终完整 stage 通过。初次失败不算通过，也没有无限重试或隔离跳过。最终新 journal 目标 build 无警告，既有 protobuf/Trade obsolete 警告未以改 vendor 规避。

## 未完成与下一切片

SV-31/32 的正式交易意图/状态机、报价版本与稳定操作关联、业务状态/结果/outbox 接线，SV-34 旧文件校验导入和切换都还未实施。当前 outbox 只有 Pending 入账原语，没有投递尝试/Unknown/receipt/ACK 状态与分页；不能连接旧 pending 自动补发。首次 DB 缺失和运行中持久卷 generation、完整 durable health/撤回能力、disk-full/存储故障恢复、全局堆积预算仍待后续。

下一切片先做实际 Trade 状态序列化/迁移演练与稳定业务意图事务，再调整提交后发送/outbox，保留旧记录与回退证据；不得长期双写两份权威。旧 Phinix 的明确未发送 Pending/重连尽力交付、可能已发送 Unknown 仍按既定契约实现，不能因底座存在提前宣称已恢复。

本轮无真实 RimWorld 物化、库存、存档或完整客户端插件联合验收，未检验停电/硬件/网络卷/全平台或远端 CI/Docker。当前不需要用户启动游戏；有完整候选及隔离步骤后再通知。P3 继续实施中。
