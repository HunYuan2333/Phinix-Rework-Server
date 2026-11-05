# P0 续验：框架试点、独立模型和原版模块

日期：2026-10-15。本切片关联 SV-01/02/03、TQ-00/01/02、FR-01/14 与 PF-07，扩展首轮真实产品修复的测试证据。P0 仍未达到阶段退出门槛。[机器结果](Server-Upgrade-P0-Model-Evidence-2026-10-15.json) 保留每次源码/配置/依赖 hash、命令、退出码、seed、变异、最小序列和程序集证据。四仓库分支与 gitlink 保持首轮记录，未修改客户端、独立 Common 或原版仓库。

## 框架与模型

采用现有控制台入口 + **FsCheck 3.4.0** 的小试点，未迁移旧测试。FsCheck 及传递的 FSharp.Core **5.0.2** 只属于 ServerModelTests，PrivateAssets=all；packages.lock.json 固定版本和内容 hash，默认 locked restore。新增依赖的具体用途是生成/缩减/重放三项报价属性，不进入产品图。参考 [官方运行/重放说明](https://fscheck.github.io/FsCheck/RunningTests.html)，实际可用性由本机编译和正负试验确认，没有采用实验性全套状态机 API。

独立 ServerReferenceModel 程序集只有 System.Runtime/Collections/Linq 引用，不引用产品、协议或 Adapter。它描述主体与 session 的引用可用性、全批报价、接受状态和版本；Subject 才调用真实 service/store/序列化器。每个生成候选、shrink 和重放均创建新模型和真实服务，不复用被测转换作为 expected。所有资产为合成 opaque lineage/bytes。

| 属性 | 正向证据 | 负对照 |
|---|---|---|
| batch：完整性、拒绝无副作用、拒绝后合法引用可用 | 256 例 | partial-batch 变异在第 3 个输入失败，经 1 次缩减得到 `[0]`，对应缺失引用；重放一致 |
| scope：同 ID 的 owner/session 独立性 | 256 例 | unscoped-cache 变异在首例空输入失败，最小两步为两个 owner 发布同 ID；重放一致 |
| sequence：缓存/报价/接受/非法目标/逻辑重连调度 | 256 例 | early-consume 变异在首例失败，最小四步为缓存、接受、含缺失引用的拒绝、合法引用重试；重放一致 |

seed 固定 `(20261015,20261017)`。框架自身的故意错误属性执行两次，均在第 3 例失败、2 次缩减，从 `[-1,0]` 缩减到 `[0,0]`，失败文本/seed 重放相同。此项只验证框架；三个产品源码变异分别验证正确 oracle 能捕捉真实组件缺陷，检查入口保持退出 **1**，没有把产品错误计成通过。

变异只写 disposable source snapshot；非 isolated 模式直接拒绝，且 supervisor 回归核对原产品源码 hash 不变。mutationId 不是 FI runtime 故障点，没有生产客户端可触发的 hook。序列中的 reconnect 只改变测试会话 scope，不冒充真实 socket 断线或 session gate 验证。模型尚不覆盖完成、取消、COMMIT、outbox、持久 ACK 或真实资产守恒，按 P3 扩展。

## 架构与持续检查

ServerArchitectureTests 使用所选 SDK 自带的 Roslyn 语法树与真实 PE AssemblyRef，不将 Roslyn 加入产品依赖。检查声明范围中的 domain 不接收 packet/context/旧凭据/协议分支，宿主不引用官方 Chat/Trade 业务程序集；独立模型不引用产品。关键目录出现未分类新文件会失败。语法负对照验证类型/协议条件违规会失败，注释和无条件说明字符串不误报。

实际记录 **8** 处宿主 ServerPipelineRunner.LegacyFallback 引用，并按文件/方法/标识符/数量锁定 baseline；这是禁止增长的债务记录，不是边界完全合格。当前 Chat/Trade service 与 Framework 输入边界混合、未编译的历史适配文件也明确分类，未将它们装作独立领域程序集。P5/SV-54 必须迁移并清零，当前检查不覆盖整个项目所有潜在间接依赖。

正常入口累计执行：4 个 Python supervisor 检查、4 个独立 golden 检查、原 Server **8 cases / 13 assertions**、固定 Common **4 cases / 7 assertions**、安全组件 **17** 场景、三属性 **768** 例、架构依赖/基线及 4 个语法反例，然后 publish/制品检查。当前没有 coverage collector，不报告覆盖百分比。

CI 新增的 quality workflow 可复用，原 Docker job 增加 needs: quality。setup-dotnet/upload-artifact 引用经官方仓库只读查 ref 后固定 commit；工作流 YAML 已本机解析，**远端 Actions 和 Docker 均未执行**。发布变量、SDK pin 和现有镜像推送授权没有改变，本轮未 push。

## 原版模块与 fixture 来源

原版 `ce82b8f2cd57718f05de86458eebd3320f35c4d8`、其 protobuf gitlink `dd06cc6c8f906b760ef224a085f45e7694dc426a`、LiteNetLib **0.7.7.3**、Pastel **1.3.1** 均按固定来源获取到隔离目录。只 archive 345 个相关 source/project/proto 输入，未 checkout/更新原版仓库子模块。真实原版 Chat/Trading net472 编译通过，调用参数仅指定 targeting pack 路径、禁用旧 SourceLink 查询，不修改原版或 vendored csproj。

实际前置失败也保留：普通 dotnet build 首先缺 net472 targeting pack；向 classic 图强行传 TargetFrameworks 又触发 outer-build 错误；正确路径/单 TargetFramework 后，旧 SourceLink beta task 无法在 .NET 10 加载。最后通过命令行关闭 source-control/source-link 查询完成构建。这些是构建适配诊断，不是对 flaky 测试重试。

独立 LegacyModuleProbe **net472/Mono 6.12.0** 使用真实旧 Client/Server 认证、用户和 Chat 模块、真实旧 serializer 和 LiteNetLib UDP，单独进程/隔离临时目录执行自动认证→登录→ClientChat.Send→真实成功回执。原版 Thread.Abort 生命周期需 Mono/Framework，本轮不把它迁入现代 Server。

正式 legacy_probe.py 入口已重复完成 protobuf restore、Chat build、Trading build、producer build、UDP smoke，全部退出 **0**；另一次初步 smoke 同样通过。三项派生 fixture hash 在两次运行间一致，保留 raw hash 与脱敏变换，原始秘密包/凭据文件不保留。来源、9 个关键源码 hash、producer/runtime/程序集 hash 和限制见 [fixture provenance](../Tests/Fixtures/OriginalLoginChat/provenance.json)。

它们是 **脱敏派生 capture**，不是原始 raw corpus，也不是新 Server Legacy 接入通过。没有补造原版 handshake/deliveryId/ACK；原版 Trade 只完成模块构建，未执行交易网络场景。未升级 Rework capture、现代 profile、真正独立 published 新 Server Harness 仍待后续。

## 复现命令与缺口

```sh
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p0-final-reviewed
python3 tools/quality/check.py --suite regressions --isolated --mutation partial-batch --model-property batch --output /tmp/phinix-p0-model-negative-batch
python3 tools/quality/check.py --suite regressions --isolated --mutation unscoped-cache --model-property scope --output /tmp/phinix-p0-model-negative-scope
python3 tools/quality/check.py --suite regressions --isolated --mutation early-consume --model-property sequence --output /tmp/phinix-p0-model-negative-sequence
python3 tools/quality/legacy_probe.py --original-source /home/hunyuan2333/Phinix/Phinix --protobuf-source /tmp/phinix-legacy-protobuf --package-cache /tmp/phinix-legacy-spike/packages --framework-root /home/hunyuan2333/.nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build --output /tmp/phinix-p0-original-acceptance
```

旧源码获取使用 `git archive` 固定原版 SHA；临时 protobuf 仓库用 `git fetch --depth=1 https://github.com/thomotron/protobuf.git dd06cc6c8f906b760ef224a085f45e7694dc426a`，无需原工作区更新子模块。包还原输入版本为原版 packages.config 中的精确版本。两份入口都在 finally 清理临时构建和运行目录，结果目录必须是新目录。原始本机日志只留 /tmp，仓库内只保存脱敏 fixture 与机器摘要，不保存二进制或日志。

尚缺 SV-05 动态工作集/预算、完整空缓存图、coverage、原版 Trade/未升级 Rework wire 范围，以及远端 CI/Docker 证据，因此 P0 未退出。FR-02/03/12 的持久提交/outbox/ACK 仍是阻断；真实 RimWorld 物化、库存、Scribe、客户端 escrow/工坊凭据迁移均未验。下一项从这些 P0 缺口继续，不能直接跳入 P2 或宣称可靠资产替换服务可发布。
