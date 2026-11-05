# P0 统一业务边界草案与客户端门槛

日期：2026-10-15。SV-03 草案、SV-04 检查清单、SV-05 待测预算输入。下文类型是待实施契约，**不是现有 API**。本轮没有新增 wire 字段、SQLite 或现代身份服务。

## 统一业务契约（SV-03）

Adapter → 领域命令：稳定 principalId、operationId、tradeId、expectedVersion、完整 opaque items 和授权事实。这里没有 Legacy/V2、connectionId、旧 token/包或凭据字段。会话验证、epoch、协议可表达性与引用解析在接入/adapter 完成；领域重新检查参与者、权限、业务版本和整批合法性。非资产 Chat 使用自己的统一意图，不借用 Trade 生命周期。

所有写操作以 `(principalId, operationId)` 唯一关联。请求指纹覆盖命令种类、目标、业务版本及有序完整 payload/metadata；规范编码和 hash 版本必须在 P2/P3 固定并测试，不能使用未规范 JSON 文本/对象 GetHashCode。相同 operationId/指纹返回原已提交结果；不同内容拒绝且原结果保留。缓存 packetId 不是 operationId，本轮拒绝重复引用也不是持久幂等。

统一结果区分未接纳、确定拒绝且未改变、已提交结果、提交状态未知。`stateChanged=false` 只能由事务/前置检查证明。领域业务状态、操作结果和 outbox 必须同一真实事务提交；只有 COMMIT 成功才返回权威成功。COMMIT 后发送失败只改变投递状态，不能改成失败退款或改 ID 重发。未确认 COMMIT 的异常不能凭 catch 推断回滚。

deliveryId 在创建 outbox 时生成并持久化，绑定 recipient principal、业务结果及 payload hash；ACK 验证接收主体和 deliveryId，以持久 receipt 区分已确认/仍待交付/从未存在。禁止把未知 ACK 回成 accepted。客户端先持久去重和实际交付，再 ACK；服务器已发送不等于到账。取消的原资产返还也走同一交付边界。

宿主只提供中立会话、授权、事务/存储及 Ready 事件；插件自行订阅恢复。Adapter 的旧关联映射可持久化，但不拥有另一份业务账本。无损表达或操作结果权威性不成立时，进入领域前明确拒绝。旧端缺持久交付 ACK 的普通交易采用 2026-10-16 确认的 [尽力交付契约](Server-Trade-Persistence-and-Legacy-Delivery.md)：离线保留待交付，已可能发出保留未知，不制造持久 receipt；它不降低领域操作提交标准。拟议现代类型由 P2/P3 分切片建立，不将当前 Framework 兼容字段搬进新的领域类型。

本轮现状：现有 Trade service 同时承担 Framework 输入边界；新增完整引用解析在该边界，store 接收完整 item 集合。它仍不是最终分层，仍保留历史 inline Items 解析。P2/P5 须迁移至明确 Adapter；本轮不添加新的 Legacy 特判。现有错误枚举没有 InvalidBatch，暂用已有 `InternalServerError` + 关联失败消息，P2 再联合更新通用拒绝契约。

## 客户端持久交付门槛（SV-04）

以当前客户端 `23643e194c254026b3665ced1e618123d68f7566` 加在途修改为检查候选；不是已发布版本。已读 R44 Semantics/Correlation/Save-Boundary 文档并核对本轮服务端线缆契约 hash。既有会话内 operation owner、token 关联、确认后 Reset 和游戏 scope 保护，不证明下表通过。

| 门槛 | 必需证据 | 本轮状态 |
|---|---|---|
| physical escrow 重启保留 | Thing/ThingComp 完整 payload、来源存档、operation、冻结 Reset 和 journal 同步 | 未验；R44 文档仍记 physical/Reset 内存资源 |
| 已提交回执丢失 | 同 operation 查询原结果；关窗/超时/断线不返还 | 未验；服务端尚无持久 operation query |
| 实际物化与 ACK | 重复 delivery、物化前后崩溃、Scribe 保存后重启、ACK 丢失 | 未验；17 个组件场景不能替代 |
| 跨存档安全 | 旧 scope 不向新存档交付/退款；重启原 scope 可核对 | 只有历史 scope 测试说明，无本轮游戏验收 |
| 无密码迁移 | UUID/交易关联不变，秘密先原子保存、迁移确认后启用、工坊更新不覆盖 | 未实施，依赖 P4/P6 |
| 原版/未升级/现代组合 | 各自真实 wire/编码/传输、明确能力拒绝、真实客户端资产交付 | 原版模块 UDP 报价/重登、历史 Rework 独立进程基础登录/凭据恢复通过；完整 Framework/Trade/混合与游戏资产待验。旧源码在临时 archive 构建，未初始化或移动原仓库 gitlink |

## 工作集与预算输入（SV-05，尚未验收）

本机：Linux x64 / cachyos，Ryzen 7 9800X3D（8 核 16 线程），约 32 GB RAM，源码位于 btrfs `/dev/nvme0n1p2`，隔离构建目录在 tmpfs `/tmp`。SDK 10.0.112 / runtime 10.0.12。临时构建耗时不作为生产 NVMe durability 测量。

下一步固定工作集应含 1/3 现代连接 smoke、三 profile 支持/拒绝、30 原版 + 70 现代混合候选、大 opaque payload 和 outbox 积压，并记录精确字节、活跃交易、随机 seed、调度和环境。该连接数是计划输入，不是容量承诺。指标包括 p50/p95/p99、commit/poll delay、heap/LOH/GC、队列字节/拒绝、恢复验证/Ready/补投时间。

目前仅采用每构建/测试子进程 180 秒 deadline 和 CI job 20 分钟作为**测试运行预算**。没有生产延迟、内存、队列、吞吐或 RTO 的合格测量，不能将它们填写为通过。资产 durability 的目标仍为经验证事务配置下“成功回执对应已提交状态”，实际 SQLite/系统故障与游戏交付证据分别在 P3/P6 收口。SV-05 保持未完成，不以配置 MaxConnections=1000 替代容量实测。
