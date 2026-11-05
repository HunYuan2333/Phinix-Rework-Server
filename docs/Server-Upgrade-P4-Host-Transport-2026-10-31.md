# P4 / SV-40：正式宿主可选TLS传输（2026-10-31）

> **当前决策（2026-11-01）：TCP＋TLS 已实现，但属于实验性功能，不建议使用。** 默认交付继续采用 UDP / ClientKey。取消本轮现代身份切换及旧登录退役，后续部署、客户端实机验收、证书生命周期和迁移切换工程列为 TODO。已有实验代码和自动化证据保留，不代表正式支持；本文后续启用/迁移步骤仅作实验与历史设计参考，不是部署要求。不得自动启用或在 TLS 认证失败后降级。详见服务端 `docs/Server-Modern-Identity-TODO.md`。


状态：TLS服务端正式组合入口已实现；P4整体仍在实施。客户端尚未接入该transport，不允许据此宣称现代游戏业务已全面加密，或开始迁移玩家凭据。

## 基线与关联

Server `dev` HEAD `5a0508acb703319e60dd5600e3e0facd0397b294` 加工作区既有修改；Client P3业务分支HEAD `53c92ce8eb314dcb8ca422ae4b6891a007931128`，本轮没有客户端/共享层/子模块改动。保留所有未提交工作。关联SV-40、TQ-40、FR-10传输接入与关闭子集；SV-41～45身份/迁移不标完成。上次中断的远端49项测试结果未回收，不将其标通过；本轮只读确认旧测试容器已不在运行，现有phinix-server仍为原b98a62b36344实例。

## 实现与不变量

- 新 `Connections.IServerTransport`，保留模块处理器、连接事件、poll续作与明确发送结果。NetServer实现接口，UDP编码及回执定义保持原样。Authentication、UserManagement、Framework、Server只依赖该中立边界；不在业务里判断TLS/UDP/Legacy。
- 新 `TlsNetServer` 将TLS读写与现有同步业务pipeline隔离。所有连接/包/断开事件经同一个poll线程处理，原中立workQueue沿用PollCompleted，非网络线程不会直接执行领域操作。
- 接收事件队列256项、opaque payload全局16MiB、每轮至多32项；超限关闭相关连接，未执行的请求不会伪造成功。连接生命周期用有界await入队，关闭事件不会因TryEnqueue失败而丢失；listener在该关闭完成前仍持有连接名额。
- 每连接出站队列16项/4MiB，发送前复制完整包。Submitted仅表示该有界本地transport队列已接管，不代表TLSflush、远端ACK或游戏持久化。队列满/断开/编码超限为NotSent；入队后的断开不触发退款、重发或协议切换。原P3 operation/result/receipt/outbox语义未改。
- TLS body格式明确为：2字节网络序UTF8模块名字节长度（1..128），模块名，原序列化opaque payload；外部仍由TlsPacketChannel的4字节总长度框定。严格UTF8，保留原protobuf/Framework编码；无旧协议字段补造。独立字面fixture为 `[0,4,116,101,115,116,17,0,255]`，不是产品编码器生成预期。该pilot尚未发布，无已交付TLS客户端格式被替换。
- 接入正式Autofac composition。Config缺少Tls字段时仍创建原UDP服务；Tls存在时仅注册TLS。证书/私钥加载、字段校验、绑定或TLS错误均不注册/启动UDP fallback。
- Tls配置只保存管理员证书/私钥文件路径，不保存私钥内容/密码。TLS连接上限默认32、最大128，并与原MaxConnections取较小值；默认8握手、1MiB完整帧（可至4MiB）。管理员调大连接/帧会提高同时在读frame、解码及出站缓存内存，不宣称16MiB事件预算覆盖全部TLS内存。没有引入新NuGet包；锁文件仅新增内部ServerTransport项目引用边。
- 启停串行，Stop从poll线程调用明确拒绝；关闭时等TLS连接、发送任务、断开事件及poll线程退出。超时保留引用，不清除后伪称已停。启动失败在锁外join poll，端口占用解除后可再次显式启动，避免锁内join或丢弃存活worker。
- 架构检查纳入Connections.Server及ServerTransport目录，禁止Host依赖Chat/Trade具体业务类型；8项既有债务基线未增加。

## 验证范围

真实组件/真实回环TCP/TLS，正式Config/Autofac/ServerAuthenticator/ServerUserManager/Framework pipeline；合成身份、隔离临时凭据/用户文件。测试临时生成合成证书并在唯一测试目录写PEM以验证正式加载路径，退出清理；不收集密钥或凭据到证据/制品。没有Mock业务成功。

