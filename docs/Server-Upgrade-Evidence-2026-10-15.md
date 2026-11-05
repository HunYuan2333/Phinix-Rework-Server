# 2026-10-15 首轮实施证据

状态：P0 实施中，本轮切片通过，不代表 P0/P1 阶段退出或联合发布验收。[机器可读记录](Server-Upgrade-Evidence-2026-10-15.json) 保存实际命令、退出码、环境、源码/制品 hash、程序集引用和修复前反例。所有输入均为合成身份与资产，未启动玩家服务。

## 实际版本与变更范围（SV-00）

| 仓库 | 分支 / 基础 HEAD | 固定依赖 / 原有修改 |
|---|---|---|
| Server | dev / `5a0508acb703319e60dd5600e3e0facd0397b294` | Common `67f243d9edced77dc3fe669f8a435b1f14b59199`；开始时已有 README/设计边界等 4 个文档修改及 6 个未跟踪评估文档 |
| Client | codex/uiux-release-main / `23643e194c254026b3665ced1e618123d68f7566` | Common `9d0710aaa9b41c07f28d9bfa25599247e7bb1f93`；97 个原有 dirty 条目，含 Common 本地修改，未动 |
| 独立 Common | dev / `85ecb18278cf98c0e4fa9ab6c49f4d7db635b413` | protobuf `4b0c3aacf0657fbf38253b38918d3358dd4319ec`；1 个原有修改，未动 |
| 原版 Phinix | master / `ce82b8f2cd57718f05de86458eebd3320f35c4d8` | protobuf gitlink `dd06cc6c8f906b760ef224a085f45e7694dc426a` 未初始化；工作区干净，未动 |

服务端递归 gitlink 与实际 checkout 一致，未更新任何 gitlink、protobuf 源码或 SDK pin。本轮仅改 Server 产品代码/工程、测试、检查入口及必要文档。没有 commit、push、部署、镜像推送或工坊发布。基础 SHA 加工作区源码 hash 标识候选，不能只引用基础 SHA 当作本轮构建源码。

Server 主图为 net10.0：宿主 → Auth/Connections/UserManagement/ServerRuntime 与固定 Common，官方 Chat/Trade 通过普通插件目录交付；现有 Server 项目仅以 ReferenceOutputAssembly=false 拉入官方插件构建。Client 主图仍是 net472，引用 RimWorld/Unity，未在本轮构建。Common 两个现有 Exe 工程和 Client/原版 CI 均已检查；独立 Common 未见自己的 .github 工作流。原 Server Docker 工作流没有测试，新加 server-quality 工作流尚未远端执行。

当前三份 Common 的 FrameworkPacket、FrameworkSerialization、ChatContracts、TradeContracts 字节 hash 一致，分别为 `bdfa6a074e7be6fe8a37bb6d34e5076616c169ddb0089c12699b3f6088c02598`、`20493c7400f7a906f950e3f63abcf5099e1a51d735897c31735e9d3081b28271`、`1027944580d8530eaa1d2ae6d7140a21ed43e7cba9f8d7b5090528020fe4bf96`、`b296203ff9390fbbdf09c0e8aa39bc882376ffcc71ec8aa3ffffb18855da24db`。这只证明这些文件相同，不等于整个 SDK、客户端/服务端/插件组合通过兼容验收。

## 切片与不变量

- SV-01 / TQ-00：保留旧控制台入口；新增串行 restore/build/测试/publish、源输入清单、manifest、deadline 和制品检查。非零退出、超时与缺少完成标记均失败；不使用无限重试。
- SV-02 / FR-01 / AS-01，提前落实 SV-10：缺失、全缺、重复、空引用、非法目标/参与者整批拒绝；不改报价/版本、不消费合法引用。完整 payload/metadata/顺序原样保存，显式空报价合法。
- SV-11 / PF-07 部分：主体/连接/session 分离；同 ID 不同 payload 拒绝；冻结缓存输入；满额拒绝新包而不淘汰已有引用。尚无 epoch/operation scope、TTL 或字节/每会话预算，不能整体关闭 SV-11。
- SV-12 / FR-14 / PF-06：登录、登出、属性更新及全量同步共用 public user 白名单；锁内取 detached 快照，锁外发送；不将 Username/未知字段带出。凭据到 UUID 的原映射保留。Auth/login 日志移除 bearer/session 内容。

