# P0 原版 Trade 协议与重登恢复证据

2026-10-16；SV-02/03/04/50、TQ-02 与旧端交付范围澄清；P0 仍未退出。

## 本轮交付

- `Tests/LegacyModuleProbe/TradeProbe.cs`：固定原版真实 ServerTrading/ClientTrading，两个自动无密码合成身份，临时 loopback UDP 端口；真实创建交易、双方报价、A 空报价。验证操作 token 回执与另一方普通状态通知分别被真实客户端处理。
- B 网络/身份/交易模块销毁，以原凭据文件重建，实际重登后同步相同 UUID、tradeId 及双方报价。这是同一服务端进程下的模块重建测试，不是独立游戏进程/服务端强杀恢复。
- `Tests/Fixtures/OriginalTrade`：9 项脱敏派生 capture；保留原包 hash、转换说明、固定源码/依赖/程序集/producer/runner hash 与命令。两次独立网络执行对应派生 hash 完全一致，含重连执行与初始报价执行。
- `Tests/LegacyGoldenTests/test_original_trade.py`：原 proto 字段与手工指定合成物品的独立 oracle，三个负对照捕捉物品方向交换、快照替代操作结果、遗漏空报价成功。不是调用被测 Server Adapter 生成预期。
- `legacy_probe.py --scenario trade` 保持原 `login-chat` 模式；`check.py` 的隔离 stage 包含新 fixture 并执行两类 oracle。
- [交易持久化与旧端交付契约](Server-Trade-Persistence-and-Legacy-Delivery.md)：用户确认旧端普通交易尽力交付；离线持久 Pending，重连尝试；可能发出后结果未知则留存，不自动重发/退款/伪 ACK。分离交易终态与每位接收者交付状态，保留现代持久去重/receipt 要求。同步更新 RoadMap、P0 契约、协议边界、QA 矩阵。

没有修改本轮产品协议、客户端/共享层源码或任一 gitlink；已有修复与用户在途修改均保留。状态机和 SQLite 仍待 P3，Legacy 产品接入待 P5。

## 实际执行

服务端基 SHA `5a0508acb703319e60dd5600e3e0facd0397b294`，包含未提交工作；精确源码和配置 hash 见本轮 JSON。固定原版 `ce82b8f2cd57718f05de86458eebd3320f35c4d8`，原版 protobuf `dd06cc6c8f906b760ef224a085f45e7694dc426a`。服务端 Common `67f243d9edced77dc3fe669f8a435b1f14b59199` / protobuf `4b0c3aacf0657fbf38253b38918d3358dd4319ec` 未变。

```sh
# 原版固定来源在 /tmp 获取，公共旧包恢复到独立 package cache；不初始化原仓库子模块。
git -C /tmp/phinix-original-trade-protobuf fetch --depth=1 https://github.com/thomotron/protobuf.git dd06cc6c8f906b760ef224a085f45e7694dc426a
dotnet restore /tmp/phinix-original-trade-packages.csproj --packages /tmp/phinix-original-trade-packages --disable-parallel -m:1 -p:NuGetAudit=false

python3 tools/quality/legacy_probe.py --scenario trade --original-source /home/hunyuan2333/Phinix/Phinix --protobuf-source /tmp/phinix-original-trade-protobuf --package-cache /tmp/phinix-original-trade-packages --framework-root /home/hunyuan2333/.nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build --output /tmp/phinix-p0-trade-reconnect-20261016
# 初始报价模式：同参数，output=/tmp/phinix-p0-trade-20261016-network
# 原登录/聊天：--scenario login-chat，output=/tmp/phinix-p0-original-login-check-20261016
python3 -m unittest discover -s Tests/LegacyGoldenTests -v
python3 -m unittest discover -s tools/quality -p 'test_*.py' -v
python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p0-trade-final-20261016
git -c core.whitespace=cr-at-eol diff --check -- Server Extensions Tests tools .github .gitignore
```

初次 sandbox 网络执行 `/tmp/phinix-p0-trade-20261016` 的 4 个构建步骤通过，UDP 步骤 `SocketException: Access denied`，总结果失败，明确保留。随后获得 loopback 网络执行权限，在新输出目录执行真实网络；不是将失败忽略后重试。三次成功原版探针各 5 步全部通过（报价、旧登录聊天、加入模块重登后的报价），stdout/manifest 中分别有完成标记。原始凭据、身份文件、raw packet bytes 和二进制制品不入库。

L1 Python 检查：golden 8 项、supervisor 4 项通过。原版真实模块/serializer/网络证据属于独立来源 spike，不算升级服务端 published L3 通过。最终隔离 stage 21 步全部通过，publish 29 个制品文件已 hash；Server 8 场景/13 断言、Common 4 场景/7 断言、安全 17 场景、模型 3×256 例、框架负对照、架构 4 个负对照通过。架构仍有显式 baselineDebt=8，不等于债务清零。逐步命令/结果和精确输入见 [机器证据](Server-Upgrade-P0-Trade-Evidence-2026-10-16.json)。

所有场景使用显式合成物品和固定调度，无随机 seed（null）、无 faultId（空）；已有模型独立使用 seed `(20261015,20261017)`。没有本轮覆盖率、生产性能或系统故障测量。

## 剩余限制与下一依赖

仍缺原版成交/断线交付/服务端重启、未升级 Rework 独立 capture、raw corpus、SV-01 完整干净环境/coverage、SV-05 实测预算。P3 必须实现同事务结果/outbox、发送前尝试记账、恢复未知与真实 SQLite 故障；P5 验证旧端和混合接入；P6 验证游戏库存/存档/托管/工坊凭据保留。不能从模块重登通过推导 RimWorld 重启不丢物品，不能从包数量或数据库记录推导资产守恒。
