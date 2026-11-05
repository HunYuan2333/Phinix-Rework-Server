# P3：交易终结空间预留与准入背压（2026-10-22）

任务：SV-36、SV-31/32、TQ-30/31、PF-07 子集。保持 Server-only，核心无 Legacy/V2 分支，无客户端/Common/vendor/gitlink、包依赖或 DB schema 改动。沿用现有控制台测试，不引入新测试框架。

## 缺陷与实施

现有 ledger 整体序列化为一个 SQLite state blob，上限 4 MiB。报价当时能写入，不代表后续终态快照加双方交付记录仍能写入：载荷会保留在 terminal snapshot 和待交付记录中。旧实现没有为这一步预留空间。

在创建和更新报价的领域方法内增加 **3 MiB 序列化准入软门槛**，与现有 4 MiB 硬门槛分开。先构造候选，分别计算当前候选状态，以及全部 active trade 终结并创建双方完整 pending payload 的投影；任一超过软门槛则整批拒绝。投影包含已有 terminal/pending（含 Unknown/弱 ACK），预留 attempt 标记、两次接受的版本增长；为后续元数据和序列化结构变化保留 1 MiB 余量。不是生产吞吐、托管堆内存、磁盘总量或端到端资产预算。

投影只算空间，不执行业务转换、持久化或发送。不修改已有报价/版本/consent，不提前消费合法引用，不淘汰任何已提交记录。成交、取消和 delivery attempt 不经过准入软门槛；其实际事务仍受既有硬门槛约束。新报价以 replacement 候选验证，成功后才替换原报价；缓存引用继续仅在 COMMIT 成功后消费。

容量拒绝使用已有 InternalServerError 编码和明确 FailureMessage，不修改共享枚举。新失败消息随原 operation result 原子持久化，重启重放仍保留；不再把容量创建失败误报成“双方已经交易”，也不把容量报价失败误报成缺引用。拒绝报价的结果不再次保存完整被拒物品，响应不授予退款/重投许可。历史结果没有 FailureMessage 时保持可读取。

已有大于软门槛且小于硬门槛的合法 ledger 不因加载而丢弃或拒绝；测试证明其中可在硬门槛内终结的旧报价仍能取消并持久保留全部交付。不能由此声称旧版本曾接纳的任意大报价都能在 4 MiB 内终结。terminal/Unknown 暂无安全归档，新准入可能持续拒绝；不能靠弱 ACK、删除记录、自动退款或放大上限绕过。后续须按权威归档/receipt 证据设计容量释放。

## 回归和独立预期

L2 正式 Trade service、真实 Framework packet/payload serializer 和真实 SQLite 文件：

- 固定 5 × 256 KiB 合成 opaque 批次，旧实现接受、修复后预留拒绝；原小报价/版本保持，4 项合法引用随后仍可完整使用。
- 4 项报价持久重开后保留；第二笔交易的单项报价因全局投影而拒绝，证明不是只算当前 trade。
- 压力下首笔仍可成交，第二笔仍可取消；拒绝结果跨重启重放；弱 ACK 不释放未知资产预算。
- 从有效文件构造隔离的“旧版本无预留”状态：报价含 5 项，加载不静默过滤；取消后 ledger >3 MiB 且 <4 MiB，新增交易拒绝且 ledger 原字节不变；重启后拒绝和消息一致。
- 最终直接 SQL 读取 XML，独立核对 4 条 recipient delivery、载荷字节合计及硬上限。这只证明 opaque server evidence 没丢，不能证明真实游戏物品守恒。

初始真实回归失败信息为 `Completion reservation admitted an oversized future terminal ledger`，保留修复前构建/测试日志。修复后直接组件运行通过。原 stage 在补充旧版超软门槛输入回归前通过，不冒充最终测试来源；最终隔离阶段另行运行并冻结源码 hash。无新增生产 fault 开关，测试只使用合成 a/b/c、独立临时目录和 DB；手工改 XML/SQL 仅用于测试 fixture，不能作为生产处理步骤。

## 实际命令与证据

工作目录 `/home/hunyuan2333/Phinix/Phinix-Rework-Server`，SDK 10.0.113/runtime 10.0.13。实际子进程设置：

```sh
export NUGET_PACKAGES=/home/hunyuan2333/.nuget/packages
export DOTNET_CLI_HOME=/tmp/phinix-dotnet-113
export MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks
export DirectoryBuildTargetsPath="$PWD/Directory.Build.targets"
dotnet build Tests/ServerTradePersistenceTests/ServerTradePersistenceTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-server-capacity-20261022/final-stage
```

profile：existing-rework-component；容量步骤确定性，无随机调度。既有 stage 模型 seeds：20261015、20261017，保留提交后响应 barrier 的进程强杀/重启、独立 SQL/XML oracle、架构负控制与发布制品检查。新增容量回归本身不是系统磁盘满或断电测试。无新增依赖，未重复声称一次在线漏洞扫描；构建使用既有缓存、阶段 NuGet audit 关闭，安全审计结果仍见独立安全记录。

最终隔离阶段 **33 步全部通过**，实际组件输出含 capacity-reservation；受影响最终源码与阶段 snapshot 哈希逐项一致。最终结果、dirty HEAD 与逐文件 SHA256 见配套 JSON、交付目录；未保存任何玩家数据、凭据或构建产物到仓库。

## 尚未完成

SV-36 仅完成全局序列化 ledger 的保守准入子集，仍缺每主体配额、分页、增量账本、受证明的安全归档及 operation result 表总磁盘预算。两次投影序列化和候选复制发生在现有同步事务路径，未测生产 CPU/GC/p99；不能称为性能优化或支持人数承诺。超软门槛不会停止已有安全终结，但超硬门槛旧数据的恢复需要后续专门迁移，不能自动回退旧 XML。

P3/P6 保持实施中。下一步继续服务端分页/积压与旧数据恢复边界，客户端持久意图/Unknown 查询及 durable game receipt 仍为独立联合依赖。本轮未部署云端、未推送或发布，未执行新 UDP/真实游戏/双边存读档验收；联合候选具备后再通知用户黑盒。
