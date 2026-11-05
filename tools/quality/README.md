# 专用服务器检查入口

从服务端仓库根目录执行，Python 3 + .NET 10 SDK；不需游戏 DLL，不启动网络服务。

```sh
python3 -m unittest discover -s tools/quality -p 'test_*.py' -v
python3 tools/quality/check.py --suite quick --output /tmp/phinix-quality-quick
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-quality-stage
```

输出目录必须不存在。quick 执行 Python supervisor/golden、Server build、原管线、固定 Common 核心套件、安全组件、三属性/独立模型及架构套件；stage 再做 publish 与宿主/四个插件制品检查。regressions 是 quick 的同义入口，用于缺陷修复前后留证。命令、退出码、超时、耗时、源码/配置输入 SHA256、Git 状态、SDK、程序集身份和制品 hash 都可在 manifest/文本结果中核对。任何构建、测试或制品检查失败均非零；不会将已知失败算作通过。每子进程默认 180 秒 deadline，可设 `--timeout`。没有自动重试。

`--isolated` 复制允许的源码/工程/固定依赖输入，排除 bin/obj、运行数据和游戏 DLL，使用新临时目录 rebuild/publish，结束后删除临时构建目录。它包含当前工作区改动，并以每文件 hash 标识；**不等于干净 Git revision 或空 NuGet 缓存验证**。manifest 中的 Git SHA 是基础版本，不能代替这些输入 hash。当前白名单包括本轮 C#/Python 源码、锁文件、架构清单和 OriginalLoginChat / OriginalTrade / HistoricalReworkLogin 各两个脱敏 fixture 文件，以及 FrameworkControl 的两个合成规范 fixture 文件；扩大 corpus 必须审核清单。

在新环境获取正式版本时先 checkout 已审核 Server commit，再 `git submodule update --init --recursive`，确认 `git submodule status --recursive` 无 `+/-/U`，运行同一入口。不得用 `--remote`；入口只检查 gitlink，不修改它。MSBuild SDK 覆盖只存在于子进程环境，不改 protobuf SDK pin。Restore 和 build 都显式串行：本轮首次未显式 `-m:1` 的 restore 非零且仅输出 Determining projects，添加串行参数后通过，未定位其根因。为构建可重复性禁用 NuGetAudit，此命令不是依赖漏洞扫描。

安全套件引用真实 Trade service/store、真实 protobuf/Framework serializers 和 UserManager/Auth 程序集，只隔离外部用户目录及发送接口；未 mock 事务成功。用户管理内部发送 seam 仅对测试程序集开放，无客户端可触发的 fault hook。测试只创建合成身份、内存状态和独立目录，不读取运行玩家数据。

FsCheck **3.4.0**（PrivateAssets=all）只在 ServerModelTests 使用，FSharp.Core **5.0.2** 的传递依赖及内容 hash 由 packages.lock.json 锁定，默认 locked restore。原 Exe 均保留。三属性各 256 例，默认 seed `(20261015,20261017)`；每个 shrink 重建独立模型与真实 service。模型不引用产品程序集，当前只建模活跃报价/缓存/接受/逻辑重连；COMMIT、成交、取消、outbox、ACK 和实际断线是后续扩展，不在此冒充通过。

```sh
# 单属性与最小输入重放（先正常 build）
dotnet Tests/ServerModelTests/bin/Release/net10.0/ServerModelTests.dll --property batch --seed 20261015,20261017 --trace '[0]'
# 负对照故意非零；只在一次性源码副本恢复已知缺陷
python3 tools/quality/check.py --suite regressions --isolated --mutation partial-batch --model-property batch --output /tmp/phinix-negative-batch
python3 tools/quality/check.py --suite regressions --isolated --mutation unscoped-cache --model-property scope --output /tmp/phinix-negative-scope
python3 tools/quality/check.py --suite regressions --isolated --mutation early-consume --model-property sequence --output /tmp/phinix-negative-sequence
```

负对照不会把产品缺陷当作通过预期，入口保持非零，记录 mutationId、原/变异源码 hash、FsCheck seed、最小输入/调度和实际重放核对。它不是生产故障注入设施，普通客户端不能启用它。架构套件使用所选 SDK 的 Roslyn 语法树和真实 PE AssemblyRef，记录 8 处既有 LegacyFallback 债务、禁止新增；过渡边界与检查范围在 architecture-scope.json 明列。P5 仍要求违规清零。

原版隔离模块构建与 UDP fixture producer 可独立执行（先获取固定源码和原包，具体命令见本轮证据文档）：

