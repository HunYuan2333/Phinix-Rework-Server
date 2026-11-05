# 未升级 Rework 登录 wire 派生 fixture

来源为固定历史客户端 `23643e194c254026b3665ced1e618123d68f7566`，固定 Common `9d0710aaa9b41c07f28d9bfa25599247e7bb1f93` / protobuf `4b0c3aacf0657fbf38253b38918d3358dd4319ec`。客户端 checkout 已推进到其他版本，本探针只 archive 这些 SHA 与父 gitlink，不取工作区的新源码或在途修改。

真实 Connections.Client、Authentication.Client、UserManagement.Client 的原 net472 工程独立构建，不重写它们。两个独立 Mono 客户端进程分别经实际 UDP 连接同一 .NET 10 测试组件宿主，自动认证、登录、接收真实用户同步；第二个进程复用私有临时凭据文件，验证同 UUID。宿主组合当前真实 NetServer、ServerAuthenticator、ServerUserManager，不是模拟认证成功，也不是 published Server。

捕获历史 `ClientUserManager.SendLogin` 实际发出的两份 LoginPacket；在接收组件处理前采样。只删除 SessionId，保留其他字段；raw bytes 仅内存计算 hash，不保留认证包/身份文件。固定字段 oracle 依据历史 proto 与 SendLogin 调用：字段 3 为合成名字，历史 API 未设置字段 4 AcceptingTrades，不能补造新版缺省。两次网络执行、每次两个独立客户端进程的派生 hash 均相同。

真实入站 UserSync/UserUpdate 在历史解码器中按 UUID/DisplayName/LoggedIn/AcceptingTrades 重建比较，确认不存在 Username 或未知字段，未将用户包 bytes 留在 fixture。这里只证明该场景实际收到的公开用户包，不宣称完整 delta/混合广播矩阵均通过。

provenance 保存源码/程序集/producer/runner hash、实际命令与网络结果。Python oracle 不依赖被测 Server Adapter；包含补造 AcceptingTrades 字段、替换历史客户端 SHA 两个负对照。

**范围限制**：没有完整 PhinixFrameworkClient（固定源码直接引用 Verse），没有协商/现代身份、Chat/Trade、真实游戏、服务端重启或持久交付/ACK 证据。不能把基础模块 profile 的通过标成整个未升级 Rework profile 已验收。后续完整 Harness 属于 TQ-20/50，P0 来源 spike 只提供这里声明的子集。
