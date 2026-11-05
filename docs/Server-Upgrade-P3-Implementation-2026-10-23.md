# P3 实现收口候选（2026-10-23）

状态：按 RoadMap §0 加速范围，SV-30～38 的产品实现已收口；本轮只有下列实际执行的自动检查通过。P3 阶段验收、扩展故障矩阵及 P6 真实 RimWorld 联机/物化/存读档均待验，不写成通过。用户本轮要求先完成实现、测试另行集中进行。

固定联合范围：Server `dev@5a0508acb703319e60dd5600e3e0facd0397b294` 加未提交改动；Client 仅业务工作区 `Phinix-Rework-P3-Business`，`codex/p3-trade-business@53c92ce8eb314dcb8ca422ae4b6891a007931128` 加未提交改动。依赖方向是 Trade → 中立 Inventory 可选保存回执 API；Host 无交易专属生命周期分支；离线 Legacy 工具 → 中立导入记录。Server Common `67f243d`、Client Common `c165f61` 与 protobuf `4b0c3aa` gitlink 未调整，没有新增包或改 vendored SDK/protobuf。原 UI 工作区冻结的 28 UI + 9 未分类文件 hash 全部保持原值。

## 实现与不变量

| 关联 | 实现 | 保持的约束 |
|---|---|---|
| SV-30/31/38、FR-02/04 | SQLite schema 1→2 的单事务迁移；state/result/outbox 使用同一 writer/事务，usage 表及 9 个触发器同事务更新；启动独立重算 usage、核对触发器；原 revision/authority 保护保留 | 迁移失败不提供空账本；只有 COMMIT 后成功；操作 ID/指纹冲突拒绝；预算不足仍能读取原提交结果 |
| SV-32/33、FR-09/12 | 协商 `trade.delivery.receipt.request/response`；`DurableInboxV1` 携带 deliveryId、recipient 绑定 payload SHA；InboxStored 与 SavedInventory 分开持久化；固定 receipt operation 幂等；每个新 Ready epoch 最多重投一次尚未确认的现代通知 | 系统通知和接收主体/session 关联；先存原始载荷才发 InboxStored；非空批次 InboxStored 不等于保存到账；弱 ACK 不升级为保存回执 |
| SV-33、TQ-30/31 | 客户端持久 `.delivery/.plan/.checkpoint`；原始收件、存档身份、库存 journal identity、完整 opaque 批次/固定 depositId 绑定；计划先写、attempt 后写、最后调用真实库存 API；检查原 deposit 是否已提交后恢复 | 冻结请求逐物品核对原载荷；不采用后来存档；已成功返回后账本缺失视为分歧；只重试同一幂等库存请求，不重造 Thing |
| SV-33/38 | 中立 `IInventorySavedReceiptApi` 读取已经写完的真实 `.rws` XML，核对 Inventory GameComponent 的 SaveId、journal identity、原 deposit 账本；客户端保存不可覆盖的 checkpoint 后发 SavedInventory | 内存/API 成功不算保存；旧存档缺少证明不确认；marker 或预期日志缺失拒绝写；包含所有权提交的 future journal 不能被“丢弃恢复”按钮抹除 |
| SV-31/33 | 发送前保存原 intent 与库存 reservation 的 save/journal/id/operation/items 绑定；原指纹只读查询的 Committed 结果用于匹配预留 Commit/Restore；接入现有活跃操作结算 | NotFound/断线/超时不退款；只解析原操作结果，不重发业务请求；不从 intent 盲目恢复实物 |
| SV-34 | `Tools/LegacyTradeImportAdapter` 停写工具，原 protobuf DTO 来源/hash 固定，独立输出目录、原输入保留、整体往返校验、基础物品无损翻译、真实候选 SQLite 回读对照、原输入 hash 回退核对 | 旧字段/默认同意规则只在 Adapter；未知/损坏/不能无损表达的源整份 quarantine；历史 terminal 均 Unknown；工具不执行在线切换 |
| SV-36/37 | 双方 Confirmed 的 terminal snapshot/receipt 冷归档同事务移出热投影；原操作结果保留；原 SQL recipient/cursor LIMIT 派生索引继续维护；客户端恢复每次读一次快照、每批 8 条调度 | Unknown 不淘汰；归档回读仍验证；无每页重复读完整文件；未共享可变载荷 |
| SV-36 | `StorageMiBLimit=256`，0 关闭或 32..16384 MiB；SQLite page cap 与精确逻辑 usage 双限；普通准入留出 16 MiB 给 status/取消/receipt/交付恢复；配置候选工具支持 `--storage-mib` | 不删除结果来腾空间；达到硬上限明确拒绝，不能承诺无限恢复；回滚日志/备份/文件系统开销另需磁盘空间 |

