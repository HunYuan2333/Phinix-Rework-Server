# 服务端协议边界与无密码身份升级评估

> **当前决策（2026-11-01）：TCP＋TLS 已实现，但属于实验性功能，不建议使用。** 默认交付继续采用 UDP / ClientKey。取消本轮现代身份切换及旧登录退役，后续部署、客户端实机验收、证书生命周期和迁移切换工程列为 TODO。已有实验代码和自动化证据保留，不代表正式支持；本文后续启用/迁移步骤仅作实验与历史设计参考，不是部署要求。不得自动启用或在 TLS 认证失败后降级。详见服务端 `docs/Server-Modern-Identity-TODO.md`。


日期：2026-10-14。状态：架构约束与待实施方案；不是已经实现的安全协议或迁移承诺。

本文件针对 Phinix Rework 专用服务端，按传统 server 的会话、协议、鉴权、事务与运维边界评估。客户端仅用于核对真实 wire 契约和回执标准。

## 1. 已确认的方向

- 保留用户无密码、自动连接、自动身份的使用方式。正常连接、升级与重连不要求用户输入密码、注册账户、填写验证码或操作私钥。
- 所有旧协议兼容性妥协局限于 **Legacy adapter**。它是翻译官，不是第二套业务系统。
- 新业务只有统一语义：整批校验、权威结果、持久提交、请求关联、幂等交付。不能因为调用方是旧客户端而降低标准。
- 会话接入层明确知道所选协议与能力；业务层只接收统一意图、稳定身份、授权上下文和统一结果。
- 旧协议无法无损表达或满足要求时，adapter 明确拒绝。不能伪造 ACK、部分成功、静默丢弃载荷、自动返还或换协议重试。
- 服务端已经是 C# / .NET 10，优先沿现有程序逐步重构，不做语言层面的重写。

## 2. 当前事实与验证限制

2026-10-14 检查服务端 HEAD `5a0508a`、固定 Common `67f243d9edced77dc3fe669f8a435b1f14b59199`；客户端 HEAD `13412fe`，其 Common 固定 `be978cfbb3f9299c24fa3fa068ccc109262efc22` 且存在本地修改。未更新固定 gitlink 或改变工作区原有内容。

| 表面 | 已核对事实 | 结论 |
|---|---|---|
| 构建 | SDK 10.0.112 下 Release 串行、使用现有还原缓存编译通过，0 errors；官方 Chat/Trade DLL 输出存在 | 当前可编译，不等于干净 restore、Docker 或联机通过 |
| 测试 | 显式 build 后执行 Phase35ServerRuntimeTests：8 cases / 13 assertions | 覆盖管线，不覆盖资产崩溃恢复、鉴权安全和老客户端 |
| 契约 | 当前 Chat/Trade contracts、FrameworkPacket、FrameworkSerialization 文件一致 | 没有整体 wire 断裂证据，Common/API 仍存在漂移 |
| 物品引用 | 缺引用时 ResolveOfferItems 继续，TryGetCachedItemPacket 立即消费；缓存仅用全局 packetId | 已用真实 DLL 合成探针复现部分报价被真实 store 接受；须先修复 |
| 持久化 | TradeStore 内存状态 + XML 全量 Save；默认保存间隔 60 秒，响应前没有事务 COMMIT | 需补齐权威结果持久提交，不能只换文件格式 |
| 旧端接入 | 现入口没有装配旧 Chat/Trading 处理器；LegacyServerTradeRuntimeAdapter 只包存取且未纳入主项目编译清单 | 当前不能承诺老客户端业务可用 |
| 协商 | 已有登录后 hello/capabilities；包 Version 未在服务端校验；Session 无所选协议；能力拒绝主要写日志 | 需真正会话契约和结构化拒绝 |
| 恢复 | Trade 在 OnLogin 补投，而出站要求已完成 Framework 协商；hello 后未再次触发补投 | 应在通用 SessionReady 后由插件恢复；实际网络顺序待测 |