```sh
python3 tools/quality/legacy_probe.py --original-source <original-git-repository> --protobuf-source <git-repository-containing-dd06cc6> --package-cache <restored-package-cache> --framework-root <net472-targeting-pack>/build --output /tmp/phinix-original-probe
```

该入口只 archive 精确原版 SHA 和固定 protobuf，使用原 LiteNetLib 0.7.7.3/Pastel 1.3.1，在临时目录编译真实 Chat/Trading 并以 Mono 执行真实 UDP 自动登录/聊天。临时凭据随目录清理，输出只保留 raw hash 和明确转换的脱敏派生包。正常 quick 用独立字段 oracle 验证这些 fixture，不需要 sibling checkout、Mono 或拉取旧依赖。原版 Trade 报价/客户端模块重建场景由 `--scenario trade` 提供；未升级 Rework 的基础登录 capture 见下文；完整 Framework/Trade 和现代 profile 尚缺。

覆盖率仅在显式 `--coverage` 时采集下述安全组件试点，不能推导全服务端覆盖率；没有 SQLite、L3 独立 published Server Harness、L4 提交/强杀恢复或真实游戏证据。CI 的 `server-quality.yml` 可复用，Docker job 依赖其成功后才继续；只修改工作流，远端执行尚未验证，不能把本机通过当成 CI 通过。

原版 Trade 报价来源子集：在上述 `legacy_probe.py` 命令增加 `--scenario trade`，使用新的 `--output` 目录。执行两个真实原版客户端、三次报价变更并捕获 9 项派生包；不是现代服务端 L3 或旧端交付恢复证明。`Tests/LegacyGoldenTests` 现在同时核对登录/聊天与 Trade；stage 隔离快照包含两个 fixture 目录。

SV-01/TQ-00 完整还原与覆盖率试点：

```sh
python3 tools/quality/check.py --suite stage --isolated --fresh-packages --coverage --output /tmp/phinix-fresh-stage
```

`--fresh-packages` 创建空的 package/HTTP/plugin 缓存，使用仅 nuget.org 的临时配置及空 fallback folders，还原后检查所有 project.assets.json 的 packageFolders，记录下载包内容 hash。无需清除或修改用户缓存。这验证当前 dirty 源码快照从空缓存构建，不等于干净 Git 提交，也不代表完整客户端组合通过。

`--coverage` 仅在一次性源码副本运行固定 Coverlet.Console 6.0.4，以 scope 清单中 SHA512 校验包内容。它再次执行真实 Safety 控制台套件，采集 TradeExtension.Server、Authentication.Server、UserManagement.Server 三个程序集；生成/vendor/test 程序集排除。Cobertura 报告必须存在、程序集范围精确、分母有效且实际命中产品代码；缺失或伪空报告非零。记录行/分支分母及命中数，不设无根据的总覆盖率门槛，不用覆盖率替代不变量。检查采集前后 DLL hash 一致，publish 不包含采集器。

固定工具原包已有旧 rollForwardOnNoCandidateFx 配置，不使用 `dotnet tool install --allow-roll-forward`（试点发现它会加入冲突配置）；不修改包、产品 runtime 或 vendored SDK。现代 runtime 兼容性以实际覆盖率执行和报告为证，不能仅凭工具安装退出码判成功。按加速 RoadMap，CI stage 已取消强制这两参数；仍可手动显式执行，远端执行仍待证实。

历史 Rework 基础模块来源 spike（需要 Mono，当前工作区不参与历史构建）：

```sh
python3 tools/quality/historical_rework_probe.py --client-source <client-git> --common-source <client-common-git> --protobuf-source <git-containing-4b0c3aa> --output /tmp/phinix-historical-login
```

该入口 archive 固定 `23643e1` / `9d0710a` / `4b0c3aa` 并检查父 gitlink；真实原 net472 项目 build，测试 producer 只显式引用这些产物（包括既有 System.Memory），无游戏 DLL。当前服务端真实 auth/users/net 组件在独立 net10 测试宿主中运行；两个独立 Mono 客户端进程自动认证登录，复用临时凭据检查同 UUID，核对收到的 public user 包无 Username/未知字段。真实登录请求只保留移除 SessionId 后的派生包。临时身份和工具/编译输出清理，失败命令与退出码保留。就绪超时含半行输出、客户端失败均清理宿主进程组，有专门反例。

这是 HistoricalRework profile 的 auth/login **子集**。固定完整 Framework 客户端引用 Verse，本轮没有伪造引擎环境，不把这些模块的通过标成完整 profile、Trade 或 published Server L3。独立字段 oracle 对历史 SendLogin 未设置 AcceptingTrades 的事实保持缺省，不补造新版字段。

