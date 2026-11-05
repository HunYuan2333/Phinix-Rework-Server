# P1 宿主保存/停机与状态机、装配根切片

2026-10-16；SV-14/17/18、TQ-10 的最小子集。遵循加速 RoadMap，不扩建完整故障/性能平台。

## 交付与依赖原因

- `ServerSaveLifecycle` 实际使用 Stateless 5.20.1，显式表示保存/停机及失败。同步所有保存，合并重叠 timer tick，停机提前禁止新保存、排空在途后执行一次 quiesce/最终保存；失败保留失败状态，不隐式重复停机写入。Stateless 回调中没有文件/网络/业务操作，互斥由外部锁提供。
- `ServerComposition` 实际使用 Autofac 9.3.2，在应用 lifetime scope 内复用核心中立组件，释放已成功创建的连接与认证 timer。Common、客户端与插件契约不引入容器；现有插件 registry 和 static command 桥梁保持，不全图改写。
- ServerRuntime 与 Server 精确 PackageReference、packages.lock.json/locked restore。版本/内容 hash 与发布依赖在 JSON，必要增量为两个成熟库；没有新增 Common SDK 渠道或变动共享层 gitlink。
- stdin EOF 禁用控制台读取并等待停止信号；SIGTERM/console stop 共用路径。最终保存异常只记录类型并非零退出。传输线程 Join 超时抛出失败、保留引用可再次清理，不把活线程清空后冒充停止。
- `TrySaveExtensionState` 新增聚合结果，原 `void SaveExtensionState` ABI 保留。Registry 关闭插件会注销持久化登记，原停机后的最终保存可能遍历空列表；宿主现在保留通用登记并在关闭后逐项保存，失败可见。没有 Chat/Trade 特判。
- 阶段入口加入六场景目标 Exe；CI 移除强制空缓存/coverage，保留阶段 build/test/publish。日常用受影响目标测试。真实发布进程 smoke 是独立 Linux 入口，不默认放每次 quick。

官方依据：[Stateless](https://github.com/dotnet-state-machine/stateless)、[Autofac scope/ownership](https://docs.autofac.org/en/latest/lifetime/)。库只提供状态规则与装配/释放；交易 SQLite、操作幂等和 outbox 不能被它们代替。

## 已执行检查

服务端基 SHA `5a0508acb703319e60dd5600e3e0facd0397b294` + 在途源码，Common `67f243d9edced77dc3fe669f8a435b1f14b59199`、protobuf `4b0c3aacf0657fbf38253b38918d3358dd4319ec` 未变；SDK 10.0.112/runtime 10.0.12，Linux x64。无随机 seed，故障点为测试方法/OS 文件路径障碍，不宣称运行时稳定 faultId 平台已实现。

```sh
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet restore Tests/ServerLifecycleTests/ServerLifecycleTests.csproj --disable-parallel -m:1 -p:BuildInParallel=false -p:RestoreLockedMode=false -p:NuGetAudit=false
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet build Tests/ServerLifecycleTests/ServerLifecycleTests.csproj -c Release --no-restore -m:1 -p:BuildInParallel=false
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p1-lifecycle-accepted-stage-20261016
```

锁文件生成时显式解锁，随后 stage 按 locked restore 验证；未改 SDK pin。目标六场景覆盖：保存→quiesce→最终保存顺序及重复停止、周期写失败与后续恢复、最终写失败且不伪成功、确定 barrier 下并发保存/停机与 timer 合并、实际 Autofac 作用域共享/隔离/释放、真实 NetServer worker Join 超时后引用保留并在释放后确认退出。不是模拟业务提交成功。

发布和目标检查的实际逐步命令在 `/tmp/phinix-p1-lifecycle-reviewed-build-20261016/manifest.json`，SDK 覆盖仅为子进程环境。独立宿主命令：

```sh
python3 tools/quality/host_smoke.py --server /tmp/phinix-p1-lifecycle-reviewed-build-20261016/publish/PhinixServer.dll --test-tool /home/hunyuan2333/Phinix/Phinix-Rework-Server/Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-p1-lifecycle-host-reviewed-20261016
```

真实宿主 3 场景通过：stdin EOF 后仍存活、SIGTERM 退出 0；console stop 退出 0；空交易状态文件改为目录，真实保存失败退出 1。正常两例还将原文件 mtime 设旧，确认关闭后的最终插件保存确实发生；失败例不可仅以“已有文件”判成功。EOF 半秒观察 CPU ticks=0/100Hz，仅记录，不当生产预算。

首次该 OS 写失败反例 `/tmp/phinix-p1-lifecycle-host-smoke-20261016` 观察退出 0，正确判失败，捕捉到注销登记后跳过保存的实际缺陷。修复后同一目标断言通过，失败 manifest 保留，未将错误退出 0 改成期望。

最终隔离 stage **24 步全部通过**：六个目标场景、Server 8 场景/13 断言、Common 4 场景/7 断言、安全 17 场景、模型 3×256、golden 11、supervisor 9 及架构检查通过；8 项既有架构债务仍明列。publish 31 文件均记录 hash，含新增 Autofac/Stateless 运行时依赖。

最终 stage/制品、源码和依赖 hash、目标与宿主结果见 [机器证据](Server-Upgrade-P1-Lifecycle-Evidence-2026-10-16.json)。没有新增本轮 coverage/空缓存，历史覆盖率不代表新生命周期代码。

## 剩余限制

P1 未整体验收。XML/protobuf Save 仍可直接覆盖原文件，尚无同目录临时文件原子替换/跨文件事务，进程强杀仍可能损坏或丢失最近快照。插件 worker/注册生命周期的完整故障保护、加载损坏保护、有界日志以及 SV-11/13 还需后续切片。插件关闭若自行销毁所登记的持久对象，其最终 Save 会明确失败，需要对应插件修复，不能宣称任意插件具备相同持久语义。

Autofac 仅接管核心应用作用域，Framework/插件仍沿现有显式关闭流程；不是完整 DI 重构。Stateless 当前只用于宿主内存生命周期，交易状态机/SQLite/outbox/operationId/现代身份仍未实现。真实 RimWorld 存档/库存、客户端重启资产恢复、断电/卷满/存储灾难均未验。本轮只隔离空测试数据，未读改真实玩家文件。