- 安全通道/监听/模块封包61项检查：包含原信任/ALPN/分帧负例及独立模块字面预期、非法UTF8/长度/总包预算、可变payload复制；监听关闭、超时与队列准入沿用上次49项验证。移除两个诊断断言的调度竞态，以有界条件等待诊断通知完成，不靠重跑或改错误期望。
- 正式TLS宿主20项：真实认证/登录、两次连接保持UUID、Framework能力协商、独立字面第三方模块往返、所有回调同poll线程、缺连接NotSent、Stop/Start、非法TLS配置与证书缺失不降级、绑定冲突回滚后可重启、真实持久用户/凭据文件存在。
- 旧UDP发送 exact-wire/disconnected-refusal、Ready登录/协商/版本/重复/冲突/到期/重连/插件快照/歧义交付保留/关联拒绝回归；既有生命周期、架构与质量supervisor同步验证。具体最终结果、命令、hash见交付manifest。
- 单进程真实网络组件测试属于L2网络子集；不伪称独立进程真实游戏L3/L4或资产守恒。TLS测试使用过渡既有认证包，不是SV-41～45新身份协议验收。

初始受限沙箱socket13明确失败，获准本机网络后执行；初次依赖恢复在非串行MSBuild下退出未成功，改为既有串行策略`-m:1 -nr:false -p:BuildInParallel=false`后正常。新增内部项目引用使locked restore正确拒绝旧图，显式force-evaluate更新内部引用锁后恢复locked build；保留失败日志，不修改SDK pin/protobuf源码或放宽产品安全校验。既有obsolete/net50分析器警告保留，未作为本轮新增错误掩盖。

主要命令：

```
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks MSBuildEnableWorkloadResolver=false dotnet restore Server/Server.csproj --force-evaluate --disable-parallel -p:RestoreLockedMode=false -p:NuGetAudit=false -m:1 -nr:false -p:BuildInParallel=false
dotnet build Tests/ServerTlsHostTests --no-restore -c Release -m:1 -nr:false -p:BuildInParallel=false -p:NuGetAudit=false
dotnet Tests/ServerTlsHostTests/bin/Release/net10.0/ServerTlsHostTests.dll
dotnet Tests/ServerSecureTransportTests/bin/Release/net10.0/ServerSecureTransportTests.dll
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --send-network
dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --ready-network
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p4-host-stage-20261031 --timeout 120
```

## 配置及下一依赖

只在隔离测试环境使用新Tls配置。示例字段（应由Config序列化为现有server.conf对应命名空间/顺序）：CertificateFile、PrivateKeyFile、MaximumHandshakes、MaximumPacketBytes、ConnectionLimit。没有Tls字段的现有部署保持UDP；当前普通客户端没有TLS选项，切换正式服会使其无法连接，因此本轮不部署。

下一切片在Client P3业务分支接入.NET10安全transport、服务器名/链/SPKI信任与全部真实编码/解码；不得认证失败静默降级。1.6本机Mono缺少当前CertificateChainPolicy API，仍需独立适配/实测；不为兼容取消验证。安全双端接入后再实施稳定principal/凭据分离、撤销轮换及保UUID自动迁移。P3临时debug日志发布前清理待办不变，持久intent/result/receipt不能删除。


## 最终结果补记

隔离stage最终40项全部通过，发布审计61文件；FsCheck三类属性各256例（固定seed 20261015/20261017），原架构8项债务/5项负对照不变；真实SQLite及P3独立进程两次强杀恢复回归通过，不代表TLS资产/真实游戏验收。首次stage明确失败：模型测试锁图未包含ServerTransport；supervisor的/proc先exists后read竞态在进程恰好退出时抛FileNotFound，后续架构又因模型未构建而缺DLL。补齐该测试锁文件，并将/proc读取改为单次open、FileNotFound视为已退出，其他错误继续失败，新增已退出/zombie/活进程断言；修复后重新执行，保留首轮失败。没有重试绕过关键flaky。

ECS上使用现有`phinix-runtime:10.0.13-ca20261022`镜像（实际runtime10.0.13），临时容器`--rm --network none --read-only`、仅/tmp为128MiB tmpfs，非root用户，仅挂载独立合成测试包只读；61项TLS及20项宿主通过。上传包SHA256 `3a7ad0061f35311667c710d1dcc6c152dbbc8b343fde1c59e063ff36dbd98437`，执行前远端校验；容器内程序集hash与本地冻结包匹配。未挂载/app或/data、未重启/替换现有phinix-server，未读取私钥文件。本轮PEM文件只由合成测试在隔离/tmp生成并清理，不含玩家凭据。不将该镜像验证称为原生Debian/Windows/Mono全平台通过。

证据目录：`Deliverables/Phinix-Rework-P4-TLS-Host-2026-10-31/`，包括本地/远端命令、hash、两轮stage及失败、源码副本。只交付证据，不发布新应用包/工坊/分支；当前普通客户端仍无TLS路由，尚未部署。下一项为Client P3安全transport及服务器信任配置，然后继续SV-41～45。
