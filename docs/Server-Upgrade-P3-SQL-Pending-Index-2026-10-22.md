# P3 SQLite Pending 索引与运行中权威修订保护

2026-10-22；SV-33/36/37/38、TQ-30/31、PF-07、FR-12。服务端基准 SHA `5a0508acb703319e60dd5600e3e0facd0397b294` 加现有未提交实现。Common `67f243d9edced77dc3fe669f8a435b1f14b59199`、protobuf `4b0c3aacf0657fbf38253b38918d3358dd4319ec` 未改变，无新增包或宿主业务分支。

## 实现与不变量

- 中立 journal 增加精确 recipient、二进制 delivery cursor 的 SQL LIMIT 页（1～64）及 `(recipient,delivery)` 索引。查询计划实际为 SEARCH 并使用该索引。旧 schema 1 增加访问索引，不转换或删除业务数据。
- Trade 以原始通知序列化内容的 SHA-256 建立派生 Pending 索引，索引只存主体、deliveryId、32 字节 hash，不复制资产。恢复读取实际 SQL 页，再通过内存 deliveryId 字典核对权威记录和 hash；返回对象仍独立深拷贝。
- 成交/取消新增 Pending、发送前转 Unknown、弱 ACK 更新 hash，与业务状态、操作原结果和 live revision 在同一 SQLite 事务。Pending 移出索引不是到账确认；Unknown、原载荷及弱 ACK 继续留在权威账本。发送只发生在事务成功后。
- 旧库监听前每 64 条事务性建立索引，chunk 的意图/结果可重放；完整计数、recipient、hash 核对后才写 ready marker。中断保持未就绪，再次启动续建。已有 marker 却少记录、错主体或错 hash 明确拒绝加载，不通过重建覆盖异常证据。
- 每个新业务事务更新随机持久 revision；运行中读写/查询核对实例已知 revision，并在 SQLite writer 锁内再次检查。其他 writer 或旧备份替换改变 revision 后，该实例 sticky fail，恢复文件不能使旧实例继续写，须检查并显式重新 Load。重放不改 revision。

## 实际执行与结果

SDK 10.0.113 / runtime 10.0.13；`NUGET_PACKAGES=/home/hunyuan2333/.nuget/packages`、`DOTNET_CLI_HOME=/tmp/phinix-dotnet-113`、`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks`，Server 构建还指定 `DirectoryBuildTargetsPath=$PWD/Directory.Build.targets`。

```
dotnet build Tests/ServerTradePersistenceTests/ServerTradePersistenceTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll
dotnet build Tests/ServerJournalTests/ServerJournalTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerJournalTests/bin/Release/net10.0/ServerJournalTests.dll
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p3-sql-index-20261022/stage
```

最初回归分别捕捉到“没有持久 Pending 索引”和“旧备份替换未拒绝旧实例”。修复后 L2 真实 SQLite 检查通过：9 条 SQL 积压 8/1 分页、索引 DELETE 故障整批回滚且不发送、recipient/hash 冲突、重开不复活 Unknown、65 条旧库在第二 chunk 故障后的 64/1 续建、原账本逐字保持、65 条最终恰好一次 attempt；revision 回退、sticky fail 与显式重新加载。中立 journal 检查页游标/主体隔离、错误删除/结果异常回滚、备份及 EXPLAIN QUERY PLAN。

最终隔离阶段 **34 项检查通过**，包括原独立 SQL/XML oracle、模型/架构、publish、原 COMMIT 前后强杀及真实 UDP 独立进程五阶段流程。模型 seed 仍为 20261015/20261017；新增测试操作与顺序固定，故障仅通过隔离 DB trigger/文件替换，生产没有普通客户端可调用的注入开关。新增受控点为 `test_index_remove`、`test_index_bootstrap`，不冒称断电/系统磁盘故障。证据保存在 `/home/hunyuan2333/Phinix/Deliverables/Phinix-Rework-Server-SQL-Pending-Index-2026-10-22/`，源码 hash 见配套 JSON。

## 边界与下一依赖

SQL 分页已落地，但 active/terminal/Unknown 仍为整体 4 MiB XML value；启动加载和写入仍有全量序列化，Pending hash 同步也扫描保留通知。未解决冷归档、operation result 总磁盘配额或全部性能目标。revision 保护当前已加载实例，不能证明跨重启备份没有回退，也不是卷身份/防篡改机制。没有删证据解限、自动补投、退款或 durable game ACK。

用户已授权必要客户端/共享层联合修改，但要求先以工坊为业务基线，冻结并隔离 UI 与业务。已经冻结本地工坊 92 文件的精确匹配及当前源码变化，业务移至 `codex/p3-trade-business` 的独立工作区；下一依赖是该分支上的客户端持久原意图/Unknown 查询与现代 receipt。完整 P3/P6、真实游戏保存与资产物化仍未完成；未部署、推送或发布。
