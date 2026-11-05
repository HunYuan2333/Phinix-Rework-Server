# P5 UDP 支持范围收口（2026-11-02）

## 状态与不变量

P5 在当前声明的 UDP / ClientKey 支持范围达到 §14 最小工程退出门槛，可进入 P6。**这不是原版完整交易实现完成，也不是游戏联机验收通过。** SV-52 原版资产交易的完整目标仍未实现，现交付准确拒绝；现代身份/TLS 正式切换仍为 TODO。原版高级业务不承诺全部支持，与 P5 原有“明确支持范围和准确拒绝”的契约一致，不删除这些未实现目标。

本轮关联 SV-50～54、TQ-01/02/20/50、FR-01/02/03/08。保持统一业务服务与账本、整批拒绝、提交后回执、稳定操作结果、主体授权、未知结果不退款及插件平等。没有 Common/客户端/协议/vendor/gitlink/SDK 或 NuGet 变更；工坊取消 hotfix 未合并，未部署 ECS、发布工坊或推送远端。

## 本轮实际修复

- `Extensions/LegacyAdapter/Pipeline/PluginResultAdapter.cs`：旧插件 ABI 的 `LegacyFallback` 只在调用边界转为 Continue；新建结果避免修改插件持有对象，保留 payload、codec 结果及失败信息，诊断失败不改变业务调用结果。显式链接编译到宿主组合程序集，不让 ServerRuntime 反向依赖业务插件。六个现有 Bound 包装器调用该 Adapter。管线删除全部八处旧结果兼容引用，架构 baseline 清零；公共接口与枚举不改。
- `Extensions/Trade/Server/PhinixFrameworkTradeServerService.cs`：创建前检查对方连接的协商状态和交易能力，缺能力时持久拒绝，不建立不可交付交易。检查位于已有 replay 之后，不能因对方断线覆盖已提交结果。能力仅为表达性事实，不作身份授权或游戏到账证明。
- 同文件 `SendCreateFailureDurably`：混合 L3 实测发现错误说明原来只发送、不入账本，导致同请求重放说明消失。现在结果与说明一起提交、一起回读。不对历史无说明记录补造结果。
- `Extensions/LegacyAdapter/Server/OriginalTradeRefusalAdapter.cs`：独立插件明确拒绝 raw `Trading` 并断开对应连接，记录固定原因；不解析不可信载荷、不调用交易业务、不发会触发原版退款的失败 CompleteTradePacket。其他连接保持可用。模块注册与注销成对。
- `Tests/ServerLifecycleTests/PluginResultBoundaryTests.cs`：61 项检查覆盖实际六个 Bound 包装器、所有现有动作、结果不变性/元数据、优先级、主体来源、null、日志异常、业务异常与真实中立管线。移除实际 Adapter 调用的隔离源码负对照被精准捕捉。
- `Tests/ServerLifecycleTests/P3NetworkProbe.cs`、原版探针与 runner：增加真实原版/Framework 混合双向聊天、原版历史、去重和主体校验、接收者会话、无能力交易拒绝、操作查询及断线后原结果重放；结构化艾特保留在 Framework，原版明确断开且不收到扁平化消息。首次聊天探针错误地沿用 Trade 的系统发送者预期，已按既有 Chat envelope/body 身份契约纠正，未放宽会话或主体校验。
- `tools/quality/historical_rework_probe.py`：新增可选 published Main 模式，使用固定历史客户端模块独立构建，真实服务端重启前后验证自动登录、UUID、凭据持久化及公开用户包不含私密字段。原有组件模式保留。
- `Tests/ServerTlsHostTests/SessionTests.cs`：完整 stage 暴露通知测试竞态：Sweep 已在锁内撤销权限，但另一线程尚未发布通知。新增确定性屏障强制该间隔，立即验证无权限、释放后等待通知；不改 TLS 产品行为、过期要求或实验性状态，不靠重复运行规避失败。

## 支持矩阵

| profile/组合 | 本轮支持或准确拒绝 | 实际证据与限制 |
|---|---|---|
| 当前 Framework v2 / UDP / ClientKey | 既有聊天、持久交易、查询和恢复 | 49 步完整 stage；真实 SQLite 文件、两个强杀点、重启与离线 Pending；不证明游戏到账 |
| 原版 thomotron/Phinix 0.9.7 `ce82b8f` | 自动登录、普通聊天、历史及真实操作回执 | 独立原版真实模块/编码器/传输，不依赖 Adapter 生成预期；初始 21 + 重启 11 项 |
| 原版 ↔ Framework v2 | 普通聊天双向共用历史；缺能力交易明确拒绝；不能表达的艾特不扁平化 | 原版混合 16 + Framework 8 项，共 24；与前两阶段合计 56 项真实网络检查 |
| 历史 Rework `23643e1` / Common `9d0710a` | 自动登录、公开用户同步和稳定身份 | 独立历史真实模块对最终 published Main，跨真实服务端重启 UUID 不变；没有补造历史 AcceptingTrades 字段；未声明完整历史 Framework/Trade 已兼容 |
| 原版完整资产交易 | **不支持；明确断开拒绝** | 确认包无报价版本；Reset 在本地返物后才发无 token 空报价。内部报价 bridge 的 66 项组件检查不算公共入口可用；不能补造观察版本、ACK 或退款。原版客户端已自行发生的本地动作无法由拒绝包安全撤销 |
| TCP+TLS / 现代身份迁移 | 实验性、不建议使用；正式切换 TODO | 组件回归继续执行，不算实机/正式部署通过 |

