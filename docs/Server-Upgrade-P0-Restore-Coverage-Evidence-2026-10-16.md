# P0 完整还原与覆盖率采集切片

2026-10-16。关联 SV-01 / TQ-00；保留原 Exe 测试和所有在途产品修复。产品协议/业务和各仓库 gitlink 本轮未变。

## 实现与不变量

`tools/quality/check.py` 新增 `--fresh-packages` / `--coverage`，两者都要求 `--isolated`，避免覆盖率插桩修改工作区程序集，避免空缓存验证依赖既有 bin/obj。

- 空缓存：新建 package、HTTP、plugin cache，仅 nuget.org 的独立配置、禁用 fallback/附加源；全部 restore 串行、强制刷新、禁 HTTP 缓存。结束时逐个读取实际 project.assets.json，确认所有 packageFolders 仅为该私有目录，记录下载包版本与 NuGet 内容 hash。没有清理用户缓存、修改 SDK pin 或依赖已构建的旧 DLL。
- 采集：固定 Coverlet.Console 6.0.4，只安装到临时目录，原 nupkg 文件 SHA512 固定在 coverage-scope.json；无产品 PackageReference。执行真实 ServerSafetyRegressionTests 17 场景，收集三个明确产品程序集。排除 generated/vendor/test，记录分母、命中、程序集/源文件列表和报告 hash。
- 证据校验：不存在/空报告、错误程序集范围、无命中、非法分母或混入 vendor 源码均使检查失败；安装成功不能代替实际测试和报告。采集后对比 DLL hash 确认还原，随后执行其余套件与 publish 检查。
- 原入口保持可执行，quick 不显式选 coverage 时明确记录未采集，不填虚假 0%/100%。没有设拍脑袋的总覆盖率门槛；三个程序集的 scoped 覆盖率不能代替资产不变量、SQLite 故障或游戏交付验收。
- 快照清单 hash 改为对复制后的实际输入文件计算，避免把复制前后的另一份工作区内容当作构建输入；仍不声称多文件快照是原子 Git 提交。
- CI stage 接入两参数，Docker quality 前置保持；远端执行未验证。

7 项 Python 检查包括既有非零/timeout 子进程清理/制品 shape/变异隔离，以及新增 coverage 分母/scope/缺失/vendor 反例和 instrumentation 参数必须隔离的反例。

## 实际命令与失败记录

服务端基 SHA `5a0508acb703319e60dd5600e3e0facd0397b294` + dirty source manifest；SDK 10.0.112、runtime 10.0.12、Linux x64。不是干净 Git revision、完整客户端组合或 Docker 验证。

```sh
python3 tools/quality/test_check.py -v
python3 tools/quality/check.py --suite stage --isolated --fresh-packages --coverage --timeout 240 --output /tmp/phinix-p0-fresh-coverage-corrected-20261016
git -c core.whitespace=cr-at-eol diff --check -- Server Extensions Tests tools .github .gitignore
```

框架试点失败保留：最初 `dotnet tool install ... --allow-roll-forward` 产生与原包 `rollForwardOnNoCandidateFx` 冲突的运行配置，工具安装成功但不可执行；换成原包安装方式后 help 可执行，不改包/产品 SDK。首次完整执行 `/tmp/phinix-p0-fresh-coverage-20261016` 在原有构建/安全场景通过后因 collector package hash mismatch 主动失败：实现把 NuGet 元数据内容 hash 与原 nupkg 字节 SHA512 混用。修正为固定包文件字节 hash，保留失败 manifest；不是绕过校验或无限重试。

最终空缓存 stage **23 步全部通过**，51 个包有内容 hash，所有资产文件的 packageFolders 只指向该私有缓存。原 Server 8 场景/13 断言、Common 4 场景/7 断言、安全 17 场景、属性 3×256 例、golden 8 项、supervisor/报告 7 项及架构负对照通过；既有 8 项架构债务仍明列。publish 29 个文件均 hash，未发现 Coverlet/Mono.Cecil/FsCheck/FSharp.Core 测试依赖。

限定三程序集覆盖率：373/1102 行（33.848%），122/482 分支（25.311%）；分母仅为该采集范围，不能读成全产品覆盖率。源码/config/tool/report hash、实际命令/退出码、包清单与覆盖率分母见 [机器证据](Server-Upgrade-P0-Restore-Coverage-Evidence-2026-10-16.json)。Cobertura 原始报告在本轮 `/tmp` 输出目录，临时构建/工具/package cache 清理，不将它们作为产品制品入库。证据 JSON 记录的是本次源码状态，后续变更须重新执行相应检查。

## 剩余门槛

本轮只关闭本地空缓存还原和安全组件 scoped collector 的证据缺口；干净可提交版本、远端 CI、Docker 仍未验。未升级 Rework capture、SV-05 动态工作集与预算继续待补，P0 不整体退出。当前交易仍是 XML 快照；新状态机/SQLite/outbox/旧端未知保留的产品实现未由本轮测试工具代替。下一项按 P0 前置继续，之后 P3/P5 实施已确认交付契约。
