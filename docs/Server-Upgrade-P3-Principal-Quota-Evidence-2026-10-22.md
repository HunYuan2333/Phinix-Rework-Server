# P3：每主体保留交易与归源载荷配额（2026-10-22）

关联 SV-36、SV-31/32、TQ-30/31、PF-07、FR-12 子集。仅 Server 产品、既有回归及文档变化；无客户端/Common/vendor/gitlink、包依赖或数据库 schema 调整。

## 准入策略

在既有全局 3 MiB 序列化预留预算内，增加每主体开发期准入保护：

| 项目 | 上限 | 计量 |
|---|---:|---|
| 保留交易 | 32 笔 | 主体关联的 active、terminal、pending/Unknown tradeId 去重；包含对手关系和历史孤立 outbox |
| 归源载荷主体 | 1 MiB | 原报价主体的 PayloadBytes.Length + PayloadJson 的 UTF-8 字节累计 |
| 归源辅助文本 | 64 KiB | 同一来源的 codecId、metadata key/value UTF-8 字节累计 |

这些是封闭开发的资源保护值，不是经真实游戏工作集验收的生产容量，也不是最大在线玩家数。终态与未知交付暂未安全归档；同一主体连续完成 32 笔且记录全保留后，新创建会被拒绝，不能部署前忽略此限制。后续归档需保留权威结果、去重及恢复证据，不能以弱 ACK、超时或删除记录释放额度。

只在创建和更新报价时检查相关双方，旧账本加载、接受/撤回、成交、取消、ACK 与既有 Pending 恢复不经过新准入门槛。超额输入整批拒绝，不修改原报价/版本/consent，不提前消费合法引用，不阻断已有安全终结。额度从权威状态重新计算，没有另一份需要重建或可能在重启后清空的计数器。

归源规则：snapshot 用 participant 自己的报价；完成 outbox 的来源是 OtherPartyUuid，取消 outbox 的来源是 RecipientUuid。按主体/tradeId 取 snapshot/outbox 主体字节、辅助字节各自最大值，避免同一批资产因 terminal + delivery 两份证据被双计；历史缺 terminal 的 outbox 仍计费。身份与 tradeId 使用已有不区分大小写的领域比较规则，大小写变体不能绕过配额。只检查本次候选双方，不把不相关主体的旧超额直接传播成全局拒绝；所有输入仍受既有全局序列化预算约束。

1 MiB 是归源 payload 字节，不是接收者全部副本的内存/磁盘/网络上限；64 KiB 也是原始文本而非 JSON/XML 逃逸后体积。仍需全局预留来覆盖终态、接收者复制、包装与编码开销，不能将本策略称为完整的 per-owner serialized heap/disk budget。主动提交新的较小报价可以释放该 active 报价对应的载荷额度；历史 operation 的原结果保持不变，不重执行已拒请求。

拒绝沿用 InternalServerError 及已有 FailureMessage，补充 participant quota 说明。消息与拒绝结果仍同事务持久化，成功仅在 COMMIT 后发送，无新增 core 协议分支或宿主业务逻辑。

## 回归与独立预期

保留控制台入口，使用正式 Trade service、真实 Framework serializer 和真实 SQLite 文件。新增 PrincipalQuotas 在旧产品上首先失败：`Per-principal payload quota was not enforced across trades`，日志保留。

修复后实际检查：

- 第一笔 4 × 256 KiB 正好 1 MiB 可接纳；另一笔增加 1 byte 时拒绝，虽然全局预留仍可容纳。直接 SQL 原 ledger 字节保持，包含原报价与已接受 consent。
- 同一拒绝重放保持结果；合法 item 引用未消费。用户显式减少另一笔报价后，原引用可用于新操作，旧被拒 operation 重启后仍回旧拒绝。
- 无关 c/d 仍能在剩余全局空间内报价；来自 a 的大报价不会错误当成 c 自己的 source payload 额度。
- codec/metadata 超 64 KiB 的整批拒绝不消费其他有效成员。4 × 100000 个“界”的 PayloadJson 按 UTF-8 计费并拒绝，不能只按 char 数计算。
- 31 笔已取消 + 第 32 笔活跃可接纳，第 33 笔及主体大小写变体拒绝；第 32 笔仍可成交。
- 弱 ACK、重启及实际发送 attempt 不释放保留交易额度。32 条已提交 Pending 仍能分批恢复，未受创建准入阻塞。
- 合成历史 outbox 删除 terminal snapshot 后重开，仍能从 source 关系计出归源载荷，弱 ACK 不能令新报价绕过。此 XML/SQL 调整仅发生在测试数据库，不是生产操作建议。

既有旧版超软门槛/超新增归源额度的合法 ledger 加载与取消、全局预算、批次引用、状态模型、架构、游标校验、分页恢复和进程强杀恢复随完整 stage 重跑。SQL/字节检查只证明服务端 opaque 证据与拒绝不变量，不能当真实游戏库存/存档守恒。

## 命令与构建证据

工作目录为 Server 仓库，SDK 10.0.113/runtime 10.0.13；子进程使用 `NUGET_PACKAGES=/home/hunyuan2333/.nuget/packages`、`DOTNET_CLI_HOME=/tmp/phinix-dotnet-113`、`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks`、Server 自有 DirectoryBuildTargetsPath：

```sh
dotnet build Tests/ServerTradePersistenceTests/ServerTradePersistenceTests.csproj --no-restore -c Release -m:1
dotnet Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-server-principal-20261022/stage
```

最终隔离 stage **33 步全部通过**，包含正式组件、真实 SQLite、现有强杀/barrier、独立 SQL/XML oracle、模型、架构负控制、真实临时 UDP poll worker 与 publish 制品检查。该环境允许本机临时 UDP；没有连接云端或玩家目录。阶段构建使用现有缓存、关闭 NuGet audit，不作为新增在线漏洞扫描。profile：existing-rework-component；新配额步骤无随机 seed，既有模型 seed 20261015/20261017。无新生产故障入口、无新增系统磁盘故障测试。

最终源码与 stage snapshot SHA256 对照、dirty HEAD、固定 gitlink 和持久证据见配套 JSON。此前直接组件构建/运行也通过；首次旧实现的失败保持原样，不记为通过。

## 剩余范围

SV-36/P3/P6 仍未全部完成。当前统计对象是单个 blob 里的领域数据，未实现 SQLite 行级分页、归档/冷存储、operation result 表磁盘配额、生产 CPU/GC/p99、独立进程配额协议 trace。仅检查目标双方的 source body/辅助文本及关联交易数，不宣称完整接收者物理字节配额或完整反滥用能力。

真实游戏联测、客户端持久意图/Unknown 展示、durable game receipt、原版 Adapter 仍是独立依赖。当前未部署本轮产品代码、未更新云端应用、未推送/发布；需要联合黑盒时通知用户。下一项继续行级存储/可保留 Unknown 的安全归档与联合意图/receipt 边界，不能靠扩大阈值或清空权威记录绕过验收。