原版 Chat Adapter 仍默认关闭；本轮测试显式开启。原版协议没有 Hello/历史请求，因此开启后登录会发送 raw 历史，随后 Ready 的 Framework 探针忽略其 raw 投影、只消费自身 Framework 流；混合自动化已验证 Ready 后不再收到 raw 广播。真实游戏启动/协商时序和 UI 仍属 P6，不据此扩大所有旧版本承诺。关闭原版 Chat 不影响正常 Framework 主线。

## 实际验证及失败记录

环境：Linux x64，SDK 10.0.114、运行时 10.0.14、Mono 6.12。Server 基础 SHA `5a0508acb703319e60dd5600e3e0facd0397b294`，有累计未提交工作，以本轮 manifest 的逐文件 hash 标识构建。Server Common 固定 `67f243d`，protobuf 固定 `4b0c3aa`；原版 protobuf 固定 `dd06cc6`。没有换 pin。

以下命令均由既有隔离构建 wrapper `.../p4-work/run` 执行；完整参数、SDK、输入 hash 和日志见本轮证据。目录以 `/home/hunyuan2333/Phinix/Phinix-Rework-Server` 为工作目录。

```text
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p5-stage-final-20261102-d --timeout 120
# PASS：49 steps / 62 published files；architecture baselineDebt=0，5 negative controls

dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --plugin-result-boundary
# PASS：61 checks；完整 stage 也调用此用例

python3 tools/quality/original_chat_acceptance.py --original-source /home/hunyuan2333/Phinix/Phinix --pastel-package /tmp/phinix-p5-original-packages/pastel.1.3.1.nupkg --framework-root /home/hunyuan2333/.codex/visualizations/2026/10/15/01a13ec4-18b2-7860-931d-fd14d2747341/p4-work/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build --server /tmp/phinix-p5-closeout-publish-final-20261102/PhinixServer.dll --config-writer /home/hunyuan2333/Phinix/Phinix-Rework-Server/Tests/ServerLegacyChatTests/bin/Release/net10.0/ServerLegacyChatTests.dll --modern-probe /home/hunyuan2333/Phinix/Phinix-Rework-Server/Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-p5-mixed-structured-final-20261102
# PASS：56 actual wire checks；三个独立 Main 阶段

python3 tools/quality/historical_rework_probe.py --client-source /home/hunyuan2333/Phinix/Phinix-Rework-P3-Business --common-source /home/hunyuan2333/Phinix/Phinix-Rework-P3-Business/Dependencies/Phinix.Common --protobuf-source /home/hunyuan2333/Phinix/Phinix-Rework-P3-Business/Dependencies/Phinix.Common/Dependencies/protobuf --server /tmp/phinix-p5-closeout-publish-final-20261102/PhinixServer.dll --config-writer /home/hunyuan2333/Phinix/Phinix-Rework-Server/Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-p5-historical-final-20261102
# PASS：历史真实模块、公开同步脱敏、跨 Main 重启保留 UUID
```

固定 seed 沿用 stage 模型 `20261015` / `20261017`；原版网络与历史登录无随机调度 seed。强杀 faultId：`p3.kill-after-offer-response`、`p3.kill-with-offline-pending`；另有 `config.trade.malformed-section`。组件 SQLite 事务触发器回滚与独立账本检查仍通过，原版报价 66、聊天组件 38、UDP 屏障 6 等均随 stage 验证。

开发中失败全部保留：缺 Chat 契约引用、聊天探针错误预期、原拒绝说明未持久化的真缺陷、配额用例被更早表达性拒绝遮挡、TLS 通知竞态，以及使用不含历史对象的 Common 仓库导致两次归档失败。配额回归另用 Ready 合成对端，仍独立证明配额拒绝；未改为接受错误行为。最终仅最后完整通过轮次记为 PASS。

## 制品与下一依赖

证据及未部署候选：工作区 `Deliverables/Phinix-Rework-P5-Closeout-2026-11-02/`，详见 manifest 与 SHA256SUMS。测试运行目录/合成凭据不进入候选包；包中不含测试程序集、游戏 DLL、配置、日志或玩家数据。

下一依赖 P6：冻结配套业务客户端候选、Windows/Waylith 和真实游戏双端联测，重点验证正常/取消、未保存提示、保存/另存为/重载、断线与服务端重启、库存实际物化和持久接收。不以 Headless、DB 条数或物品总量证明真实资产守恒。Windows/.NET 10.0.13 ECS 与容器运行未由本轮本机测试覆盖；没有自动发布/部署。

SV-52 完整原版资产交易及全版本混合矩阵继续列为未实现/未验证，不用“P5 支持范围收口”把它们记成通过；未来改变旧客户端能力或安全契约时单独评估。P6 联机、P7 部署发布和现代身份 TODO 各自保留状态。