代码依据：`Server/Server.cs`、`Server/Framework/PhinixFrameworkServer.cs`、`Server/Authentication/ServerAuthenticator.cs`、`Server/Authentication/Session.cs`、`Extensions/Trade/Server/PhinixFrameworkTradeServerService.cs`、`PhinixFrameworkTradeStore.cs`、`BuiltInTradeServerExtension.cs`，以及客户端 `Client/Common/Authentication.Client/ClientAuthenticator.cs` 和 `Client/Source/Framework/PhinixFrameworkClient.cs`。

同级客户端文档已核对：Compatibility-Boundaries、Plugin-Development、Trade R44 semantics/correlation/save gates、1.7 迁移路线。客户端的跨存档物品托管恢复仍有未完成边界；不能把 UI 或白盒测试完成写成端到端资产恢复已完成。

## 3. Adapter 作为翻译官的硬边界

```text
Transport → Session / Identity / Authorization / Protocol gate
          → Framework adapter ─┐
          → Legacy adapter ────┴→ 统一业务意图 → 插件领域服务 → 单一权威存储
                                  统一领域结果 → 所选 adapter → 对端可理解的回执
```

图中的新类型/事件名称只是设计名称，尚未声明为现有 API。Legacy adapter 可以是独立组件，不要求先固定未实现的目录名。

| 层 | 允许持有 | 禁止承担 |
|---|---|---|
| 通用会话接入 | connection/session epoch、认证 principal、协议 family/version、协商能力、Ready、通用安全策略 | Trade/Chat 专属业务分支、假业务回执 |
| Legacy adapter | 旧包和模块、旧字段方向/缺省值、旧 token 映射、旧载荷转换、旧能力限制、旧登录格式映射 | 第二套账本、改成功标准、直接提交/返还资产、将旧身份凭据语义带进新身份 API |
| Framework adapter | 新包与统一意图/结果的转换、现代 feature gate | 承接旧字段缺省或旧 ACK 特例 |
| 业务服务 | 统一 operationId、principalId、业务输入、授权事实、事务结果、交付状态 | Legacy/V2、客户端 build、wire token、Username 中的旧秘密、根据旧端改变提交或到账语义 |
| 持久化 | 稳定主体、操作结果、业务状态、交付 outbox/receipt | 按旧端保存另一份业务权威 |

具体规则：

1. 业务服务不接收 `isLegacy`、`ProtocolMode` 或 Legacy 特例参数，不在领域模型里加入旧 packet/token/credential 字段。
2. adapter 在调用业务前确认整批输入可无损翻译、功能可满足契约、结果可正确关联并表达。不能执行成功后才发现旧端无法接收。
3. adapter 可持久保存必要的 token/operation 映射和迁移绑定，这些属于边界元数据，不形成第二份交易状态。
4. 两种协议调用同一领域服务、同一授权规则、同一事务边界。跨新旧双方的交付可行性在 adapter/通用接入处验证。
5. 通用安全要求对所有入口一致执行。Legacy adapter 不能绕开验证与授权；也不能为了让旧端通过而削弱现代认证器。
6. 旧端缺少可靠交付 ACK 时，不能用“已发送”制造“已到账”。2026-10-16 用户确认普通旧端交易仍尽力交付：离线保留 Pending，已可能发送而无证据则持久保留 OutcomeUnknown，不自动重发/退款；现代可靠交付能力仍要求持久 receipt，无法无损表达的资产操作仍拒绝。具体状态与验收见 [交易持久化与旧端交付](Server-Trade-Persistence-and-Legacy-Delivery.md)。
7. 允许的显示投影只改变表现，不改变意图或业务结果。回复/提及等有语义的意图不能擅自删字段当成普通消息成功发送。
8. 不支持的 opaque item codec 整批拒绝；服务器原样保存不代表真实旧客户端能解码。
9. 日志与 UI 不能补造业务状态。断线、超时、关窗均不自动确认/返还结果未知的资产。

现有客户端历史桥梁可暂时保留以保护 ABI，但调用方向只能归一化到新语义。服务端新增领域 API 不继承这些兼容接口。

## 4. 会话协议和明确拒绝

协议版本、插件/API 版本、客户端运行时/.NET 版本是不同事实。新业务不读取协议级别。