P1 宿主切片：stage/quick 已包含 ServerLifecycleTests 六个目标场景（Stateless 保存/停机与 Autofac 实际装配根/worker 停止确认）。相关变更日常可只 build/run 该 Exe；无需每次采集或旧端重编译。新的宿主生产依赖精确锁定在 Server/ServerRuntime 与 Server 的锁文件，原 Common/客户端不改。

Linux 独立发布宿主的短 smoke（先按 stage 的方式 publish 并复制官方四个插件 DLL）：

```sh
python3 tools/quality/host_smoke.py --server <publish>/PhinixServer.dll --test-tool <built-tests>/ServerLifecycleTests.dll --output /tmp/phinix-host-smoke
```

使用真实 Config.Save 创建隔离配置和空数据目录，不读取玩家文件。测试 EOF 后 SIGTERM、console stop、将空测试交易状态文件替换为目录触发真实 OS 写失败；检查正常最终保存未被插件注销跳过，失败明确退出。没有测试专用生产网络命令，不是 SQLite/游戏到账/断电证明。输出保留已脱敏的临时宿主日志到 `/tmp`，不要提交这些日志、数据或构建产物。

单文件快照相关修改可只编译/运行 `Tests/ServerSnapshotTests`。其六组场景使用真实 Config/Trade serializer、独立文件预期和一个替换前强杀的测试子进程，不启动网络、不接触玩家数据；测试 root 与孤立临时文件最后清理。stage 已接入该 Exe，并要求 publish 包含 ServerStorage/Stateless/Autofac。临时文件不会被产品 Load 自动提升，单文件替换不代表事务账本或每操作提交。Linux 新文件 0600、原 Unix mode bits 保留；文件 symlink 目标明确拒绝。

加载失败目标回归：`python3 tools/quality/host_load_smoke.py --server <published>/PhinixServer.dll --test-tool Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-host-load`。需先 build 生命周期工具、publish 并携带 Extensions。六个隔离临时目录各先真实启动/stop，再破坏一种持久文件/路径，检查监听前非零退出、原文件 hash 不变和合成私密载荷不进日志；只使用空状态，不代表交易提交或游戏交付验收。需允许本机 UDP；不纳入无网络 stage。

SV-11 暂存缓存现有准入上限：单项编码 UTF-8 1 MiB、owner/connection/session 4 MiB、全局 16 MiB 与 200 条；key 256 UTF-16 code unit、metadata 1024 项，raw 输入在序列化前也检查。安全组件新增四组预算回归，不覆盖解码/inline offer/托管堆或游戏兼容验收；TTL/断线回收和操作绑定仍待 P2。具体执行见 docs/Server-Upgrade-P1-Cache-Evidence-2026-10-16.md。

传输结果检查已纳入原 ServerLifecycleTests 默认入口（8 项基础断言 + 原生命周期检查）。允许本机 UDP 时另执行 `dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --send-network`，使用固定 bundled LiteNetLib、空合成载荷与临时回环端口，验证 API submission、真实 module/bytes 和移除 peer 后拒绝。Submitted 不等于远端 ACK/持久提交，Unknown 不自动重发。此网络入口不纳入无网络 stage；见 docs/Server-Upgrade-P1-Send-Evidence-2026-10-16.md。

通用发送结果与请求 gate 检查现也纳入 Lifecycle 默认入口：插件可从 ExtensionHostContext 取得 ServerRuntime.IResultServerPacketDispatcher。PacketDispatchResult 记录逐目标 NotSent/Submitted/Unknown 与整体失败不确定性；Common 的 void SendMessage 保留。默认入口同时检查配置加载后范围及限制地址拒绝，显式 --send-network 增加实际端口冲突无 worker 检查。当前宿主只接受 Address=0.0.0.0，固定 LiteNetLib 不支持限制地址绑定；不是 IPv4-only 承诺。P1 最小验收及 P2 依赖见 docs/Server-Upgrade-P1-Dispatch-and-Startup-Evidence-2026-10-16.md。

结果报告发布后本次 scoped delivery callback 关闭，晚到调用不会再发送；异步插件需新发起 dispatch。该发布边界也由 Lifecycle 目标回归覆盖。

P2 Registry 的绑定/Ready/失效独立预期已纳入 Lifecycle 默认入口。显式网络检查：`dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --ready-network`（先正常 build，cwd 为服务端根目录，需 Server/bin/Release/net10.0/Extensions）；真实 Auth/User/Framework/正式 Trade 插件经回环 UDP 验证报价快照恢复。仅临时合成数据；不重放含义不明的旧 pending，不代表资产守恒/到账。私有反射过期只在测试，生产普通客户端无 fault hook。此入口不默认要求无网络 stage，详见 docs/Server-Upgrade-P2-Session-Ready-Evidence-2026-10-17.md。

