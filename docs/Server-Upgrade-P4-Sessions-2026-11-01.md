# P4 / SV-42：安全连接绑定与短时会话（2026-11-01）

> **当前决策（2026-11-01）：TCP＋TLS 已实现，但属于实验性功能，不建议使用。** 默认交付继续采用 UDP / ClientKey。取消本轮现代身份切换及旧登录退役，后续部署、客户端实机验收、证书生命周期和迁移切换工程列为 TODO。已有实验代码和自动化证据保留，不代表正式支持；本文后续启用/迁移步骤仅作实验与历史设计参考，不是部署要求。不得自动启用或在 TLS 认证失败后降级。详见服务端 `docs/Server-Modern-Identity-TODO.md`。


范围：SV-42 会话组件、TQ-40 的 L1/L2 安全传输与身份联动子集、FR-10 过期清理。SV-41 的真实文件 SQLite 身份库为前置。P4 未完成，新身份认证 wire handler、原 UUID 迁移、客户端按服务器保存秘密仍待后续切片。

## 变更与不变量

- `Connections.Server/AuthenticatedTransportConnection.cs` 由传输程序集内部构造。`TlsNetServer` 只在真实 TLS 握手成功、连接登记后提供此能力；普通 UDP 不提供。此能力证明连接经过安全传输，客户端身份仍需独立验证，不能凭连接 ID 前缀或自报布尔值认证。
- `Authentication.Server/PasswordlessSessionAuthority.cs` 使用真实 `PasswordlessIdentityStore.Authenticate`，输出原 principal，不接收旧凭据字段或协议兼容开关，不加入业务分支。
- 随机 256-bit 会话秘密只交还调用者，内部索引只保存其 SHA256。会话与具体连接及其存活租约绑定；跨连接、未知或已替换秘密不能授权。`ToString` 不输出秘密。
- 默认有效期 5 分钟，原凭据认证最长年龄 30 分钟。续期原子替换旧会话，不能越过最长年龄；授权以单调时间计时，不受 UTC 回退延长。UTC 到期时间只供显示。
- 每次授权重查持久 principal epoch；轮换、撤销、身份库读取异常均拒绝并撤销会话。数据库修复不能复活原会话。断线和停止释放会话；Poll 至多每秒扫描一次有界集合，清理闲置过期会话与尝试记录。
- 默认最多 128 个会话，每连接每分钟 10 次认证尝试，尝试集合最多容量两倍。此预算不是首次注册预算，后续注册入口仍须另行接入限制。
- 失效通知不携带会话秘密；观察者异常逐个隔离，仅记录异常类型，不输出异常消息或参数。`Replaced` 表示旧会话被替换，后续宿主消费者不得据此误删同连接的新会话。

没有新增 NuGet 包、公共协议、生产 DI 注册或玩家数据迁移。工坊取消修复未合并，客户端和 Common 本轮未改；没有云部署、容器重启或工坊发布。

## 自动化验证设计

扩展既有 `ServerTlsHostTests`，使用两个真实 TLS loopback 连接、临时真实 SQLite、测试 CA、独立单调时钟。TLS 证书仅合成测试关闭在线撤销查询，正式传输默认策略未改。固定时钟从 2026-11-01T00:00:00Z 开始，推进 4/4/4 秒验证续期最大年龄；UTC 回退一天仍在单调时刻 12 秒过期。没有随机调度 seed；身份及秘密由 CSPRNG 随机生成，不输出秘密。

覆盖 UDP/伪 TLS ID 拒绝、真实身份保持、跨连接拒绝、容量、旧秘密失效、续期上限、主动过期清理、轮换/撤销、失败尝试预算及恢复、断线释放、重复停止、故障观察者隔离。稳定受控故障 `synthetic.identity.session.authority-corrupt`：只把合成 principal 行暂时替换为缺字段 JSON，确认授权和认证均拒绝，恢复原始行后仍须重新认证。这是受控数据损坏测试，不是进程强杀或真实磁盘故障。

该组件测试直接调用会话 API；网络只用于生成真实 TLS 连接能力。既有测试继续验证原认证包经过真实宿主，不能将两者拼接宣称新的身份协议端到端已经通过。真实 RimWorld、Windows/Waylith、生产证书及跨机迁移均未验证。

## 执行证据

证据目录：工作区 `Deliverables/Phinix-Rework-P4-Sessions-2026-11-01/`。以其中最终 `evidence/stage/manifest.json` 为准，包含执行命令、源码指纹、HEAD 加未提交修改、子模块 gitlink、结果与日志。当前 SDK 10.0.114 / runtime 10.0.14；未修改仓库 SDK pin。

首次普通环境构建遇到 CLI home 只读，改用隔离的 `DOTNET_CLI_HOME`；首次 loopback 执行被环境禁止创建 socket（SocketException 13），随后在允许本机测试网络的环境执行。两者均是环境失败，不算通过的自动化检查。

验证结果及下一依赖在本页末尾和 RoadMap 补记。

## 本轮结果与下一依赖

隔离 stage **43 个步骤全部通过，61 个发布文件审计通过**；TLS 宿主 **43 项断言**（本轮会话 **23 项**），身份持久层 **69 项**通过。架构约束与 5 个负对照通过，既有 8 处 P5 待退役项没有增加。P3 独立进程持久回归仍通过，但不作为新身份协议进程级证据。无覆盖率或真实游戏资产声明。

实际主命令：

```sh
DOTNET_CLI_HOME=/tmp/phinix-dotnet-session MSBuildEnableWorkloadResolver=false python3 tools/quality/check.py --suite stage --isolated --output /home/hunyuan2333/Phinix/Deliverables/Phinix-Rework-P4-Sessions-2026-11-01/evidence/stage --timeout 120
```

详细子命令、耗时、返回值、当前未提交源码 SHA256 及固定依赖均在证据 manifest；`source-hashes.json` 与隔离构建输入逐项核对一致，变更源码另存于 `evidence/source/`。普通环境 `git diff --check`（允许既有 CRLF）通过。

下一切片接 SV-42 新安全认证入口与 SV-45 客户端按可信服务器身份先持久化秘密，再实现 SV-43/44 原 UUID 的授权绑定与迁移重放/重启测试。当前组件不接管既有线上登录，不据此把 SV-42 或 P4 整体标完成；工坊热修复仍隔离。
