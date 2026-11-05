# P4 / SV-40：有界TLS监听与关闭（2026-10-30）

> **当前决策（2026-11-01）：TCP＋TLS 已实现，但属于实验性功能，不建议使用。** 默认交付继续采用 UDP / ClientKey。取消本轮现代身份切换及旧登录退役，后续部署、客户端实机验收、证书生命周期和迁移切换工程列为 TODO。已有实验代码和自动化证据保留，不代表正式支持；本文后续启用/迁移步骤仅作实验与历史设计参考，不是部署要求。不得自动启用或在 TLS 认证失败后降级。详见服务端 `docs/Server-Modern-Identity-TODO.md`。


状态：首个待实现前置子集完成，SV-40及P4整体仍在实施；没有启动凭据迁移或声称现代业务已加密。

## 实际起点与范围

用户确认上一P3库存保存修复“没问题”，授权持续推进P4。仅将该反馈记作该修复的实机反馈，不扩大为整个P3/P6跨端资产验收。Server dev基线HEAD5a0508ac加既有未提交修改，Client P3基线53c92ce；所有Common/protobuf固定gitlink及SDK pin保持原样。

核对10/23试点及现有代码：TlsPacketChannel有真实TLS、ALPN和分帧，但正式NetServer仍为LiteNetLib UDP，Server未引用ServerTransport。首个缺口是安全listener准入及资源所有权，不能为新身份秘密复用明文UDP。关联SV-40、TQ-40、FR-10的安全传输/生命周期子集；SV-41～45未完成。

## 实施

新增`Server/ServerTransport/TlsPacketListener.cs`，纯.NET10、中立回调，无Host/Trade/Chat/旧协议依赖或额外包。现有TcpListener支持可取消accept（[官方API](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.tcplistener.accepttcpclientasync?view=net-10.0)），监听器在TLS前做连接/握手准入，握手仍复用已验证TlsPacketChannel，不自造密码协议。

- 默认128连接、8并发握手，计入原始握手及认证后连接；超限socket直接关闭，不先创建TLS任务，不维护无界等待队列。内核accept backlog上限128。连接、握手、包大小及deadline配置在绑定之前校验。
- 握手失败与应用处理失败分开报告枚举，不向诊断回调传递原异常、载荷、私钥或玩家信息；诊断回调抛错不影响名额回收。只在完成TLS/ALPN后调用中立应用处理，不创建身份、不做业务ACK或自动重发。
- 每个连接socket/channel/worker有明确owner；正常EOF、握手超时、应用异常、停机都释放。caller保持证书存活直到Completion，不将证书密钥写入配置/制品。后续证书切换需在旧listener彻底终止之后释放旧证书。
- Dispose先停止准入，异步触发取消并主动关闭socket，等待accept/worker/取消回调终止。默认10秒deadline，超时明确失败，保留Completion及worker引用；可在处理任务最终退出后再次等待，不伪称已停止。取消回调异常在清理后仍可观察。AcceptCompletion供后续Host监控意外监听错误，不静默自动重启或切协议。
- 架构scope纳入整个ServerTransport目录，按host规则禁止业务插件类型依赖；新文件需显式分类，不更新8项旧债务基线。

保持关闭边界：现代Host接入未完成、正式Server仍原UDP；未将当前秘密或业务报文改发TLS，未改客户端UI/身份/协议。P4后续必须覆盖所有认证、控制、聊天、资产及第三方插件流；不能只保护登录。现有4MiB帧上限及空闲deadline需与真实完整Envelope、准入预算及keepalive共同确定，不能把超限整批资产静默裁剪。

## 实际验证

真实TCP/TLS回环、内存合成CA/证书、独立字面帧预期；不写密钥，不访问玩家数据。最终49项检查/16组主场景，包括原29项通道回归，以及listener握手/连接上限、握手失败/超时回收、应用异常后新连接、正常双向opaque包、停机时握手和应用同时存在、幂等关闭与端口重绑定、不合作应用超时保留引用、取消回调抛错仍清理、无效配置。

Release构建0warning/0error。原沙箱socket13失败保留；仅本机回环的同一测试获准执行通过。质量supervisor7项通过；架构检查通过（8项既有债务/5项负对照未变），且包含新增transport目录。没有重跑整个Server stage/交易网络测试：正式应用引用/协议/账本未改。

主要命令：

```
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks MSBuildEnableWorkloadResolver=false dotnet build Tests/ServerSecureTransportTests --no-restore -c Release -m:1 -nr:false -p:BuildInParallel=false -p:NuGetAudit=false
dotnet Tests/ServerSecureTransportTests/bin/Release/net10.0/ServerSecureTransportTests.dll
python3 -m unittest discover -s tools/quality -p test_check.py -v
MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.113/Sdks dotnet Tests/ServerArchitectureTests/bin/Release/net10.0/ServerArchitectureTests.dll
```

平台preflight：本机Mono6.12元数据probe发现SslClientAuthenticationOptions及ALPN属性存在，但CertificateChainPolicy不存在。这仅是本机API检查，不是RimWorld1.6实际Mono握手验证；当前net10组件不能直接拷入1.6。禁止据此取消链/主机名/pin校验，后续需独立兼容adapter或明确平台支持方案。

SDK10.0.113/runtime10.0.13；profile=`phinix-stream/1`；合成固定opaque载荷[17,0,255]与独立字面帧；证书由CSPRNG临时生成，仅公钥pin在内存使用。不依赖随机调度种子；barrier为异步TCS/有界状态等待。仅受控超时/异常/断开，不冒充强杀或真实系统故障。

## 剩余依赖

1. 将listener接入普通Host transport生命周期/发送结果/串行准入pipeline，所有现代报文都使用同一安全连接；Legacy保持adapter边界，不能降级重发。当前没有正式安全监听配置或部署开放端口。
2. Client P3业务分支的.NET10真实编码/解码及信任配置、pin分发/轮换；1.6需独立运行时验证，不把自动身份变为手工密码/私钥操作。
3. 完成可信通道后，SV-41～45持久凭据/稳定UUID分离、撤销/轮换、自动旧身份映射及事务幂等迁移；目前不动玩家身份。
4. P3临时操作debug日志仍为发布前待办；持久intent/result/receipt/Unknown证据不属于可删调试日志。
