# P3 服务端配置与加速版最小验收收口（2026-10-22）

**结论：RoadMap §14 的 P3 服务端加速版最小退出门槛已满足；P3 全部目标、真实游戏交付和 P6 未全部通过。** 本轮按用户要求连续完成配置接入、持久化健康补强、实际配置工具、独立进程验收和持续阶段门禁。继续遵守本线程 Server-only，不修改并行客户端工作区或固定 Common/vendor/gitlink；未部署云端、推送或发布。

## 本轮产品变化

- TradeServerConfig 增加可配置主体 retained count、source payload、codec/metadata 文本限额及恢复页大小。三项主体限额 0 表示关闭对应配额，固定全局/硬存储保护保持。旧反序列化缺字段用默认，非法值拒绝。Service 复制为不可变 domain policy，事务候选必须继承原策略，配置变化需重启。
- 正式 Trade 插件经既有 IExtensionConfigProvider 读取配置，不把配置或协议 profile 塞入领域业务。通用 Config.GetConfig 对损坏的持久插件配置报错，防止默默还原默认；外层异常不输出任意配置值。插件按通用注册回滚规则不可用，无 Trade 专属宿主启动分支。
- 增加只写新候选的配置工具，实际 Config.Load/GetConfig roundtrip 验证根/其他插件设置和权限、显式 0、旧配置默认、拒绝覆盖及坏输入。使用说明见 [交易配置](Server-Trade-Configuration.md)。
- 加强 SV-38：根实例读写/查询前验证权威原始文件、marker 与 SQLite source-hash。证据缺失、改变、marker 过大时停用该实例，不能空账本/假 NotFound/继续写；恢复文件后必须显式检查并重新 Load。Save 错路径也使实例失败。保留数据库和已提交结果，没有退款或重放副作用。全源 hash 检查增加同步 I/O，未称性能优化。

## P3 最小退出核对

| 加速门槛 | 已执行证据 | 范围 |
|---|---|---|
| 真 SQLite 与短事务 | Journal/Trade 文件 DB、busy/rollback/预算/在线备份恢复 | L2；不替代真实断电 |
| 重复/冲突与原结果 | 元组 principal/operation/fingerprint、独立 wire query 自己/他人/冲突、改配置后旧拒绝仍不重执行 | L2/L3 |
| 双方报价版本/确认 | 既有实际服务回归、陈旧/未来/缺版本拒绝、报价变更撤 consent、双边确认 | L1/L2；历史联合客户端证据沿用其版本范围 |
| 正常重开/报价恢复 | published host 两客户端，强杀后同 UUID/原报价/原结果保留 | L3 |
| COMMIT 前失败 | SQLite trigger/事务 rollback、原引用保持、无成功回执 | L2/L4 受控异常 |
| 提交后丢响应/一次强杀 | 原 response barrier 子进程 kill/reopen；新增 published host 两次 SIGKILL，分别在报价回执后、离线取消提交后 | L3/L4；SIGKILL 不是断电 |
| recipient 与交付恢复 | real UDP envelope/session、查询 principal 隔离、双方 recipient outbox 独立 SQL/XML、离线 Pending → 单次 attempt → Unknown，弱 ACK 不伪确认 | L2/L3 |
| 配额/健康失败路径 | 可配置/0/不可变策略/全局保护仍在、authority 缺失 sticky fail、坏配置无能力/明确控制拒绝/资产状态不变 | L2/L3 |

最终串行隔离 **34 步全部通过**，包括原 33 步与新增 `p3-process-test`。新增独立流程已接入 stage/既有 CI 调用，P6 不再首次建设该证据。没有重写全部测试或添加 NuGet 依赖。

## 独立进程网络流程

`P3NetworkProbe` 只使用真实 Common 编解码、LiteNetLib 与自己的断言/查询 DTO，不引用服务端 Trade adapter/service 生成预期。Peer framing 源自冻结的 2026-10-18 独立探针（原 SHA 在代码中记录），本轮编译使用 Server 固定 Common。profile 是声明的 Framework v2 synthetic peer，不冒充原版/未升级 Rework 完整 Harness 或真实游戏客户端。

1. initial：配置 retained=1/payload=16/aux=1024/page=1；双身份登录、报价 `{17,0,255}`、原操作查询、他人 NotFound、指纹 Conflict；published server 随后 SIGKILL，不经 timer/final save。
2. restart：同凭据恢复同 UUID、原报价重放；显式断连并等待真实 UserUpdate.User.Uuid 离线广播后取消，A 收自己整批物品事件并发弱 ACK，B 保留 Pending。B 重新登录但不发 Framework hello，尚不可交付；新增创建因持久配置 quota=1 拒绝。再次 SIGKILL。
3. policy：仅修改配置三项主体配额为 0 后启动；A 不重收 Unknown，B 恰收一次保留事件。原拒绝 operation 仍回旧拒绝，新 ID 可创建；原完成/报价结果仍可查询。正常 stop。
4. recovered：再次启动恢复新 active 交易；两方已尝试事件均不重发，原取消结果保留。正常 stop。
5. bad-config：隔离配置注入非法数值；Trade 未发布能力，创建收到 RequestInvoked=false 的相关控制拒绝，没有伪造业务成功或默默默认；独立 SQL 核对原 ledger/操作记录保留，敏感合成值不入日志。

