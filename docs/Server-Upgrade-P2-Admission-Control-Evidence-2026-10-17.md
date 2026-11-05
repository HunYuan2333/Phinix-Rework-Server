# P2 关联拒绝、客户端处理及独立进程检查（2026-10-17）

完成 SV-21/22/24、TQ-20/21 的声明子集。服务端基础 `5a0508acb703319e60dd5600e3e0facd0397b294`，客户端基础 `27878e7fb78c11fec4f390db317dc0db5b0b10c9`，均包含未提交修改，不能仅用基础 SHA 复现。命令、输入/制品 hash、工作区状态、fixture 与分层结果见 [机器证据](Server-Upgrade-P2-Admission-Control-Evidence-2026-10-17.json)。原有客户端 UI/昵称等未提交修改保留。

## 实现及不变量

新增 `control / framework.admission.rejected` 中立消息，携带 Code、原 RequestMessageId/CorrelationId、Negotiation 或 Admission 阶段。版本、Hello、Ready、能力和未知 kind 的**准入前拒绝**可返回控制消息；不依赖业务能力，但必须匹配已认证连接/session。ID/correlation 有 128 字符上限；不回显任意 payload 或其他会话的 bearer。能力响应也关联原 Hello。解析失败日志不再输出异常消息，item 拒绝日志移除 session 值。

控制消息明确固定 `OperationOutcome=Unknown, Retryable=false, RequestInvoked=false`：只说明这次准入没有调用业务，**不能证明同 operation 以前从未提交**。控制消息不经过业务结果/交付 ACK 路径，不触发退款或重发。进入 pipeline 后的异常不套用准入拒绝，避免伪称没有副作用。尚无持久 operation 查询，因此不把 correlation 宣称为已完成稳定操作账本。

真实客户端 Framework 接入独立于游戏的有界策略：匹配 session、系统 sender、Hello/请求关联；明确协商拒绝锁定本连接，重复 Begin/迟到 capabilities 不能复活，断线后才重置。看到现代响应或明确拒绝后不能由 timer 静默降级。普通请求拒绝不破坏其他独立请求。新增只读事件及中英文系统提示，不加入 Trade 特判、不操作库存。近期最多 256 个请求关联只是内存策略，不是持久去重。

依赖方向保持 ServerRuntime 中立服务及客户端现有 Framework；没有新增包、Common 修改或 gitlink 改动。JSON DTO 当前由两个端点分别定义，通过固定规范编码及真实网络检查约束；未来公共版本契约归一化仍待后续联合升级，不用改动固定子模块掩盖版本差异。

## 联合版本范围

- 服务端 Common 固定 `67f243d9edced77dc3fe669f8a435b1f14b59199`，客户端 Common 固定 `c165f61d749189f700ca896d63d9366d5c71dbf7`；两端 protobuf gitlink 均 `4b0c3aacf0657fbf38253b38918d3358dd4319ec`，全部未更新。
- 本轮新增客户端要求 capabilities 关联原 Hello；旧现代服务端若不提供关联，会保持拒绝而非自动 Legacy 重发。因此应使用本轮对应的服务端/客户端候选，不宣称任意版本互通。
- 旧端无响应时的历史 fallback 仍在客户端宿主，是 P5 已知迁移债务；本轮只封住显式现代响应后的降级。没有宣称所有旧规则已进入 Adapter。
- 当前 session/系统 sender 核对不是密码学服务器信任；P4 安全通信、撤销/轮换尚未实现。

## 实际检查

构建使用 `MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks`、`-m:1 -p:BuildInParallel=false`；未新增框架。已执行命令的完整参数及日志 hash 在机器证据，日志/制品本体留在 `/tmp` 或忽略的 build 目录，不加入源码。

| 层级 | 实际命令/范围 | 结果 |
|---|---|---|
| L1/L2 | build 客户端 `Tests/FrameworkAdmissionRuntimeTests`，run `--server-fixture <Server>/Tests/Fixtures/FrameworkControl/normalized.json` | 17 项通过；产品策略源码、客户端固定真实 serializer，未加载服务端程序集 |
| L1/L2 | `python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-control-stage-20261017` | 27 步及 33 制品通过；21 安全组件、3×256 属性 seed 20261015/20261017、既有控制台/架构/快照/生命周期保留 |
| L2+真实 socket | `dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --ready-network` | 通过；原身份/版本/重放/Ready/重连/Trade 快照/未知 pending 保留，增加关联控制拒绝 |
| L3 子集 | `tools/quality/admission_probe.py` 启动独立 published Server 与客户端 probe 进程 | 2 场景通过：错版本 Hello；正常 Hello 后缺业务能力请求。真实 UDP/认证/登录/编解码；客户端显式 Unknown、不降级/重发 |
| 发布宿主 | `host_smoke.py`、`host_load_smoke.py`，使用 `/tmp/phinix-control-publish-20261017/PhinixServer.dll` | 停机 3 项、损坏加载 6 项通过 |
| 客户端编译 | `dotnet msbuild Client/Source/Client.csproj -restore -t:Build -p:Configuration=Release -p:SolutionDir=<Client>/ -p:RimWorldDepDir=<Client>/GameDlls ...`，本轮最终再 Build | net472 完整客户端编译通过；使用已有私有编译依赖，没有伪造或分发游戏 DLL |

fixture 是 `FrameworkControl/normalized.json` 的合成规范包，producer 源码和 bytes SHA256 记录在 provenance；并非原版抓包。客户端的字段和拒绝语义预期独立写出，不由被测 Adapter 生成。没有对本切片声称执行过修复前失败运行；新增预期与实际组件/网络检查提供当前行为证据。

控制通道新检查无随机故障 seed/barrier，无生产注入开关；原 Ready 检查仍使用测试内部 `auth.expire-before-outbound`、`auth.identity-switch-before-relogin`、`session.ready-subscriber.throw`，不能称作系统故障。属性 seed 仅属于既有模型。独立进程脚本只用合成身份/隔离目录，限制 startup/client/shutdown 时限并清理自有进程。

## 剩余与下一依赖

本轮 L3 只覆盖控制层，完整 Unity Framework endpoint、UI 提示、三 profile/schema/聊天/混合客户端及游戏物化/库存/存档均未验；完整客户端编译不等于插件组合或真实联机通过。尚未提供完整游戏联测包，因此目前不要求用户操作游戏；到候选包及具体隔离用例准备好时再通知。

SQLite、业务状态/指纹/操作结果/outbox 同事务、COMMIT 后权威成功和持久 receipt 仍未实现。报价快照恢复不能解决周期保存间隙及已提交交付未知。旧 Phinix 继续按已确认方案设计：明确未发送则保留 Pending、重连尽力交付；可能已发送无可靠 ACK 则保留 Unknown，不盲补投或自动退款。本轮没有提前接上含义不明的旧 pending。

P2 控制切片完成，可依 §0/14 加速范围开始 P3 的 SV-30/31 中立 SQLite 与交易操作事务切片；SV-21 版本/schema 扩展、SV-24 fallback 迁移、SV-25 全面清理、SV-27 多连接策略、缓存 TTL/断线回收/operation scope 继续显式跟踪，不称 P2 全项通过。真实游戏交付验收仍留待客户端交付能力与候选准备完成。
