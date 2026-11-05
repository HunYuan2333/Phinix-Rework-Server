# P1 单文件快照替换保护

2026-10-16；SV-17 / TQ-10 / FR-04 的子集。按加速 RoadMap，复用现有工具，不新增 NuGet/故障平台。

## 产品变更与边界

新增中立 `ServerStorage` 程序集（只依赖框架），由宿主配置、认证、用户以及官方 Chat/Trade 的 Save 路径共同使用 `AtomicFileSnapshot.Write`。不修改 Common/客户端、协议或 gitlink；没有 Trade 专属宿主恢复分支。第三方插件可复用该设施，但不会盲目包装任意第三方 IPersistent.Save（其路径/目录/多文件语义尚不明确）。

流程：在目标同一目录创建独占随机临时文件，写入完整序列化数据并 `Flush(true)`，关闭后替换目标；不先删除或截断旧文件，不做跨卷复制 fallback。序列化/flush/替换失败时原目标不因本 writer 提前截断，错误向原保存调用者传播。受控失败清理临时文件，清理错误不覆盖原错误。强杀可能留下临时文件，恢复仍只读取原逻辑文件，不自动选择或提升临时副本。

Linux 新文件以 0600 创建，临时文件也为 0600；既有文件保留 Unix mode bits。没有承诺复制原 owner/group/ACL/安全标签；Windows/网络文件系统未验。目标为目录或符号链接时明确拒绝；部署若此前以文件 symlink 指向数据，应改为真实文件路径或使用目录级挂载。不会沿文件 symlink 改写目标。普通数据目录路径可以沿用。

这只保护一个文件的完整发布，不是跨文件事务、每操作持久化、SQLite/outbox、断电/存储灾难保证。当前仍周期性快照，最新内存操作可能尚未保存；不能把本轮视为“已确认交易不丢”。

## 回归与独立预期

`ServerSnapshotTests` 六组目标：

1. 实际 Config.Save 的旧文件在写入半段后主动异常，逐字节不变，独立 Config.Load 仍读原端口，临时文件清理。
2. 实际 Config.Save 替换后可读新值，新文件权限私有，原 Unix mode bits 保留。
3. 目录目标拒绝且目录不变，无遗留临时文件。
4. 文件 symlink 拒绝，实际目标配置不变。
5. 真实编译 Trade store 创建交易/报价并 Save，重新创建 store / Load 后相同交易 ID 和精确 opaque bytes；未 mock 交易或 serializer。
6. 独立进程通过生产文件写设施写部分临时文件，在测试 callback barrier `snapshot.temp-written-before-publish` 等待；父进程强杀，原快照 bytes 不变，重新创建真实 Trade store 仍能加载原交易和报价。孤立临时文件保持私有且未被提升，测试最后清理隔离目录。

强杀只验证“替换前”的文件写边界，不是 COMMIT/outbox 强杀或断电测试。barrier 仅在测试 Exe，普通生产 peer 无入口。场景不包含真实玩家、游戏 Thing 或库存，不由记录数推导资产守恒。

## 实际命令与证据

Server 基 SHA `5a0508acb703319e60dd5600e3e0facd0397b294` + 在途源码；SDK 10.0.112/runtime 10.0.12，Linux x64，临时文件/测试数据库路径隔离。精确源码/锁文件/构建命令/退出码见 [机器证据](Server-Upgrade-P1-Snapshot-Evidence-2026-10-16.json)。

```sh
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet restore Tests/ServerSnapshotTests/ServerSnapshotTests.csproj --disable-parallel -m:1 -p:BuildInParallel=false -p:RestoreLockedMode=false -p:NuGetAudit=false
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet restore Tests/ServerModelTests/ServerModelTests.csproj --force-evaluate --disable-parallel -m:1 -p:BuildInParallel=false -p:RestoreLockedMode=false -p:NuGetAudit=false
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet build Tests/ServerSnapshotTests/ServerSnapshotTests.csproj -c Release --no-restore -m:1 -p:BuildInParallel=false
dotnet Tests/ServerSnapshotTests/bin/Release/net10.0/ServerSnapshotTests.dll
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p1-snapshot-stage-20261016
```

新增项目引用引起的 Server/模型锁文件更新仅改变项目图，没有增加包或变动已固定 Stateless/Autofac/FsCheck 版本；后续 stage 按 locked restore 验证。制品检查补入 Autofac/Stateless/ServerStorage 必需 DLL，确保插件引用的存储设施被实际交付。存储程序集的 PE 规则禁止依赖业务、protobuf 或 DI/状态机库，避免把业务装进文件设施。

发布进程复用已有 3 例 host_smoke：EOF/SIGTERM、console stop 和插件状态文件不可写；验证新的 Config/protobuf/Trade 路径在真实发布宿主中保存与错误传播。发布/目标构建在 `/tmp/phinix-p1-snapshot-publish-20261016`；最终结果：目标六组、隔离 stage **27 步**、发布宿主 **3 场景**全部通过，publish 33 文件已 hash（含 ServerStorage）。已有生命周期 6 场景、Server 8/13、Common 4/7、安全 17、模型 768、golden 11、supervisor 9 均通过；架构仍保留 8 项既有债务。JSON 逐步结果与最终源码/config hash 已核对一致。没有采集新覆盖率或跑空缓存/完整 Legacy 进程矩阵。

## 尚未完成

P1 不整体退出。SV-11 缓存字节/每会话限制、SV-13 发送边界、SV-15 资产加载失败保护等继续待实施。现有 Framework.LoadExtensionState 仍可能记录加载失败后继续启动，这个问题没有由原子写自动解决；历史坏文件和挂载缺失仍需明确保护。

SV-17 单文件替换和已有的宿主保存互斥有证据；没有跨文件一致恢复点或可靠资产 COMMIT。SQLite、稳定 operationId/结果/outbox、交付状态机和真实游戏重启验收仍按 P2/P3/P5/P6 的最小路径推进。
