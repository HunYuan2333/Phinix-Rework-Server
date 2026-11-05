# P5：800白银报价拒绝与回复昵称修复

2026-11-04（Asia/Singapore）。关联 SV-51/52 的旧端展示/资产表示、TQ 真实引用网络回归与 FR 整批/持久结果边界。Server `dev` 基线 `5a0508acb703319e60dd5600e3e0facd0397b294` 加已有与本轮未提交修改；完整源码身份以 `evidence/stage.json` 逐文件 SHA 为准。

## 证据与根因

用户日志在 01:47 多次报价时报告 `Trade offer rejected: invalid target or incomplete item batch`，并伴随两次 `NO handler claimed core.item.scribe-v1`。实际客户端在该返回 false 后仍发送预先构建的物品包，所以不能把这条警告本身当作未发物品的证据。用户确认报价为800白银，提交时尚未进入成交。

本地实际游戏 Def：`Items_Resource_Stuff.xml` 的 Silver 为 `useHitPoints=false`、`stackLimit=500`。对1.7参考游戏程序集进行只读IL核对，`Verse.Thing` 构造器明确初始化 `hitPointsInt=-1`；该程序集 SHA-256 为 `3D4F1DDFE71BD8760CB488255E3BDEAC9366F537DC1EF813CC6F8D09824E2DA9`，未复制游戏DLL到发布包。

Adapter 原来的 `HitPoints < 0` 校验把合法“不适用耐久”哨兵拒绝。新增500+300白银回归在修复前稳定抛出 `OriginalItemInvalidValue`，证据见 `evidence/silver-before-fix.txt`。客户端日志没有原始物品载荷，因此不声称恢复了那次线上包的全部字段；但已证明实际游戏规则会触发相同拒绝链，并覆盖用户描述的报价规模。

昵称方面：服务端玩家目录保存当前显示名，旧聊天记录没有每条发送时昵称。截图中的 Reply 后面实际是被回复消息ID，不能直接拿它当玩家UUID查询名字，更不能把当前昵称冒充历史昵称。

## 修改

- `OriginalTradeItemCodec.cs`：只将耐久下界从0修正为合法哨兵-1；保留-1，不归零，不按Silver专门绕过；-2及更小仍拒绝，其他整批、数量、主体、字段、深度检查保留。旧端输入和现代基础投影共用同一正确规则。
- `OriginalTradeMixedProbe.cs` / 原版 `TradeChecks.cs`：现代端改走物品包+两个引用的真实路径，报价500+300白银（HP=-1），固定原版 ClientTrading 解码报价和完成事件时均验证完整两堆。过去 inline 测试未覆盖这个实际入口，此次补齐，不以合计数量替代逐项验证。
- `ChatMessageNames.cs` / `PhinixFrameworkChatService.cs`：在消息被服务端接受时捕获公开显示名，不读取认证用户名/ClientKey。昵称快照与消息在同一锁内绑定、同一原子历史文件写入；改名不重写旧快照，历史淘汰同步淘汰名字。
- 本地历史文件追加 server-owned protobuf字段1000，内容为版本1的昵称记录；没有修改Common或任何网络包。旧文件缺此字段表示名字未知，不根据今日目录补造过去快照。加载会核对消息ID/作者及版本/重复记录；展示名记录有大小边界。旧程序可忽略未知字段读取正文，但回退旧程序重新保存会丢弃新增昵称快照；资产存储不受此展示格式影响。
- `OriginalChatAdapter.cs` / `OriginalChatTextProjection.cs`：先按回复消息ID找对应消息的发送时昵称；存量没有快照时，若原消息仍在保留历史中，显示当前昵称并标明 `(current name)`；否则显示 `unknown author`，不再把长消息ID充当昵称。艾特查询当前显示名并同样标注。只改变旧端文本投影，不改核心消息ID/作者/时间、现代端结构或任何资产结果。

没有修改客户端、工坊/UI分支、Common gitlink、协议schema、SDK pin 或 vendored protobuf。两项修复仅需服务端更新。

## 实际测试

- 交易组件107项、聊天组件70项通过：含合法/非法耐久边界、真实SQLite整批/引用与幂等结果；昵称改名后不变、保存/重启不变、旧格式不伪造历史名字、旧端实际文本包及有界淘汰。
- 原版/现代混合交易真实UDP89项通过（原版普通42、重启23、混合原版15、现代9）；800白银两堆经缓存引用、报价和完成均无遗漏或哨兵值改写。旧端无ACK记录仍保留Unknown，正常停启的独立SQLite检查保留。
- 聊天真实UDP69项通过（初始21、重启11、混合原版19、结构化历史重启10、现代8）；旧端回复使用名字，现代消息结构不改。
- 完整阶段49步 / 62个发布文件检查通过；架构债务0，既有独立模型、SQLite、P3强杀/恢复通过。没有把Headless物品计数当成真实游戏资产守恒或完成实机验收。

实际入口均通过既有 `p4-work/run` 环境执行：

```sh
dotnet build Tests/ServerLegacyChatTests/ServerLegacyChatTests.csproj -c Release -m:1 -nr:false -p:BuildInParallel=false -p:NuGetAudit=false
dotnet Tests/ServerLegacyChatTests/bin/Release/net10.0/ServerLegacyChatTests.dll
dotnet build Tests/ServerOriginalTradeAdapterTests/ServerOriginalTradeAdapterTests.csproj -c Release -m:1 -nr:false -p:BuildInParallel=false -p:NuGetAudit=false
dotnet Tests/ServerOriginalTradeAdapterTests/bin/Release/net10.0/ServerOriginalTradeAdapterTests.dll
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-silver-names-stage-20261104 --timeout 120
```

两个真实网络运行器完整命令、原版/protobuf/依赖固定SHA、profile与输出hash见 `evidence/trade-native.json` 和 `evidence/chat-native.json`。seed不适用于此确定性数据流程。

## 云端部署与待验证

按既有开发ECS授权，已部署到 `47.93.172.62` / `phinix-server`，保持原UDP/ClientKey、同一运行时镜像、聊天和交易开关均on。停服后备份程序/配置/完整数据到 `/opt/phinix/backups/silver-names-hotfix-20261104`，未恢复旧快照或编辑交易账本。

实际50个部署文件hash全部吻合。新启动正常加载既有身份、40条聊天和交易状态，UDP16200监听，running=true、restartCount=0，本次启动错误计数0。Adapter SHA为 `f746aee4e1f033b7dd404bf5b8f20cd0df8f2a2368a0f228b2f464e9147afbae`；Chat服务 SHA为 `c6b86452af93ef2cf5168607effdbec37c914d4facc391f8013ca5c57ec2ad9f`。

请客户端重连、新建交易再尝试800白银，分别验更新报价、完成/取消和双方保存读档库存。既有已拒绝operationId仍保持原拒绝结果，补丁不把旧失败重写成成功，不自动重发或补物。尚未执行此次修复的Windows/Waylith/RimWorld实机资产验收。
