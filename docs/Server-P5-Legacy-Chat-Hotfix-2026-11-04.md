# 原版聊天无回执：Adapter 修复与云端配置纠正

日期：2026-11-04，Asia/Singapore。关联 P5/SV-51 原版聊天及故障域隔离。

## 已确认根因

1. 云端 `builtin.legacy-chat-adapter.Enabled=false`，原版 `Chat` 原始模块没有注册接收处理器；旧端因此收不到自身消息的真实 `ChatMessageResponsePacket`。前次部署只开启了原版交易，此为配置遗漏，不是发送成功后客户端应当自行确认。
2. 只读检查当时云端聊天历史的结构：32 条消息，4 条含回复字段。未输出、下载或修改聊天正文。`OriginalChatCodec.CanExpress` 严格禁止回复/艾特，而 `OriginalChatAdapter.History` 原先要求全量都可表达；`Receive` 又把 History 成功当作新普通消息准入条件。即使开启配置，该历史也会让新普通消息收到拒绝。`Stored` 同样以 HistorySent 阻挡独立 live 消息。

## 修复

- 新增 `OriginalChatTextProjection`，仅在 Adapter 出站克隆中，将回复显示为 `[Reply 消息ID] 摘要`、艾特显示为 `[Mentions: UUID]`、公告显示为 `[Notice]`，其后完整保留正文。消息 ID、作者和时间不改；核心/现代端的完整结构不改，不宣称旧端有跳转/提醒能力。严格 codec 本身未放宽。
- `History` 与 `Stored` 使用这项显式文本投影，历史含正常回复不再导致整个历史或客户端连接被拒绝。
- 新消息准入只看真实身份、请求冲突和容量，不以历史加载成功为前置。历史输出过大或不可编码仍明确失败，不伪造空历史、不清数据；新消息仍由实际 Chat 服务处理后返回对应原消息 ID 的真实成功回执。
- 无历史 bootstrap 的独立 live 广播也可发送；失败历史只在登录尝试，不因每条消息重新读取/重发，避免后来全量重复叠加。无法编码的超大单条输出仍保留明确失败边界。
- 配置工具支持 `--original-chat on/off`，可独立调整聊天且保留原版交易开关和其他设置，实际 Config provider 往返验证。没有修改客户端、UI/工坊分支、Common gitlink、协议 schema、SDK pin 或 vendored protobuf。

## 实际验证

- `ServerLegacyChatTests`：62 项通过。包含结构化上下文投影、原数据不变/幂等、超大历史明确失败但普通消息可真实成功/重放、live 不被历史状态阻挡，以及既有主体/发送/日志隔离边界。
- 固定 thomotron/Phinix `ce82b8f2cd57718f05de86458eebd3320f35c4d8` 原版实际 ClientChat 模块 + 现代独立 wire 探针、真实 UDP、独立服务端进程：67 项通过（初始21、重启11、混合原版17、含回复/艾特历史重启10、混合现代8）。验证自己发送得到原服务的真实确认；回复/艾特有明确文本上下文；现代端仍收到完整结构；重启加载含结构化消息的历史后继续收发。
- 完整阶段 49 步 / 62 发布文件检查通过；架构/模型/交易/SQLite/P3 强杀恢复回归保留。
- 配置 Python 回归9项通过；实际 Config provider 的聊天/交易独立 on/off 往返在阶段生命周期检查通过。
- 隔离副本恢复“历史成功才可接收新消息”条件，新增回归精确失败于 `History failure blocked actual new-message success`。不是把错误行为写成新期望。

实际命令经既有 `p4-work/run` 包装器执行：

```sh
dotnet build Tests/ServerLegacyChatTests/ServerLegacyChatTests.csproj -c Release -m:1 -nr:false -p:BuildInParallel=false -p:NuGetAudit=false
dotnet Tests/ServerLegacyChatTests/bin/Release/net10.0/ServerLegacyChatTests.dll
python3 -m unittest discover -s tools/quality -p test_trade_configuration.py -v
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-legacy-chat-fix-stage-20261104 --timeout 120
```

原版真实网络运行器的完整 source/依赖 hashes、命令及 phases 见交付目录 `evidence/native.json`；阶段完整源码 hashes 见 `evidence/stage.json`。profile 为固定原版0.9.7和声明的 Framework profile，seed 不适用；没有游戏 DLL/实际 RimWorld UI 或库存到账验收。

## 部署

沿用用户对开发 ECS 更新/重启的授权，备份原程序、配置及完整数据后，已将修复部署到 `47.93.172.62` 的 `phinix-server`。仍为 UDP/ClientKey、同一 runtime 镜像。原版聊天与交易现在均为 true；50 个实际部署文件 hash 与已验收候选一致，Adapter hash 为 `0647c06d2ca94c018097dc11b7aa1d081a0e5fb9957eed1a347ed11ad6f88be4`。

备份：`/opt/phinix/backups/legacy-chat-hotfix-20261104`。新启动正常加载既有身份、33条聊天与交易状态，监听UDP16200，容器 running=true / restartCount=0。停服阶段曾有已关闭连接的 UserUpdate 发送失败日志；不据此宣称整段日志无错误，也未在聊天补丁中扩大修改用户管理停机路径。没有用测试前旧数据覆盖运行账本。

请旧客户端断开后重连，测试发普通消息、接收回复文本、重连后历史显示。旧客户端无需更新；实际玩家画面是否正常仍待这次人工反馈。此前旧包没有收到 ACK 的消息不推定为已发送，亦不自动补发，以免重复。