回执响应包含有权限查看的双方交付状态；客户端验证原 correlation、系统 sender、当前 session/endpoint/主体后，在现有核对视图显示。服务端响应观察不是物品重投授权；原保存 checkpoint 独立保留，响应丢失后仍能重发同一回执。

## 明确的策略范围

可靠自动恢复适用于 `inventory.deliveryMode=inventory` 的原存档/原库存日志。已有 `direct` 直投选择保留；直投的 attempt/原始载荷被持久保存，但没有假造保存 ACK，也不会在重启后再造实物。没有原接收存档绑定/冻结计划、历史 attempt 缺证据、不同 save/journal、旧存档实物托管分歧均保留核对。原版/历史客户端首次实际发送后缺少可靠持久回执，继续按 §0 尽力交付且保留 Unknown；旧端实时接入 Adapter 的完整扩展仍是 P5。

热账本仍采用有界 XML 投影放在 SQLite 内；本轮没有为了重写为全关系模型扩大迁移面。SQL Pending 索引和逐交易冷归档已实现本阶段聚焦优化。冷归档保留资产证据与结果，不是无限容量承诺。SQLite full synchronous 的进程崩溃能力不等于断电/硬件故障零丢失。

## 实际检查与证据

SDK 10.0.113 / Runtime 10.0.13。所有数据为合成身份与隔离临时目录。无新随机场景，fixture 是固定字面 protobuf；既有强杀沿用原 barrier。完整命令、输入/源码/程序集 hash、原失败和修复原因在 `/home/hunyuan2333/Phinix/Deliverables/Phinix-Rework-P3-Implementation-2026-10-23/`。

- L1/L2：ServerJournalTests（真实 SQLite、schema migration、usage/quota 回滚、原提交结果在超预算后可读、缺触发器拒绝、既有 COMMIT 前后强杀）；ServerTradePersistenceTests（含本轮现代双主体回执、跨 recipient 拒绝、新 epoch 重投、双方保存确认归档、重启不再发送）。独立 Python SQLite/XML oracle 通过。
- 客户端文件控制台：Inbox **90** 检查（含冻结计划先于 attempt、重启、冲突、session 回执关联、checkpoint/孤立证据、既有 SIGKILL）；Intent **32** 检查通过。保持原控制台入口，没有替换框架。
- 实际 1.6 / 1.7 Trade 组合分别 **291** 断言通过；实际编码 wire 分别 **34** 检查通过。1.7 Inventory 组合 **85** 断言、运行时 **21** 场景通过。新增恢复 owner 的订阅一次注册、停止和失败路径全部释放。
- 1.7 完整 26 项目源码图、1.6 solution 与三个维护插件构建通过；最终小变更在隔离图中重新编译。Server 完整工程与离线工具构建通过。保留既有 obsolete/SDK/跨框架警告；未新增包审计结论。
- 离线工具：4 个独立字面 wire fixture（空、基础物品、未知字段、截断）按预期分别候选成功/整份隔离；实际 SQLite 完整性、数量/载荷及原输入 restore SHA 核对通过。

复现环境：`NUGET_PACKAGES=/home/hunyuan2333/.nuget/packages`、`DOTNET_CLI_HOME=/tmp/phinix-dotnet-113`、`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks`。Server 另设 `DirectoryBuildTargetsPath=<Server repo>/Directory.Build.targets`。

```sh
dotnet run --project Tests/ServerJournalTests --no-restore -p:NuGetAudit=false -p:BuildInParallel=false
dotnet run --project Tests/ServerTradePersistenceTests --no-restore -p:NuGetAudit=false -p:BuildInParallel=false
python3 tools/quality/journal_check.py Tests/ServerJournalTests/bin/Debug/net10.0/ServerJournalTests.dll
python3 tools/quality/trade_persistence_check.py Tests/ServerTradePersistenceTests/bin/Debug/net10.0/ServerTradePersistenceTests.dll
```

客户端控制台增加 `-p:RimWorldDepDir=/home/hunyuan2333/Phinix/Phinix-Rework/GameDlls`。隔离产品图及组件命令由 evidence manifests/logs 记录；游戏 DLL 只作本地引用，没有拷入交付证据。

## 留给集中测试的工作

P3 联合 L3：实际新 client wire + published Server 的 SavedInventory/响应丢失/重连/恢复序列；本轮没有重跑现代回执的完整网络链。P6：双方真实 RimWorld 1.6/1.7.4981 交易、取消、保存/重启、库存取出物化、旧存档/另存为、direct 模式 Unknown 展示；不能以本轮自动化代替资产守恒。另有真实历史备份迁移/一致回退演练、专用卷满/断电/损坏与容量压力按声明范围补测。没有覆盖率/性能预算达标声明。

本轮未安装候选、更新工坊、部署 ECS、推送镜像、提交或推送代码。下一依赖是集中联合验收；本文件仅关闭加速 P3 的实现清单，不关闭 P3/P6 验收。
