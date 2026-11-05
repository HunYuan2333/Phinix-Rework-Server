# P3：服务端权威操作结果只读查询（2026-10-22）

关联 SV-31/32、TQ-30/31 子集。安全运行环境升级后恢复原 RoadMap；本线程继续仅修改 Server。依赖方向为 Trade Framework 边界 → Trade 持久业务 → 中立 SQLite journal，不新增宿主业务分支。Common/client/vendor/gitlink、包依赖及数据库 schema 不变。

## 实施与不变量

新增现代可协商命令 `trade.operation.query.request` / `trade.operation.query.response`，由 Trade 插件声明及路由。查询独立 additive API，不改变原 `IFrameworkTradeServerApi` ABI。当前客户端尚未声明此能力、持久保存稳定意图或消费结果，不能据此称客户端未知结果恢复已完成。

请求只有 `OperationId` 与 `RequestFingerprint`（64 个十六进制字符，SHA-256）。主体从已就绪的连接/session 取得，不能在请求中指定；两个查询能力均需协商。旧端不被补造此能力。查询限定单项、operationId 最长 128 字符、JSON 最长 1024 字符，不提供跨主体/全库枚举。

指纹沿用已经提交的 typed intent：SHA256(UTF8(kind + LF + FrameworkSerialization.SerializePayload(原 typed request)))，十六进制大小写均接受。kind 为 create、offer、ack；status 带原观察版本时为 status@十进制版本，历史缺版本为 status。不是整个 FrameworkPacket/raw JSON 的 hash，不能在未知时重建一个“差不多”的请求。客户端后续须在发出前持久保存原 intent、operationId 和指纹；本轮未迁移客户端。测试以独立字面量 `create\n{"OtherPartyUuid":"b"}` 核对真实数据库指纹。

响应 Status：

| 状态 | 语义 |
|---|---|
| Committed | 该主体、operationId 和指纹对应原已提交结果，包括已提交的业务拒绝；Success 表示原业务结果 |
| NotFound | 当前读取视图没有该主体的操作；他人操作也返回此值，不证明永远未提交，更不授予退款或换 ID 重发权限 |
| Conflict | 同主体、同 ID 的已提交指纹不符；不执行或覆盖原操作 |
| InvalidRequest | 无效字段、指纹、过大或损坏的查询载荷；不执行业务 |

仅 Committed 返回原 Success/Failure、可用的 TradeId/SnapshotVersion、Completed/Cancelled；这是原操作时结果，不是交易当前快照。部分历史拒绝没有 snapshot，TradeId/version 可为空。Completed 沿用原业务终态标记，Cancelled 单独区分取消。响应不含物品、双方报价、其他主体记录或凭据，不能充当交付事件/游戏 ACK。

持久层用参数化 SQL 按 principal/operation 单项读取并验证原指纹。查询不写 operation、state 或 outbox，不触发发送资产、重投、退款或状态转换。缺失 DB/存储故障向上失败，不能转为 NotFound/业务拒绝；未加载持久存储时也拒绝，不使用内存成功替代权威结果。

## 验证与执行范围

回归放入现有 ServerTradePersistenceTests 控制台，不新增框架。覆盖真实 SQLite、正式 Trade service、真实 packet/payload serializer：提交成功/拒绝、独立字面指纹、另一主体和缺失操作、冲突/非法字段/坏 JSON/长度、重启后终态结果、原结果版本保持、查询不改变 SQL 操作数和原始 ledger 字节、无物品/业务事件、过期 session/未就绪连接拒绝、数据库移走不创建/不伪造结果、插件能力和路由声明。

原实现缺少查询 API，新增回归首次构建明确失败，保留日志；实施中遗漏 Linq 导入的编译错误已修复。临时隔离副本关闭读取指纹检查，测试以 `Query ignored request fingerprint` 失败，负控制检测成功；故障点 `test.query.fingerprint-check-disabled` 仅存在于测试副本，不可由生产客户端触发。负控制脚本首轮因两个已有指纹检查同名而断言失败，修正为只替换读取检查后执行，无生产源码注入。

命令（工作目录为 Server 仓库；构建使用 SDK 10.0.113/runtime 10.0.13，CLI HOME `/tmp/phinix-dotnet-113`，缓存 `/home/hunyuan2333/.nuget/packages`）：

```sh
# 直接组件构建/运行（既有 assets）
DirectoryBuildTargetsPath="$PWD/Directory.Build.targets" MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks dotnet build Tests/ServerTradePersistenceTests/ServerTradePersistenceTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll
# 最终隔离阶段门禁；实际执行时设置上述 CLI HOME 和包缓存环境变量
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-server-operation-query-20261022/accepted-stage
python3 /tmp/phinix-query-negative.py
```

第一次隔离阶段因遗漏已有包缓存变量、受限网络还原失败（NU1301），不记通过；第二次构建/业务检查成功但新增源文件未分类，架构门禁失败，不记阶段通过。已将查询 store 方法放入原受约束的 domain persistence 文件，wire DTO/翻译文件明确登记为非领域边界，不增加 Legacy 债务豁免。最终隔离阶段 **33 步全部通过**（L1/L2、既有进程强杀与独立 SQL/XML 检查、架构与 publish 制品检查）。最终受影响源码与 stage snapshot 哈希逐项一致；结果和源码 SHA256 见配套 JSON 与持久交付证据。

profile 为 existing-rework-component（新增 query capability），全部测试身份为 a/b/c 等合成数据、隔离目录、真实文件 DB，无玩家数据。阶段 runner 固定模型 seeds 20261015/20261017，查询用确定性步骤，无随机 seed。既有提交后响应 barrier、强杀及独立 SQL/XML oracle 随阶段运行；不把 SIGKILL 叫作断电。

## 剩余依赖

P3/P6 仍未完成。客户端持久原意图、Unknown 查询与关联展示、接收归属/旧存档回滚、modern durable receipt、原版 Adapter 和全局预算仍待推进。查询本身不补投资产，也不消除客户端曾调用交付后未知的问题。新查询还未在独立进程真实 UDP 或真实游戏验证；不要求用户此刻做新客户端黑盒，待联合候选就绪再通知。未部署本轮产品代码、未推送/发布，不改刚更新的云端运行环境。
