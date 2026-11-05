# Original 登录/聊天派生 fixture

`normalized.json` 来自固定原版源码构建的真实 ClientAuthenticator、ClientUserManager、ClientChat → 原版 Server 模块的临时 UDP 会话；没有使用现代 Server Adapter。`provenance.json` 保存来源 SHA、原版固定 protobuf SHA、proto/module 源码 hash、程序集/依赖 hash、producer hash、SDK/Mono 与实际命令。

三项分别为 LoginPacket、ClientChat.Send 产生的 ChatMessagePacket，以及原版服务实际返回并由真实 ClientChat 消费的 ChatMessageResponsePacket。原始 bytes 只在内存计算 SHA256，不保存秘密；派生包去除 SessionId，规范化合成 UUID/message IDs，去除非确定时间。每项记录转换、raw hash、派生 bytes 和派生 hash。它们是**脱敏派生 capture**，不能标为原始 raw golden，也不能直接当作合法认证包重放。

独立 Python oracle 的字段依据是 provenance 中的原版 .proto，业务预期来自显式合成登录名/消息和实际相关 Success，不调用被测 Adapter。重复运行原版 producer 后三项派生 bytes hash 一致，原始会话 hash 不同。

没有原版 Trade capture、未升级 Rework/现代 profile、可靠资产完成 ACK 或真实 RimWorld 交付证据。原版 CompleteTradePacket 的源码没有现代 delivery/ACK；不能给 fixture 补造能力。原始身份/秘密文件和二进制构建输出不入库。