每阶段独立 SQLite `integrity_check`、原 operation 唯一、literal XML/原始 byte 预期、双方 recipient/deliveryId/Unknown/弱 ACK、查询不写操作记录；合成私有凭据只存在临时目录，结束删除，不进入日志/制品。模型 seed 保持 20261015/20261017，新流程操作顺序固定，身份随机数不作为伪确定性调度 seed。

稳定测试故障点：`p3.kill-after-offer-response`、`p3.kill-with-offline-pending`、`config.trade.malformed-section`；组件仍有既有 response barrier。无生产普通客户端可启用的故障机制。外层超时将 driver/host/peer 保持在同一进程组，新增负控制验证 timeout 不遗留 peer。

首两次新 Harness 失败保留：停止/断连屏障的身份索引曾误读外层 Uuid，实际广播填在 User.Uuid；修正为显式断连+实际字段后，在同一超时门槛通过，未增加 timeout 或无限重试。一次制品检查导入了错误的本地工具模块，已改用实际 `check.check_artifacts`；不是产品编译失败，也未当成制品检查通过。所有阶段最终 source hash 与隔离 snapshot 对照见配套 JSON。

## 实际执行入口

工作目录 Server，SDK 10.0.113/runtime 10.0.13；子进程使用 Server 自有 DirectoryBuildTargetsPath、`NUGET_PACKAGES=/home/hunyuan2333/.nuget/packages`、`DOTNET_CLI_HOME=/tmp/phinix-dotnet-113`、`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks`。

```sh
dotnet build Tests/ServerTradePersistenceTests/ServerTradePersistenceTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll
dotnet build Tests/ServerLifecycleTests/ServerLifecycleTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll
dotnet publish Server/Server.csproj -c Release --no-restore --no-self-contained -m:1 -p:BuildInParallel=false -o /tmp/phinix-p3-closeout-20261022/app
# 现有工程在 Build 而非 Publish 复制插件；实际检查前复制完整 Extensions 闭包
python3 tools/quality/p3_acceptance_probe.py --server /tmp/phinix-p3-closeout-20261022/app/PhinixServer.dll --probe Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-p3-closeout-20261022/network-final
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p3-closeout-20261022/final-stage
```

独立进程和 stage 在允许本机临时 UDP 的环境执行；不连接 ECS。stage 使用缓存并关闭 NuGet audit，不能代替独立在线安全扫描。正式产品依赖图未因测试项目引用 Trade contract 新增外部包。

## 完整目标未关闭的清单

| 条目 | 当前状态 / 后续依赖 |
|---|---|
| SV-30 | 基础事务/备份通过，完整 schema 运维/系统 IO 矩阵未声明完成 |
| SV-31 | 服务端稳定意图/结果通过；客户端发送前持久化 operationId/原意图仍未接入本轮 |
| SV-32 | state/result/outbox 原子、弱 ACK recipient 通过；modern durable game receipt 未实现，不能提升为游戏保存确认 |
| SV-33 | 服务端查询/报价恢复/Pending 尝试通过；客户端 Unknown 展示/持久去重后的可靠重投、原版 Adapter 待联合/P5 |
| SV-34 | Framework XML 校验导入/权威切换有证据；原版 protobuf 导入、完整回退演练未全部完成 |
| SV-35 | 加速最小异常/强杀通过；真实磁盘满/断电/完整点集延期 |
| SV-36 | 全局预留、可配主体限额、内存恢复页通过；SQLite 行级页、安全归档/冷存储、结果表总磁盘预算仍缺 |
| SV-37 | 单项查找避免全主体 clone；分配/query plan/全部性能优化未验 |
| SV-38 | 缺文件/authority/加载及存储写保护有证据；完整恢复 generation/卷故障全集未验 |

这些保持未完成，不因最小退出满足而勾掉。原 P0～P7 主依赖保持：可以基于本轮服务端最小退出继续后续开发，完整资产/现代 receipt 和真实游戏发布声明必须等对应联合证据。客户端未被本轮修改，真实 RimWorld 物化、库存、双边存读档以及工坊凭据保留未在本轮验收。P3 完整目标/P6 不能凭记录数、自动化或一次服务器构建宣告通过。