会话建议状态：Connected → Authenticating → Authenticated → Negotiating → Ready；失败进入 Rejected/Closed。Legacy profile 的确定只在兼容入口发生。现有客户端 3 秒超时进入 Legacy 的规则需联合升级，不把非法现代握手降级成旧端成功连接。

现代 hello 以可解析的 bootstrap envelope 提供版本范围和 feature/schema 范围；服务端选择支持版本，取客户端支持、服务器实际激活支持、策略允许的交集。结果固定在 session epoch 内；重复 hello 不任意覆盖身份/协议。协商能力不授予管理权限。

所有业务在 Ready 后才进入领域。通用 SessionReady 事件供任意插件订阅恢复，宿主不硬编码 Trade 补投。

控制协议应有原请求关联的明确拒绝：UNSUPPORTED_PROTOCOL、FEATURE_NOT_NEGOTIATED、NOT_AUTHENTICATED、NOT_AUTHORIZED、INVALID_PAYLOAD、MISSING_ITEM_REFERENCE、RATE_LIMITED。返回 operationId、可重试性、最低 feature 要求，以及有证据时的状态未改变标记。控制错误不能被缺失业务能力的出站过滤再次吞掉。

Legacy adapter 将统一拒绝翻译成旧端可理解的失败回执；没有合适表达方式时拒绝该操作/会话，而不是返回空成功。现代端的拒绝不能触发自动 Legacy fallback。

## 5. “弱登录”应保留什么

现有客户端以 CSPRNG 生成 64 字节（512 位）随机 ClientKey，写入本地凭据文件，连接时将其放在 AuthenticatePacket.Username，Password 为空。服务器按这个字段找身份，首次出现自动建立记录。ClientKey 就是 bearer 登录秘密，不是显示名。

因此 ClientKey 模式不检查 Password **符合现有契约**，不能称为普通账户密码绕过。全量 UserSync 会剥离 Username；补充静态审计发现增量 broadcastUserUpdate 直接打包原 User，存在秘密外发路径（FR-14），因此不能把全量同步的保护推广到所有用户包。此项需先修复，不等凭据升级。

需要保持的是低交互产品体验，而不是继续暴露秘密：

- 玩家选择服务器、设置显示名；客户端完成身份生成、保存、注册、续期、轮换。
- 显示名不是唯一账户标识，不用 Steam ID、设备指纹或 IP 冒充已验证身份。
- 对同一份客户端凭据提供稳定玩家身份；不承诺真实姓名、真人唯一性或跨设备账户找回。
- 普通玩家凭据只具备普通权限，管理权限不能由显示名或自报 capability 获得。

## 6. 风险与升级价值

| 风险 | 当前依据 | 升级重点 |
|---|---|---|
| 凭据泄露 | ServerAuthenticator 日志输出 Username，ClientKey 模式下该值就是秘密；本地持久文件保存秘密 | 脱敏、日志审查、原子写入及权限、服务端仅保存 verifier |
| 传输身份被窃取/篡改 | 所查 UDP 接入没有加密配置；连接标识 Phinix 不构成安全认证 | 认证服务端身份的成熟安全传输；不能仅加入 challenge 就宣称安全 |
| 身份与秘密耦合 | 旧 key 兼作 credential map key/user lookup 输入 | 稳定 principalId + 独立可撤销凭据 |
| 跨服务器关联 | 客户端安装级 ClientKey 在各服务器复用 | 新凭据按可信服务器身份隔离，不只按 IP/端口隔离 |
| 无限身份注册 | 首见 key 自动注册 | 注册预算、每连接/IP 请求预算、总量限制、背压；限 IP 不等于识别真人 |
| 资产操作保护不足 | 登录不等于拥有某交易或管理权限 | 校验会话主体、参与者、操作权限、请求关联与幂等 |
| 凭据丢失 | 删除/损坏文件等同失去原身份证明 | 保持原文件，不自动覆盖损坏凭据；提供可选备份/恢复工具 |

匿名自动身份允许用户重新生成身份。封禁一个 principal 不能保证封禁同一个真人；IP 封禁会误伤共享网络。无可信外部身份或人工管理，就无法同时获得无交互、强真人唯一性和强封禁防绕过。当前不引入实名、密码或强制第三方登录。