P2 admission control 增量：stage 白名单增加 `Tests/Fixtures/FrameworkControl/{normalized,provenance}.json`，是有 producer/hash 的合成规范包，不冒充历史抓包；Lifecycle 检查其 hash 和拒绝语义。客户端仓库的 `Tests/FrameworkAdmissionRuntimeTests` 独立解码同包并执行产品策略。阶段默认仍不要求客户端仓库或 UDP。

允许本机 UDP 的候选检查（先 publish Server、复制 build 的 Extensions，build 客户端上述测试项目）：

```sh
python3 tools/quality/admission_probe.py --server /tmp/phinix-control-publish-20261017/PhinixServer.dll --test-tool Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --client-probe /home/hunyuan2333/Phinix/Phinix-Rework/Tests/FrameworkAdmissionRuntimeTests/bin/Release/net10.0/FrameworkAdmissionRuntimeTests.dll --output /tmp/phinix-control-process-new
```

两个独立进程使用真实认证/登录/Framework 编码和传输，验证版本及能力拒绝关联、缺业务能力仍收到控制消息、不降级/不重发。仅合成身份和临时持久目录，输出目录必须不存在；startup/client/shutdown 有界并清理自有进程组。manifest 包含命令、输入/输出 hash 和退出码。它补充 README 较早的“没有 published Server L3”记录，仅属于 L3 控制通道子集，未补齐三 profile、schema、业务资产或游戏交付矩阵。具体范围及联合版本见 docs/Server-Upgrade-P2-Admission-Control-Evidence-2026-10-17.md。

P3 中立 journal 首片已接入 quick/stage：新增 `journal-restore/build/test` 三步（该首片 stage 为 30 步，最新增量见下文）。单独检查：build `Tests/ServerJournalTests/ServerJournalTests.csproj`，再运行 `python3 tools/quality/journal_check.py Tests/ServerJournalTests/bin/Release/net10.0/ServerJournalTests.dll`。真实文件 SQLite 验证状态/结果/Pending 同事务、重复/冲突、回滚、mutation budget、writer busy、备份、schema/损坏/缺失保护和提交前后两个 barrier 强杀。Python sqlite3 独立检查合成备份的逐字段/指纹/字节预期；记录数只用于结构性回滚，不证明游戏资产守恒。测试无网络、无生产故障 hook，不读取玩家数据；stage 的制品检查加入 managed/native SQLite 依赖。它只是 SV-30/35 与 TQ-30 的基础子集，正式 Trade 尚未接入，见 docs/Server-Upgrade-P3-Journal-Evidence-2026-10-17.md。


正式 Trade 持久化已接入 `trade-persistence-restore/build/test`，当前 stage 为 33 步。单独 build `Tests/ServerTradePersistenceTests` 后执行 `python3 tools/quality/trade_persistence_check.py Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll`。独立 SQL/XML oracle 检查实际业务账本、原结果与 recipient/Unknown/ACK evidence；不会把弱 ACK 或表记录数当作游戏保存证明。最新宿主 smoke 是 3+9 项，Trade 封存源不再依赖停机改写；以仍需要最终 Save 的 Chat 记录检测宿主通用最终保存，Trade 检查封存源与新 DB。

允许 UDP 时运行实际发布进程的恢复子集（客户端仓库先 build Tests/FrameworkAdmissionRuntimeTests）：

```sh
python3 tools/quality/trade_network_probe.py --server /tmp/phinix-trade-durable-publish-20261017/PhinixServer.dll --test-tool Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --client-probe /home/hunyuan2333/Phinix/Phinix-Rework/Tests/FrameworkAdmissionRuntimeTests/bin/Release/net10.0/FrameworkAdmissionRuntimeTests.dll --output /tmp/phinix-trade-wire-new
```

使用 2 个独立客户端 peer，三个 server/client 阶段（initial、restart、login-failure），包括实际 SIGKILL、原 UUID/报价/操作重放、独立 SQL version=2 检查和身份快照拒绝。客户端确实发送历史协议要求的 UserUpdate，不假设 Login 应用 AcceptingTrades。synthetic key 文件只在私有临时目录，清理后不复制到结果；manifest 仅记录命令/hash/阶段和结果。没有真实 RimWorld、库存、存档或全 profile 验收。SDK 当前 10.0.113，使用现有 cache 时可设置 DOTNET_CLI_HOME 与 NUGET_PACKAGES，保留 vendor SDK pin。完整限定见 docs/Server-Upgrade-P3-Trade-Durable-Evidence-2026-10-17.md。
