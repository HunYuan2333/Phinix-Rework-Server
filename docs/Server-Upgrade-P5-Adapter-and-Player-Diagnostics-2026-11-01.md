# P5 首切片与玩家可见诊断清理（2026-11-01）

## 范围、版本与不变量

Server `dev` / `5a0508acb703319e60dd5600e3e0facd0397b294`，Client 业务线 `codex/p3-trade-business` / `53c92ce8eb314dcb8ca422ae4b6891a007931128`，均包含既有未提交修改，以证据的源码和 DLL hash 为准。依赖方向不变：业务客户端消费其固定 Common；Server Adapter 消费其固定 Common wire DTO，统一交易 store 不接收旧包或兼容开关。两个 Common gitlink 与 vendored protobuf/SDK pin 未调整，无新依赖，工坊/UI 分支未合并或修改。

关联：P3 玩家展示清理、SV-50/52 输入翻译子集、SV-54 架构约束、TQ-30/50 回归。整批拒绝、不提前消费引用、事务/结果/outbox、COMMIT 后成功、Unknown 不退款等底线保持。

## 实际修改

- Client `TradeList.cs`：原始 Reservation / Completion / Intent journal 投影及计数仅在 `Prefs.DevMode` 显式开启时出现。普通模式不调用 recovery Capture/Revision，不再把历史交付日志堆成交易列表。模式变化使旧 action context 失效，并清掉诊断选择。活跃交易、仍待结算的实际操作和重试确认保持；持久记录、后台恢复、去重和存档绑定不删除。
- 取消提交异常在页面显示本地化“结果尚不能确认”提示，异常详情继续进入错误日志，不向普通页面拼接内部异常文本。
- 检查了 Chat/Trade/Inventory/PluginStore 与宿主展示入口。用户登录变更细节和本地开发包安装入口已有 DevMode 限制；插件加载失败、缓存/目录受限提示、支持摘要和库存恢复警告是实际故障功能，保留。没有全局屏蔽日志、删除玩家持久文件或重排 UI 布局。
- Server 保留六处详细交易追踪，通过 `TradeServerConfig.DiagnosticLogging` 控制，默认 false；启动读取后固定，重启生效。拒绝、恢复异常、缺交付记录等 WARNING/ERROR 及加载/保存摘要不随该开关关闭。按用户补充要求，不删除服务端诊断能力。
- 配置候选工具增加 `--diagnostic-logging on|off`；保留旧配置默认、其他配额和数据库路径，保证 DataContract 字段顺序；损坏 boolean 明确拒绝，输入和已有输出不覆盖。
- P5 `LegacyTradeOfferAdapter.cs`：历史 Rework 内联 Items/缺省空报价与引用批次转成一个完整集合。混合输入、缺失/重复引用、非法项整批拒绝。Adapter 只翻译并校验，不消费、提交、退款或推断 ACK；scope 查找仍由 wire 边界提供，消费仍在成功提交之后。

## P5 profile 实际来源与缺口（SV-50）

| Profile | 可复核来源 | 当前证据边界 |
| --- | --- | --- |
| 原版 Phinix | `Tests/Fixtures/OriginalLoginChat`、`OriginalTrade` 的 provenance/normalized；原版 SHA `ce82b8f2cd57718f05de86458eebd3320f35c4d8` | 原版模块真实 UDP 的脱敏派生捕获及独立字段 oracle。不能当成当前升级 Server 已支持原版聊天/交易；完整接入待实现 |
| 未升级 Rework | `HistoricalReworkLogin`；Client `23643e194c254026b3665ced1e618123d68f7566` / Common `9d0710aaa9b41c07f28d9bfa25599247e7bb1f93` | 历史真实客户端基础模块登录捕获，不是完整旧端 Chat/Trade 验收。固定 Common 中 TradeContracts 同时声明旧 Items 与 ItemPacketRefs；本次针对其 wire 形状做真实服务/序列化回归，不伪称历史完整客户端已跑通 |
| 当前 Rework / UDP Framework | 固定 Common、实际发布 Server 与独立 Protocol peer | 阶段内独立进程 UDP/真实业务恢复通过；真实 RimWorld 资产与双方联机仍待 P6 |
| TCP＋TLS / 现代身份 | 既有 P4 实验代码与自动化证据 | 实验性、不建议使用；实机和正式切换 TODO，不阻塞当前 UDP 主线 |

固定 golden corpus 未重生成，hash 见本轮 manifest。没有给旧端补造 ACK、把独立字段预期改成产品输出或宣称完整 profile 兼容。SV-50 来源清单/输入形状子集、SV-52 历史 Framework 输入边界推进；SV-51 原版 Chat 模块接入、SV-52 原版 Trade/关联/无损表达、SV-53 混合双方流程、SV-54 宿主既有八项 obsolete-result 债务仍未完成。P5 不标整体退出。

## 实际验证

- `python3 Tests/PresentationRuntimeTests/run-isolated.py --output <isolated>`：528 项，含普通模式不读/不展示三类 journal、切换清选、保留记录和实际交易动作；GUI stub，不是游戏画面验收。
- `python3 tools/compatibility/build-dual-host.py --game16 <refs16> --game17 <refs17> --harmony17 <Harmony> --output <isolated>`：1.6/net472、1.7/net10 全图构建、三个维护插件消费者和 95 文件包审计通过。
- `python3 tools/quality/run-client-component-probes.py --suite trade-composition --assemblies <exact-package-closure> --game <refs> --harmony <Harmony> --output <isolated>`：实际包内程序集，两版各 320 项。
- Server `python3 tools/quality/check.py --suite stage --isolated --output <isolated> --timeout 120`：43 步通过；包含 golden 11 项、真实组件安全回归 27 cases、SQLite/独立 oracle、事务/重放/故障/恢复、架构及最终发布 Server 的独立进程 UDP 业务探针。新增内联批次无损/非法尾项保全、trace 开关开关/旧配置兼容；新增 Python 配置检查 5 项进入阶段入口。
- 两仓库 CRLF-aware `git diff --check` 通过。没有修改 vendored 警告来获得通过。

首次 GUI 探针发现原有 stub 落后于产品（缺 WARNING 与 SaveBlockMessage），只补齐 presentation fixture；真实保存行为仍由包内组合测试验证。旧 UI 断言检查英文原始异常，现改为精确要求本地化不确定提示，仍要求交易保留，未把失败当成功。早期 Server stage 因 socket 权限和测试误用 DTO 的 Items 字段失败；修正为真实 ItemsOnOffer 后在允许回环网络环境运行最终阶段通过。闭包探针首次输入用了发布排序前缀，入口拒绝，按实际程序集名规范化同一 ZIP 的原字节后通过。全部失败记录与最终证据分开保留，不计通过。

SDK 10.0.114，runtime 10.0.14；固定 seeds 由 stage manifest 保存。进程故障点、命令、源码/profile/fixture/制品指纹见工作区 `Deliverables/Phinix-Rework-P5-First-Slice-2026-11-01/manifest.json` 与各阶段记录。

未进行 Windows/Waylith 真机、真实库存/存读档和资产守恒验收。未部署本次修改、未发布工坊、未推送或合并分支。