服务器本身可修改其领域状态；身份升级不把服务器变成可验证可信托管，也不阻止客户端伪造自己拥有的游戏物品。资产验证与本地游戏可信度是另一项边界。

## 7. 推荐：自动生成、按服务器隔离的匿名凭据

**默认先选择自动随机 token + 认证加密通道，不强制密码，也不先引入复杂账号系统。** 高熵随机 token 已适合低交互身份；增加公钥只能改变凭据证明方式，不能解决匿名用户重建身份、丢失找回或安全传输缺失。

建议现代身份模型：

- principalId：服务端稳定非秘密 ID，维持既有玩家 UUID 与业务记录关联。
- credentialId：非秘密随机索引，与可撤销凭据分开。
- client secret：客户端自动生成高熵随机 token，按可信服务器身份保存；用户不输入。
- verifier：服务端保存 token 的摘要/verifier，不保存明文 token；必须选择明确版本和成熟密码库。高熵随机 token 不是人类低熵密码，不要求套用人工密码登录流程。
- session：验证凭据后签发短时会话，绑定连接与 epoch；续期/撤销由会话设施管理。

首次连接在有认证和加密保证的通道上自动注册，建立稳定 principal 并原子提交后响应。后续自动证明凭据持有，普通连接不弹密码窗口。token 不写日志，不在用户同步或错误详情中回传，服务器不要求客户端上传安装级总秘密。

**安全通道是实施前置，不是文档中已经解决的条件。** 当前 UDP/LiteNetLib 不能直接套一个 SslStream。TLS 的服务器证书校验及加密可用于流式方案，见 [.NET SslStream](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream?view=net-10.0) 和 [TLS 最佳实践](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-best-practices)。客户端随机秘密生成使用成熟 CSPRNG，见 [RandomNumberGenerator](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator?view=net-10.0)。

后续独立比较“成熟安全 UDP transport”与“现代业务整体走 TLS stream”的部署/延迟/平台成本；保留 Legacy UDP 为兼容入口不意味着新端也必须永远复用它。如果只增加 TLS 登录控制通道、后续业务仍明文 UDP，资产命令仍缺保护，不能宣称安全升级完成。证书信任由部署者正确配置或可信分发解决，不能通过证书校验永远返回 true 来实现无感连接。自建私服首次信任可能需要一次证书/服务器身份确认，但不要求玩家创建或输入账户密码。

备选的每服务器设备公钥方案：客户端自动产生私钥，服务端保存公钥，对带服务器身份、session epoch、用途与一次性 nonce 的挑战证明持有；签名与格式使用成熟库。私钥不传输且服务端公钥表泄露不等于泄露登录秘密，但带来两版客户端加密库、导入导出与挑战状态成本。仅在实际需求证明收益后采用；不用自造密钥交换/加密协议。两种方案都保持同一身份与业务 API。

## 8. 无密码迁移：不能靠旧明文证明“安全找回”

迁移目标是保留旧 principal/UUID、显示名及交易关联，改变凭据，而不是自动给所有人新建玩家。

1. 新客户端先自动生成并原子保存待注册凭据，保留旧 ClientKey 与迁移 operationId。未持久保存新秘密不能先让服务器绑定成功。
2. 在已经认证并加密的现代通道中提交旧凭据证明及新 credentialId/verifier 绑定。旧字段/身份查找规则只在 Legacy credential adapter 内解析和映射，现代身份服务收到的是稳定 principal 和绑定意图。
3. 身份存储同事务写入新凭据绑定、迁移结果及旧凭据的退役策略，保持 UUID；相同 operationId/请求重试返回同一结果，冲突绑定拒绝。
4. 服务端提交后客户端收到确认才将新凭据设为 active。响应丢失时用相同迁移请求/新凭据查询结果，不重建账号、不删旧文件；客户端与服务端都要覆盖崩溃时点。
5. 同一 principal 的旧登录可在有限迁移窗口暂存，但长期保留旧入口可登录同一身份会让新凭据保护失效。完成切换后拒绝旧凭据；受影响的历史客户端仍可以进入被管理员允许的兼容范围，但不能突破新安全边界。
6. 旧凭据已经泄露时，自动迁移不能区分原持有人与攻击者，可能出现抢先绑定。迁移窗口只能降低运营风险，不能恢复真实性。需要本地可信迁移材料、已绑定现代凭据或管理员审核才能做争议恢复；不能允许凭显示名领回身份。