历史无 scope 的公开缓存签名保留以保护 ABI，但写入明确抛 NotSupportedException、读取返回 false；官方 Trade 插件已使用新增 scoped 接口。旧第三方调用者需要迁移，不能继续使用不带身份的资产缓存。没有修改共享 wire，现有失败枚举暂用 InternalServerError 和原 correlation 说明整批拒绝；P2 再联合提供精确通用拒绝码。

## 实际执行与结果

```sh
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet build Server/Server.csproj -c Release -p:BuildInParallel=false -m:1 --no-restore
python3 -m unittest discover -s tools/quality -p 'test_*.py' -v
python3 tools/quality/check.py --suite regressions --output /tmp/phinix-p0-before-serial
python3 tools/quality/check.py --suite regressions --output /tmp/phinix-p0-privacy-before
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p0-reviewed
git -c core.whitespace=cr-at-eol diff --check -- Extensions Server .github tools Tests
```

| 检查 | 层级 / profile | 实际结果 |
|---|---|---|
| 初始 Server 串行 build | 构建 | 退出 0 |
| 修复前资产目标（同样的正确语义断言） | L2 / existing-rework-component | 9 场景 8 失败，runner 退出 1；并未把错误行为设为成功预期 |
| 修复前秘密目标 | L2 / existing-rework-component | 原资产 9 场景通过；新增增量包/auth log 两场景失败，全量 sync 通过，runner 退出 1 |
| 监督器/制品检查反例 | L1 / Python 3.14.8 | 3 个测试通过：子进程非零、超时杀后代、缺制品/游戏 DLL 拒绝 |
| 固定 Common 原套件 | L1 / 固定 gitlink | 4 cases / 7 assertions，通过 |
| 原服务端管线套件 | L1 | 8 cases / 13 assertions，通过 |
| 安全组件套件 | L2 / existing-rework-component | 17 个场景通过，含真实插件 item 入口；完整列表见 JSON |
| 隔离 restore/build/publish | 构建/制品 | 全步骤退出 0；29 个产物记录 hash，含宿主、四个官方插件/contract DLL；无游戏 DLL |
| 改动代码 whitespace 检查 | 静态 | 上述路径通过；原有文档 CRLF 差异未做无关重写 |

最终阶段入口实际子步骤累计约 30.891 秒，使用本机现有 NuGet 缓存、SDK 10.0.112 / runtime 10.0.12；不是空缓存获取、生产性能预算或 Docker 验收。第一次未显式 MSBuild 串行的 restore 非零且无有效诊断，补串行参数后成功，未将其隐瞒或靠无限重试通过。临时隔离源码/构建目录已清理，原始本机结果留在上述独立 /tmp 目录；JSON 摘要保留 hash 和失败证据，不提交日志/二进制产物。

Fixture 为本测试源码生成的合成 opaque payload（`test.opaque-v1`），来源是 ServerSafetyRegressionTests/Program.cs；验收源码 hash 为 `ecff58fecfbbc10452b53bac7005f5769910105f89521ac5bd5428668584e011`。无随机生成，seed=null；未注入持久提交故障，faultIds=[]。这不是原版 Legacy golden capture，不提供游戏物品守恒证明。程序集 full name、引用与二进制 hash 在 JSON，不以 DLL 文件名相同推断 ABI 一致。

## 剩余门槛与下一依赖

P0 尚缺框架小试点、三属性/独立模型/shrink、架构违规基线、独立旧模块与 golden 来源、生产工作集/预算实测；新 CI 尚未执行，也没有覆盖率。详细统一契约草案和客户端 R44 清单见 [P0 契约](Server-Upgrade-P0-Contracts.md)。

FR-02/03/12 仍存在：报价成功未获 SQLite COMMIT、成交与 outbox 未同事务、ACK 无持久 receipt。输入修复不能提供 operationId 持久幂等或崩溃恢复。缓存未消费引用可能占满至重启；本轮不自动淘汰或退款。真实网络三 profile、系统 IO/强杀恢复、可信传输/自动凭据迁移、RimWorld 物化/库存/Scribe/工坊更新都未验收。

下一项继续 TQ-00/01 小试点与独立模型/架构基线，再核验 TQ-02 原版 wire 和 SV-05 预算，满足 P0 后按原依赖推进 P1～P7。
