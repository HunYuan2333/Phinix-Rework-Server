# 原版 Trade 报价派生 fixture

固定原版 `ce82b8f2cd57718f05de86458eebd3320f35c4d8` 与 protobuf `dd06cc6c8f906b760ef224a085f45e7694dc426a`，独立构建真实 ServerTrading / ClientTrading，两个合成身份经真实 loopback UDP 自动登录并创建同一交易。

A 报 Steel×7、B 报 WoodLog×3，随后销毁 B 的网络/身份/交易模块，以同一凭据文件重建并经真实 UDP 重登，验证 UUID、tradeId 和双方报价原样恢复，再由 A 清空报价。每步保留请求、实际成功回执与另一方状态包，共 9 项。实际 ClientTrading 处理后另核对原 token、双视角完整物品和清空仅影响 A；没有用现代 Server Adapter 生成预期。

`normalized.json` 仅含去除 SessionId、规范化 UUID/TradeId 的派生 bytes，token 为合成固定场景名，物品是手工指定 ProtoThing，未经游戏物化。原始 bytes 只在内存计算 hash，非原始 raw golden、非认证有效重放包。`provenance.json` 记录源码、依赖、构建程序集、producer/runner hash 和实际命令。独立 Python oracle 使用固定 proto 字段与显式物品值，包含物品方向交换、用快照冒充结果、遗漏空报价回执三个负对照。

已验证原版同服务端内存会话下的客户端模块重建/重登同步；未证明真实游戏重启、服务端进程重启、升级服务端兼容、持久提交、可靠完成 ACK、真实 RimWorld 资产守恒或到账。旧端尽力交付策略见服务端 `docs/Server-Trade-Persistence-and-Legacy-Delivery.md`。
