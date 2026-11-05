# P0 历史 Rework 基础 wire 与进程恢复切片

2026-10-16；SV-00/02/04/50、TQ-02；补充 FR-14 实际接收包证据。不是完整 Framework profile 或产品持久化交付验收。

## 代码与不变量

`historical_rework_probe.py` 校验固定父 gitlink，archive 历史 Client/Common / Common / protobuf 到临时目录，按原 net472 工程构建三个客户端模块。客户端和当前服务端组件各在独立运行时/进程加载各自固定程序集，避免同进程程序集统一掩盖版本差异。

- 历史来源固定 Client `23643e194c254026b3665ced1e618123d68f7566` / Common `9d0710aaa9b41c07f28d9bfa25599247e7bb1f93` / protobuf `4b0c3aacf0657fbf38253b38918d3358dd4319ec`。当前客户端工作区已推进到 `5366dc7e2b6de0d50a087828324c1c88e94fd223`、Common gitlink `c165f61d749189f700ca896d63d9366d5c71dbf7`；该工作区不参与历史构建，4 个既有 untracked fixture DLL 未动。
- 当前 Server 基 SHA `5a0508acb703319e60dd5600e3e0facd0397b294` + 在途修复；Common `67f243d9edced77dc3fe669f8a435b1f14b59199` / protobuf `4b0c3aa`。currentSourceInputs 记录实际代码，不能只凭基 SHA 声称源码可重现。
- `HistoricalReworkPeerHost` 只组合真实 NetServer、ServerAuthenticator、ServerUserManager。自动认证/登录使用真实组件、serializer、UDP；不是返回成功的 mock。它不是 published Server、插件宿主或现代认证。
- 两个 Mono 客户端进程顺序执行真实认证、登录和用户同步，第二个进程复用私有临时凭据，要求相同 UUID。实际收到的 UserSync/UserUpdate 逐项与白名单重建后的 protobuf 比较，不允许 Username/未知字段；只证明本场景收到的包。
- 历史客户端发出的两份 LoginPacket 在组件处理前采样，只删除 SessionId，其余字段保留；raw bytes 仅内存 hash，私有凭据不进入输出。历史 API 没有设置 AcceptingTrades，不能补成新版默认值。两次网络执行、每次两个进程的派生 hash 均为 `a74aaf9f685bfe3ac0365ea4251033b6e7b0a4650852734e3dddb4d9bf9cbcb8`。
- 独立字段 oracle 使用固定历史 proto 和调用意图，不由被测 Adapter 产生预期。新增补造 AcceptingTrades、替换历史 SHA 两个负对照；来源/hash 在 `Tests/Fixtures/HistoricalReworkLogin/provenance.json`。
- readiness 必须在 deadline 内收到完整有界 JSON 行。半行输出不能无限阻塞；任一客户端失败会清理组件宿主进程组。两个反例通过，失败命令/退出码保留。

只新增 Server 仓库的测试/工具/fixture 与文档，没有修改历史源码、关联仓库、游戏 DLL、协议、gitlink 或产品依赖。测试项目显式引用既有 LiteNetLib / System.Memory，因为直接使用类型和 protobuf 解码；没有新增包版本或修改 vendored protobuf。

## 实际执行与失败记录

Linux x64，.NET SDK 10.0.112 / runtime 10.0.12，Mono 6.12.0；具体环境、源码与程序集 hash 在 JSON。

```sh
python3 tools/quality/historical_rework_probe.py --client-source /home/hunyuan2333/Phinix/Phinix-Rework --common-source /home/hunyuan2333/Phinix/Phinix-Rework/Dependencies/Phinix.Common --protobuf-source /home/hunyuan2333/Phinix/Phinix-Rework-Server/Dependencies/Phinix.Common/Dependencies/protobuf --output /tmp/phinix-p0-historical-rework-wire-20261016
python3 -m unittest discover -s tools/quality -p 'test_*.py' -v
python3 -m unittest discover -s Tests/LegacyGoldenTests -v
python3 tools/quality/check.py --suite stage --isolated --coverage --output /tmp/phinix-p0-historical-stage-network-20261016
git -c core.whitespace=cr-at-eol diff --check -- Server Extensions Tests tools .github .gitignore
```

保留失败而不算通过：首次 `peer-build` 缺显式 LiteNetLib 引用；加入用户包 wire 白名单检查后，producer 缺 protobuf 解码签名所需 System.Memory 引用。都在测试工程补齐现有 DLL 引用，未改旧源码或 SDK pin。首次最终 stage 在已有安全套件通过后，因为 sandbox 禁止 NuGet 代理访问而在固定覆盖率工具安装失败；获准网络访问后在新输出目录重新执行。不是重试忽略业务缺陷。

初始基础网络成功执行在 `/tmp/phinix-p0-historical-rework-fixed-20261016`；最终含 wire 白名单/完整行 deadline 的网络执行在 `/tmp/phinix-p0-historical-rework-wire-20261016`：4 个构建步骤、两个客户端进程、宿主正常退出全部成功，机器输出 `newClientProcessRetainedUuid=true`，两个结果均 `secretFreePublicSync=true`。无随机 seed、无 faultId；就绪/客户端/宿主停止分别有固定超时。

最终隔离 stage **23 步全部通过**：9 项 supervisor、11 项 golden、Server 8 场景/13 断言、Common 4 场景/7 断言、安全 17 场景、属性 3×256 例、架构负对照与 publish。29 个制品文件 hash 完整；架构仍有 8 项显式债务。限定三程序集 coverage 为 373/1102 行、122/482 分支，不能推导整个产品覆盖率。结果以 [机器证据](Server-Upgrade-P0-Historical-Rework-Evidence-2026-10-16.json) 的实际通过记录为准。原包/identity/runtime 私有目录与编译输出清理，源码/依赖 hash 可追溯。

## 范围与下一前置

固定完整 PhinixFrameworkClient 源码直接引用 Verse/UnityData，本轮没有用 stub 冒充完整客户端。未升级 Rework 的完整协商、Framework/Trade、三 profile 混合及原始安全 wire corpus 扩大依赖 TQ-20/50；并未因基础模块通过而把这些标为通过。

P0 来源/构建 spike 在声明子集有证据；下一前置为 SV-05 工作集/负载及预算，干净候选/远端 CI/Docker 仍待验证，P0 未整体退出。服务端强杀/事务持久化、游戏重启、真实库存与存档、现代身份迁移均未由本轮验证。旧端尽力交付策略维持：离线持久保留、重连尝试，已可能发出而未确认保留未知，不盲重发或退款；产品状态机仍待 P3/P5。
