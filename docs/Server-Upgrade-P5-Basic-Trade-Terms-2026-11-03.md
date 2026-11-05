# P5：原版交易降级条款首个实施切片

日期：2026-11-03。关联 SV-03、SV-50～53、TQ-01/资产组件回归；沿用 FR 持久提交、未知结果保留边界。P5 此次新增原版交易范围仍在实施，不计完整兼容或真机验收通过。

## 用户决定与不变量

用户明确允许：包含原版 Phinix 对端时，沿用原版交互及确认方式，丢弃原版无法表达的附加物品属性，并向 Rework 一侧提示。此决定取代此前“一律要求无损、否则拒绝原版交易”的交付策略。

本切片不改变普通 Rework 双方交易。核心领域不识别原版/Legacy/V2，只保存 `ObservedQuote` 或 `BasicItemsCurrentQuote` 通用条款；Adapter 负责有损投影。整批验证、参与者校验、同事务保存业务状态/操作结果/outbox、COMMIT 后回执、已尝试无 ACK 的 Unknown 保留继续适用。不会伪造报价已读版本、自动退款或静默过滤整件物品。

## 已实现

- `TradeTerms.cs`：通用条款，缺省及存量无字段记录保持严格报价版本规则。网络 metadata 不能选择条款。
- `PhinixFrameworkTradeStore.cs` / `PhinixFrameworkTradePersistence.cs`：创建时保存条款；报价替换清除双方确认；基础条款允许无版本的当前报价确认；明确带来的陈旧/未来版本仍拒绝。条款随 SQLite 状态与终态保留，归档也保留。读取未知/孤立/重复条款失败，不擅自降级或过滤。保留原 `TryCreate` 签名，以独立方法扩展。
- `TradeNegotiationTerms.cs` / `PhinixFrameworkTradeServerService.cs`：服务端专用显式创建入口和基础物品投影端口。整个报价在提交前完成投影，尾项错误不修改报价、不消费缓存引用。默认创建入口仍严格；投影器缺失不会创建基础条款交易。
- `OriginalTradeBasicItemProjection.cs`：Adapter 显式有损转换；从 vanilla 或已知 Scribe 预览取基本字段，保留种类、数量、材质、品质、耐久及嵌套物品。丢弃 Scribe 状态与额外 metadata，不改原请求对象。严格旧编解码器未放宽。未知编码没有可解释的基本字段、预览缺失/歧义/损坏、非法数量仍不能生成虚构资产。
- 有损结果与原始请求分别核对：只保存原始物品指纹用于引用重放，不再次保存已丢弃的完整状态；即使投影结果相同，原始请求改变也仍冲突。
- `TradeArchive.cs`：完整归档保留条款，不留下热状态孤立条款。
- 增加基础条款组件回归和架构分类；通用条款参与领域语法约束，无协议分支白名单。

只修改 Server 工作树；未改 Client、Common、UI/工坊分支、固定子模块或 protobuf，未部署 ECS。

## 验证与发现的实际失败

基线：Server `dev` / `5a0508acb703319e60dd5600e3e0facd0397b294` 加原有未提交修改和本切片。不能把 HEAD 当成完整构建源码身份；阶段清单保存每个源码输入 SHA-256。Common / protobuf 继续使用原 gitlink。

实际命令均通过既有 `p4-work/run` 环境包装器运行，未改 SDK pin：

```sh
dotnet build Tests/ServerOriginalTradeAdapterTests/ServerOriginalTradeAdapterTests.csproj -c Release -m:1 -nr:false -p:NuGetAudit=false -p:BuildInParallel=false -v:q
dotnet Tests/ServerOriginalTradeAdapterTests/bin/Release/net10.0/ServerOriginalTradeAdapterTests.dll
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p5-basic-terms-stage-final-20261103 --timeout 120
```

首次阶段检查失败并保留证据：新增可选参数改变旧方法反射调用形状，已恢复原签名；新增源码缺少架构角色声明，已补齐真实分类。P3 进程 Harness 另触发固定旧 LiteNetLib 客户端 `NetPeerCollection.RemoveAt` 接收/逻辑线程竞态；测试客户端在启动前安装已有经过 hash 校验的同步保护，复用传输修复，不复用业务预期或协议 Adapter。没有修改 vendored DLL、跳过检查或无限重试。

修复后阶段检查通过 49 步，发布目录检查 62 个文件，含 L1/L2、架构/独立模型及 P3 独立进程真实 UDP、强杀和重启检查。组件通过 94 项（既有 66 项及新增 28 项），涵盖 SQLite 重启、终态结果重放、整批投影、陈旧确认、空报价重置、原始引用指纹冲突和严格路径保留。阶段检查后仅加强该组件的两项断言，再构建并重跑组件；产品源码与通过阶段检查的源码一致。

在隔离源码副本执行两项负对照：恢复“一律拒绝无版本确认”和取消提交前投影，均须被新增检查捕获。结果与实际 SHA 见证据清单；不修改正式工作树来执行负对照。

profile：原版 ProtoThing 独立字段写入器、固定原版 golden 子集，以及真实 Framework 服务/SQLite；本切片未运行原版完整交易网络流程。固定 seed 不适用；阶段进程故障点含 `p3.kill-after-offer-response`、`p3.kill-with-offline-pending`。不使用数据行数或模拟物品证明 RimWorld 真实资产守恒。

## 未完成与下一依赖

1. 接通 Adapter 拥有的公开原版 Trading 模块：创建、报价（含原版 tokenless reset）、状态、取消、全量/增量同步与完成事件；通过中立的插件交易路由接口接入，宿主不加 Trade 专属分支。
2. 处理真实登录主体/会话和协议选择：登录本身不能区分原版与历史 Rework；不得仅因未发 Hello 就宣称识别出精确客户端版本。现代发起到原版也必须能选择并持久保存同一条款。
3. 路由补齐后，生产组合注入投影器并选择显式条款。当前新增入口仅经真实组件使用，原版公开入口仍维持先前状态，**不能交给玩家测试“原版交易已支持”**。
4. 在 P3 业务客户端分支增加交易降级提示，检查预留物品、报价响应与持久接收对基础载荷的配合。不修改 UI/工坊分支。
5. 固定原版模块真实网络回归：原版↔原版、原版↔Rework、无报价取消、reset、重连、离线待交付与现代双方不降级。原版没有持久交付 ACK，发送后 Unknown 不会变成 Confirmed。
6. 完成上述自动化后，再要求真实 RimWorld/Waylith 双端到账及存读档验收。Windows、本轮原版真实游戏资产、云端运行均未验证。
