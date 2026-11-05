# 服务端交易配额与恢复配置

本配置从 2026-10-22 的 P3 服务端实现起生效；现有 ECS 应用尚未由本任务替换。服务端启动时读取 `server.conf` 的 `ExtensionConfigs` / `builtin.trade` 配置，重启后生效，不热更新运行中策略。

| 字段 | 默认 | 合法值 / 含义 |
|---|---:|---|
| PrincipalRetainedTradeLimit | 32 | 非负整数，0 关闭该主体交易条数限制 |
| PrincipalPayloadByteLimit | 1048576 | 非负整数，0 关闭该主体归源 payload 限制 |
| PrincipalAuxiliaryByteLimit | 65536 | 非负整数，0 关闭该主体 codec/metadata UTF-8 文本限制 |
| RecoveryPageSize | 8 | 1～64，每次恢复页的记录数；保持单条资产批次完整 |
| DiagnosticLogging | false | 详细交易操作/发送追踪开关；不影响失败警告、错误、加载/保存摘要，重启生效 |

`0` 只关闭对应主体配额，仍保留全局 3 MiB 预留预算、4 MiB ledger/operation 值硬上限、网络与引用缓存保护。它不是“无限容量”开关，也不删除历史记录。当前全局 ledger 仍是单 blob，长期终态/Unknown 保留后可能继续拒绝新增；需要后续行级存储与安全归档解决，不能把扩大主体限额当成归档。

条数计入 active、terminal 和 pending/Unknown 的相关 tradeId，弱 ACK、超时、发送 attempt 和重启均不释放。字节配额按原报价来源归属，不是接收者所有物理副本总量。降低配置不会删数据，也不会因为已有数据超额而拒绝加载；成交、取消和已有 Pending 恢复仍走原事务。新的创建/报价准入可能被拒绝。

旧配置缺少新字段时保持默认；显式 0 会保留。负数、恢复页 0/大于 64 或损坏的持久配置明确失败，不静默恢复默认。插件配置坏时对应插件不可用、缺少相应能力并明确拒绝请求；其他健康插件仍按宿主中立规则运行。旧 DatabasePath 字段保留兼容，配置工具不会迁移或改写账本位置。

## 生成配置候选

`ExtensionConfigs` 的值实际为内嵌 DataContract XML，手工拼 JSON 不适用。提供 Python 3 标准库工具，只生成一个新候选，保留其他核心/插件设置，不覆盖原文件或已有输出，不输出完整配置内容。

例如，关闭 32 笔主体条数限制，保留其他保护（以下为示例路径，未在云端执行）：

```sh
python3 tools/configure_trade_limits.py \
  --input /opt/phinix/data/server.conf \
  --output /opt/phinix/data/server.conf.next \
  --retained-trades 0
```

按需增加 `--payload-bytes 2097152`、`--auxiliary-bytes 131072`、`--page-size 4`；也可分别设前两项为 0。单位均为 byte，无隐含 MiB 换算，原字段的所有其他有效值保留。配额上调不绕过全局序列化预算，开发阈值不代表生产工作集验证。

维护窗口先停止唯一服务端实例，备份配置和整个数据目录；在有 Python 3 的管理环境中基于停止后的配置生成、审阅候选，再替换配置并启动。运行时保存可能覆盖外部编辑，因此不直接修改运行中的配置，也不把旧快照生成的候选覆盖到后来已变化的配置上。工具产物继承原文件权限，失败不发布半份候选；候选可能含私有设置，不提交仓库或公开。

本轮没有替换 `/opt/phinix/app`、数据目录或云端容器。完整发布程序集须一起更新，不能只替换一个插件 DLL。实际 Config.Load/GetConfig、旧字段、0、非法值、候选工具及修改配置后独立进程重启均有自动化证据，见 [P3 收口记录](Server-Upgrade-P3-Closeout-2026-10-22.md)。


## 详细交易诊断开关（2026-11-01）

详细诊断默认关闭，排查时可用配置候选工具开启：

```sh
python3 tools/configure_trade_limits.py --input /opt/phinix/data/server.conf --output /opt/phinix/data/server.conf.next --diagnostic-logging on
```

关闭使用 `--diagnostic-logging off`，也可以和配额选项合用。工具只生成候选，不部署；按上文停服备份、审阅替换并重启。旧配置无此字段默认 false，其他配额调整保留原开关。常规失败/恢复异常不依赖此开关，详细追踪也不输出物品载荷或凭据。本次源码修改尚未部署到 ECS。

## 原版交易兼容开关（2026-11-04）

`builtin.original-trade-adapter` / `OriginalTradeServerConfig.Enabled` 默认 **true**。已有配置明确写入 `Enabled=false` 时继续关闭。开启代表采用用户已接受的原版较弱交易规则：基础物品投影、当前报价确认、tokenless 请求不承诺跨重连应用幂等、无可靠交付 ACK。普通 Rework 双方仍使用原有严格规则。

```sh
python3 tools/configure_trade_limits.py --input /opt/phinix/data/server.conf --output /opt/phinix/data/server.conf.next --original-trading on
```

关闭使用 `--original-trading off`。同一工具可合并配额/诊断开关；候选继承原权限，不覆盖输入/已有输出，拒绝损坏或重复的适配器配置，保留其他设置。内嵌 DataContract XML 由工具生成，不能把 JSON 直接填入该节。停止唯一实例、审阅候选后替换并重启；本任务没有执行 ECS 部署。

原版普通聊天仍是独立的 `builtin.legacy-chat-adapter` 开关；交易不要求它开启。交易模块依赖 `builtin.trade`，不依赖聊天。关闭适配器不会删除或重新解释既有账本；已持久保存的基础条款不会自动恢复丢弃的属性。原版首次发出真实 Trading 请求后完成识别；仅登录尚不能支持现代侧主动创建到一个完全未识别的对端。详见 [实施与验收限制](Server-Upgrade-P5-Original-Trade-Network-2026-11-04.md)。

## 原版聊天独立开关（2026-11-04 热修复）

候选工具新增 `--original-chat on/off`，配置键为 `builtin.legacy-chat-adapter.Enabled`，默认 **true**，已有显式关闭设置继续生效。原版交易开关不会自动开启聊天；需要两者时可同时指定 `--original-chat on --original-trading on`。聊天会将回复/艾特显式显示为普通文本，现代端结构不变。云端已在本次热修复中开启两者。仍须停服后审阅/替换候选并重启，工具不覆盖原文件或其他配置。