“无感迁移”仅适用于旧凭据完整、服务器可信且没有已知身份争议的正常用户。秘密丢失/泄露、服务器身份异常等情况应给明确故障，不偷偷建新玩家或降低验证。

## 9. Legacy 弱登录的隔离

Legacy adapter 保持旧包可解析，并将旧 bearer 身份映射到通用 principal。旧 Username 的特殊含义、Password 为空及首次注册规则留在兼容入口；现代 token 验证不接收这些例外。

所有入口仍遵守统一权限与资源预算。管理员可配置兼容入口是否允许新身份注册、是否允许未迁移凭据；这些是接入策略，不在 TradeService 中出现 `if (legacy)`。旧通道无法达到所需的通用安全要求时，在 adapter/接入 gate 拒绝操作，而不降低业务标准。

Legacy 业务使用同一个权威 repository 与 operation result/outbox。旧协议无法满足准确确认和无损交付时明确拒绝，不能以“弱登录”作为弱资产一致性的理由。

## 10. SQLite 与实施顺序

SQLite 建议从单机资产事务与身份绑定切片开始，而不是单纯替换 XML 文件。业务变更、operation result 与 outbox 同库同事务提交；成功回执在 COMMIT 后发送。身份迁移也要同事务，临时会话无需全部永久保存。插件共享中立事务设施，宿主不依赖 Trade 专属接口。

| 顺序 | 切片 | 验收标准 |
|---|---|---|
| A | 整批引用校验、owner scope、日志脱敏、明确发送结果 | 一项缺失全批无状态改变；失败不消费合法引用；日志不含合成秘密 |
| B | 会话协议、通用拒绝、Ready/recovery | version/feature 不满足明确拒绝；拒绝不降级；Ready 后补投 |
| C | Trade SQLite operation/outbox + 身份稳定 ID/凭据模型 | 各提交/响应/ACK 时点强杀可恢复；重复 ID 幂等、不同请求冲突拒绝 |
| D | 成熟安全传输验证 + 自动凭据/迁移 | 两版客户端支持、服务器身份验证；零密码输入；迁移全崩溃点、撤销与重放拒绝 |
| E | Legacy 翻译接入 | 老包 fixture/真实老端；域服务没有协议分支；不支持功能旧失败回执 |
| F | 宿主与联合测试 | stdin EOF/SIGTERM、背压、干净 build/publish/Docker、新旧组合 |

D 的安全方案调研可以提前做；未完成安全通道前，不把现代凭据升级发布为安全能力。E 的资产路径依赖 C 的单一账本，不同时运行旧 ServerTrading 的独立 store。

新增测试必须验证边界差异：相同领域输入经现代/Legacy adapter 具有相同业务结果；缺能力在业务前拒绝；旧包字段不进入领域；无密码注册/重连/迁移；密钥变更不改变 UUID；失去秘密不能凭显示名找回；已提交发送失败不变成安全退款。

本次只更新评估与约束文档；以上升级没有实施，服务端运行行为保持现状。

后续实施阶段、依赖、任务编号与工坊切换范围见 [服务端升级 RoadMap](Server-Upgrade-Roadmap.md)。现有 Rework 登录需要客户端/共享契约/服务端联合更新，保持无密码体验与原 UUID；具体切换窗口留至发布阶段决定。

补充 [静态性能审计](Server-Static-Performance-Audit-2026-10-14.md) 与 [容错和可恢复性评估](Server-Fault-Tolerance-and-Recoverability-2026-10-14.md)。性能/恢复任务及新增增量用户包秘密风险已接入 RoadMap。


## 2026-10-23 实施状态补充

SV-40 已有独立 TLS packet channel 首轮实现及本机真实网络负例，参见 [安全传输试点](Server-Upgrade-P4-Transport-Pilot-2026-10-23.md)。仅是中立传输试点；尚未接入正式监听/全部现代包，未迁移凭据，不能据此称当前 UDP 服务安全升级完成。游戏平台与部署验证仍是下一前置。
