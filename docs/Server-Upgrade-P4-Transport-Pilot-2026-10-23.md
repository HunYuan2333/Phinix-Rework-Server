# P4 / SV-40：安全传输试点（2026-10-23）

> **当前决策（2026-11-01）：TCP＋TLS 已实现，但属于实验性功能，不建议使用。** 默认交付继续采用 UDP / ClientKey。取消本轮现代身份切换及旧登录退役，后续部署、客户端实机验收、证书生命周期和迁移切换工程列为 TODO。已有实验代码和自动化证据保留，不代表正式支持；本文后续启用/迁移步骤仅作实验与历史设计参考，不是部署要求。不得自动启用或在 TLS 认证失败后降级。详见服务端 `docs/Server-Modern-Identity-TODO.md`。


状态：**TLS stream 首个实现/自动验证子集完成，SV-40 整体待完成**。延续用户“继续升级”，在 P3 实现候选基础上推进；没有把 P3 待测的联合/真实游戏验收标为通过。本轮只改 Server 仓库，不触碰 UI/客户端业务分支、共享层 gitlink、protobuf 或 SDK pin。

## 方案比较与当前选择

| 路线 | 依据 | 本轮决定及缺口 |
|---|---|---|
| TLS stream / SslStream | .NET 内置证书/主机名验证与 TLS；Microsoft 说明默认可由 OS 选协议及密码套件 | 先做可执行试点，无外部包。此候选显式只允许 TLS 1.2/1.3，保留 OS 密码套件选择。整个现代 packet stream 都应经过同一安全通道，不能仅保护登录再把资产包发回明文 UDP。TCP 队头阻塞/延迟预算尚未测 |
| QUIC / System.Net.Quic | 官方说明 QUIC 基于 UDP、强制 TLS 1.3；.NET Linux 需要 libmsquic，平台支持受其限制 | 有安全流多路复用能力，但会增加 native 部署及旧客户端适配范围。本轮未安装或试跑，不声称 RimWorld/Mono 支持 |
| 成熟 DTLS 库 | Bouncy Castle C# 官方提供 (D)TLS 能力 | 仅有库能力不等于现有 LiteNetLib 已加密。仍需独立完成连接关联、可靠消息边界/重连、预算与游戏 runtime 验证；本轮不新增依赖或自造密码协议 |

依据（2026-10-23 实际查阅）：[Microsoft SslStream best practices](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-best-practices)、[Microsoft QUIC overview](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)、[Bouncy Castle C# 官方 README](https://github.com/bcgit/bc-csharp/blob/master/README.md)。以上是方案证据；选择 TLS 先试点属于本工程判断，尚非全部客户端的平台选型结论。

## 产品代码与边界

新增独立、中立 `Server/ServerTransport/ServerTransport.csproj`，只依赖 .NET 内置 API。`TlsPacketChannel` 的 Accept/Connect 承担已传入流的 TLS 握手及所有权；参数校验后接管流，握手失败/超时关闭它。没有自动重连、证书失败后的明文 fallback、业务成功 ACK 或交易专属逻辑。

- 客户端必须提供明确服务器名和预先可信分发的 SPKI SHA-256 pin。验证**证书链 + 主机名 + serverAuth 用途 + 有效期 + pin**，pin 不能替代前四项。可选根证书仅代表预先信任的私有 CA；不是从当前不可信连接学习信任。默认在线吊销检查；测试的内存 CA 没有吊销服务，明确使用 NoCheck，不修改生产默认。
- 双方必须协商 `phinix-stream/1` ALPN，握手后再次验证已认证/已加密/已签名及 TLS 版本。错误协议拒绝进入应用层。
- 4 字节网络序长度前缀，长度 1..配置上限；默认 1 MiB，构造上限 4 MiB。先验证长度再分配载荷；逐字节读取/粘包均通过 ReadExactly 处理。不是让超限物品部分成功，未来 adapter 必须按统一准入上限完整拒绝或另行定义整批传输。
- 同一通道允许一个读及一个写；并发第二读/写立即拒绝，没有内部无界队列。发送前复制当前报文，不借用跨 await 的可变 payload。
- 默认握手 10 秒、帧 30 秒，配置有界到 2 分钟。帧中断/取消/超时关闭通道，不让后续消息接在残帧后面。干净 EOF 不产生空包；部分头/载荷 EOF 明确失败。上层必须按原 operation 查询未知结果，不能把连接错误当作退款依据。

这是独立可复用 transport 组件，**尚未引用进正式 Server 启动/端口路径**。现有 UDP 服务没有因本轮变更获得安全承诺；未迁移现有凭据或发送现代秘密。后续接入必须覆盖所有现代认证、会话、聊天、物品和插件报文，继续通过统一 pipeline/业务/账本。现有启动行为不因试点改变。

## 实际自动验证

新增既有风格控制台 `Tests/ServerSecureTransportTests`，真实 TCP 仅绑定 127.0.0.1 临时端口。证书/CA/私钥均由内存 CSPRNG 生成，测试不写密钥、用户配置或凭据；不访问 ECS/玩家服务器。

实际环境：CachyOS/arch-x64，SDK 10.0.113、runtime 10.0.13。Release build 0 warning / 0 error。真实 TLS/网络 **29 项检查、15 组场景通过**；检查结果与 hash 记录在 `/home/hunyuan2333/Phinix/Deliverables/Phinix-Rework-P4-TLS-Pilot-2026-10-23/`。

覆盖：双向完整 opaque 报文/最大边界、客户端发送前超限拒绝后仍可使用通道；错误 pin/主机名/根/过期/serverAuth 用途拒绝；独立原始 SslStream 按字节写入的字面分帧预期；显式 TLS 1.2 对端；外部 ALPN 拒绝；0/超限/uint.MaxValue 长度；载荷截断；同向并发拒绝；取消后通道关闭；干净 EOF；半个头的读超时；无 TLS 输入的握手超时及底层流释放。

```sh
NUGET_PACKAGES=/home/hunyuan2333/.nuget/packages \
DOTNET_CLI_HOME=/tmp/phinix-dotnet-113 \
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks \
dotnet build Tests/ServerSecureTransportTests --no-restore -c Release -m:1 -nr:false -p:NuGetAudit=false -p:BuildInParallel=false
dotnet Tests/ServerSecureTransportTests/bin/Release/net10.0/ServerSecureTransportTests.dll
python3 -m unittest discover -s tools/quality -p test_check.py -v
```

第一次受限沙箱运行在创建 TCP socket 时明确失败，未计通过；之后获准运行仅本机网络的同一合成测试。既有 supervisor 7 项通过。质量入口 `tools/quality/check.py` 增加 restore/build/执行及 JSON 完成标记检查，任一步失败使整体失败；本轮没有重跑完整 stage/远端 CI。

## 下一依赖及未验范围

1. SV-40 剩余：真实 1.6 Mono / 1.7 游戏运行时 API、证书与 ALPN 兼容试验；Debian 容器端试验；确定整包 TLS 接入和普通 host transport 生命周期、连接数/握手并发预算。不能用本轮 .NET 10 对端代替它们。
2. 确定可信服务器身份的用户配置/管理员分发与轮换路径；任何自动首次信任都不能静默接受错误证书。玩家仍自动无密码，不引入手工私钥操作。
3. 安全 transport 完整接入后再推进 SV-41～45 的凭据分离、自动绑定/轮换撤销及迁移操作。保留原 UUID/玩家/交易关联，在安全链路完成前不发送新随机秘密。

本轮没有发布、部署、更换云端容器、提交或推送；没有声称 P4/P3/P6 验收通过。
