# P1 缓存字节预算证据（2026-10-16）

SV-11 / PF-07 / TQ-10 子集。Server 基础 SHA `5a0508acb703319e60dd5600e3e0facd0397b294`，包含既有 dirty 工作区。完整输入 hash、固定 gitlink、命令/退出码及失败记录见 [机器证据](Server-Upgrade-P1-Cache-Evidence-2026-10-16.json)。没有新增包、共享层或客户端修改，没有提交/推送/部署。

## 行为与原因

现有缓存仅有 200 条总量限制，无法约束单个 payload 的体积。现在暂存引用按实际 Framework JSON 序列化后 UTF-8 大小准入：单项 1 MiB、同 owner/connection/session 4 MiB、全局 16 MiB，同时保留总量 200 条。packet/owner/connection/session key 各限 256 个 UTF-16 code unit，metadata 最多 1024 项。序列化前另限制原始 bytes/文本累计和 metadata 数量，避免先为明显超大输入复制/序列化，再发现超限。

预算包含 CodecId、PayloadJson、PayloadBytes、metadata 的实际编码开销及转义；不是游戏物品数量、托管堆精确占用或生产性能预算。这些是当前开发版保护上限，尚未用真实大型 mod 物品确定适配范围。网络解码、并发瞬时分配、inline offer 不由这个暂存缓存预算覆盖。

同一锁内从最多 200 条真实保留项计算 scope/global 使用量，与整批报价消费共用该锁。相同 ID/内容重传不重复计费，冲突不覆盖，超限拒绝新项，不淘汰旧项。失败批次不消费引用/释放额度；成功消费才删除项并释放其字节预算。原公共 ABI、线格式和报价数据不变，插件原 StopPropagation/FailureReason 边界继续传播拒绝；并非权威提交回执。

## 实际执行及结果

1. 先添加四项独立预期（单项/会话/全局/key）并构建真实组件。`dotnet Tests/ServerSafetyRegressionTests/bin/Release/net10.0/ServerSafetyRegressionTests.dll` 修复前退出 1：21 项中新增 4 项失败，明确捕捉无预算问题。
2. 修改产品后，同一组件最终执行 **21 项全部通过**，覆盖 binary 编码膨胀、JSON/metadata/转义超限、metadata 条数、拒绝前后原报价与合法引用保留、跨会话预算独立、满额相同包重放、失败批次不释放、消费后额度可再次使用。
3. 显式串行 build Safety、Server、Model、Architecture：`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet build <project> -c Release --no-restore -m:1 -p:BuildInParallel=false`。产品/组件构建通过。Architecture 初次失败 NETSDK1004（本地缺少 obj/project.assets.json），补 `dotnet restore Tests/ServerArchitectureTests/ServerArchitectureTests.csproj -m:1 -p:BuildInParallel=false -p:NuGetAudit=false` 后 build 和运行通过。失败保留在 targetAttempt，不算通过。
4. `dotnet Tests/ServerModelTests/bin/Release/net10.0/ServerModelTests.dll --property all --seed 20261015,20261017`：3×256 属性检查通过，独立模型不引用产品，原整批/主体/会话/逻辑重连预期保持。该模型仍不覆盖字节预算、持久事务或交付。
5. `dotnet Tests/ServerArchitectureTests/bin/Release/net10.0/ServerArchitectureTests.dll`：通过，仍有既有 8 处 LegacyFallback 债务，4 个架构负对照通过，禁止新增业务协议判断。
6. `python3 -m unittest discover -s tools/quality -p 'test_*.py' -v`：9 项通过。缓存内部条目改为 payload+encoded size 后同步修正两个旧变异文本目标，并在临时副本验证三种变异均能应用；本轮未重跑完整产品变异测试。coverage 入口的历史固定 17 项匹配改为沿用普通安全入口的动态案例数匹配，避免新增真实回归后采集入口误判；没有重跑 coverage。

首次临时检查脚本误用了不存在的 copy_snapshot helper，在执行测试前退出；改用已有 snapshot 后继续。最终目标检查与修复记录分开保留，没有无限重试。源码输入 hash 与最终文件一致。

## 剩余限制

未实现 TTL、断线清理、epoch/operation 绑定、会话撤销后的缓存回收；未消费项仍可能长期占用有限额度，只能拒绝新项，不能宣称已解决所有拒绝服务。完整 stage/publish、真实网络、游戏库存/存档/物化、CI/Docker 本轮未跑。单项上限及旧端实际可表达范围仍需客户端联合验收。没有 SQLite/逐操作提交/outbox/重启交付证据。

P1 继续 SV-13 发送结果边界，随后按最小 P2/P3 实施协议、事务持久化和交易恢复。旧端离线待交付/未知保留策略不变，未因缓存预算实现而标为交付完成。
