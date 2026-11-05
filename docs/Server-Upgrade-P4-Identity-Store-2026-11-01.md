# P4 / SV-41：稳定身份与可撤销凭据的持久核心（2026-11-01）

> **当前决策（2026-11-01）：TCP＋TLS 已实现，但属于实验性功能，不建议使用。** 默认交付继续采用 UDP / ClientKey。取消本轮现代身份切换及旧登录退役，后续部署、客户端实机验收、证书生命周期和迁移切换工程列为 TODO。已有实验代码和自动化证据保留，不代表正式支持；本文后续启用/迁移步骤仅作实验与历史设计参考，不是部署要求。不得自动启用或在 TLS 认证失败后降级。详见服务端 `docs/Server-Modern-Identity-TODO.md`。


状态：SV-41服务端持久组件实现并自动验证，P4整体及新认证入口/玩家迁移未完成。没有将旧ClientKey-over-TLS称为新身份方案。

## 范围及边界

Server dev HEAD5a0508ac、Client业务分支HEAD53c92ce加既有未提交修改，固定Common/protobuf gitlink和SDK pin未改。新增`Server/Authentication.Server/PasswordlessIdentityStore.cs`；复用中立ServerStorage/真实文件SQLite，不新增NuGet依赖，不改业务插件或UI。

关联SV-41及SV-44幂等/撤销的持久子集、TQ-40。新组件没有被注册为网络handler或生产DI服务；BindAuthorizedPrincipal/RotateAuthorizedCredential/RevokeAuthorizedCredential是供已授权服务调用的内部服务边界，不能直接从客户端字段推断授权。AuthenticationFacts由成功验证生成，IsCurrent只检查这些已验证facts的当前epoch，不能把客户端自报facts当作凭据。

## 实现

- 稳定ServerId与principalId分离于credentialId。首次注册生成新的principal；BindAuthorizedPrincipal用于后续受信任身份映射，保留原UUID字符串（含原存储大小写），不以显示名认领、不自动重建玩家。当前单一active credential，轮换须原子撤销旧条目，不能再次Bind覆盖已有principal。
- 客户端未来提供32-byte CSPRNG随机秘密，服务端仅持久化version=1的SHA256 verifier，域分隔包含固定用途、稳定ServerId、credentialId和secret。服务器检查长度，不能凭长度证明客户端随机性；此机制不是低熵人工密码哈希，不接受Password/旧凭据字段。
- 验证使用[.NET FixedTimeEquals](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.fixedtimeequals?view=net-10.0)。使用SHA256对高熵随机token作域分隔verifier属于本工程设计，不宣称实现OAuth或独立密码协议；后续发送仍必须经过已验证TLS，遵循[Bearer安全边界](https://www.rfc-editor.org/rfc/rfc6750/)中对机密性与服务器信任的要求。
- state、稳定operationId/指纹、原始result与中立失效outbox在同一SQLite事务提交；Execute返回是唯一成功边界。相同请求重放返回原结果，不同请求同operationId明确冲突；后续撤销不改写历史已提交结果，也不会使重放重新激活凭据。
- 轮换/撤销递增principal epoch。Authenticate检查当前凭据及活跃绑定；IsCurrent同时验证ServerId、principal、credentialId与epoch。旧凭据及旧facts失效，跨store即使UUID/index相同也不能复用facts。此刻尚未接入实际session registry，因此不宣称已撤销现有旧登录连接。
- 原始secret不进入result、outbox、SQL日志或用户包。存储verifier不能直接作为bearer secret使用；没有显示名/设备/IP找回或密码/验证码/手工私钥操作。
- 新建与正常打开分离：CreateNew不覆盖已有DB；OpenExisting不重建丢失/缺header/版本不兼容数据。缺少Revoked JSON字段明确拒绝，不能默认false放行。新写入操作有64MiB数据库预算，历史operation结果的读取/重放先于容量检查；无自动过期删除或无界重试。

## 验证

真实编译组件、真实SQLite文件、独立SQL检查，无Mock业务成功。最终身份组件69项检查通过：

- 注册与重启稳定、相同操作幂等、指纹冲突拒绝、唯一credential绑定；不同ServerId的verifier不同，跨store facts拒绝，verifier本身不作为秘密通过验证。
- 原UUID绑定保留、重复绑定拒绝、跨owner轮换拒绝且state/result/outbox不变；轮换后旧秘密/epoch失效，新秘密可用；撤销及重启不复活旧凭据，历史原结果仍可查询。
- 稳定测试故障`synthetic.identity.before-result`：仅在隔离DB安装SQLite trigger，阻断result插入。权威状态写入后整体回滚，原凭据仍有效、无新result/outbox；明确重开后同operation成功提交。受控数据库异常，不是强杀或断电。
- 独立store实例并发轮换：固定operation UUID ...0010/...0011，共同启动信号；不要求某个线程获胜，仅一个结果/新凭据可提交，输家拒绝或有界busy；原结果重放一致。没有无限重试。
- DB/WAL等隔离文件不含原始秘密、Base64或hex；测试仅输出计数/状态，不输出秘密或玩家内容。损坏字段、缺失DB/不完整bootstrap拒绝。数据库行数只证明本组件事务，不作为游戏资产证明。

隔离Server stage43项、61发布文件审计通过（其中身份基础49项）；之后仅增加并发/存储verifier负例等测试到69项，产品文件SHA与该stage sourceInputs一致，未无理由重跑整个图。最新身份69项另有明确命令、源码/DLL hash及输出，不合并计为不同案例。

新组件已加入原serial quality入口及domain架构约束，8项既有债务/5负对照不变。SDK10.0.113/runtime10.0.13；构建保留既有protobuf net50/obsolete警告。无新增远程fault开关；密码学秘密用CSPRNG临时生成、不固定seed；既有FsCheck seeds/故障点随stage manifest保存。

## 上一切片证据恢复

10/31临时构建/测试目录被环境清理，上一切片交付包未及时落盘。11/01重新完整构建并立即复制至`Deliverables/Phinix-Rework-P3-P4-Client-TLS-2026-11-01/`，不是声称旧临时文件仍存在。新客户端候选SHA256 `4e8e2976d7f6e7e383ba82284bdbb093b42b6deecf0cc5e36d2a16d0655e1500`，双版95文件审计通过；最终包Trade组件各320、1.7 wire34/保存33/恢复35及真实TLS联合11项（含32秒保活）重新执行通过。每个probe完成立即保存证据，避免再次只留/tmp。

本包包含此前P3修复与16条临时debug清理，默认UDP兼容；1.7 TLS为明确配置入口，1.6 TLS仍明确不支持，不静默降级。未绑定历史交付、终态冷归档和真实游戏/存读档范围未被标完成。

## 下一依赖

1. SV-42：仅安全连接可进入新认证handler；成功facts绑定connection/session及短期epoch，每次入站/出站、续期及presence均检查失效。消费持久outbox，不把IsCurrent当作客户端可自报的权限。
2. SV-43/44：Legacy identity adapter证明旧身份，原子绑定新凭据、迁移operation及旧认证源退役；不跨XML/SQLite“各写一半”或长期保留旧登录绕过新安全性。
3. SV-45：客户端按可信服务器保存pending新秘密和operation，先原子持久再请求；丢回执使用同operation查询，不重新注册新玩家；轮换/撤销及迁移确认完整闭环。

本轮没有旧玩家迁移、生产identity DB创建、服务端部署/重启、工坊发布、push/merge；Windows/Mono及真实RimWorld资产验收仍未完成。
