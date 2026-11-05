# P6 UDP 接收预算与故障边界

本轮处理 SV-66/SV-67 的传输资源风险，基于 Server dev `5a0508acb703319e60dd5600e3e0facd0397b294` 与当前工作树。固定 LiteNetLib SHA-256 为 `320526331c04be0218341e68412b51df5752aa5381842c7c9d26e414c217eca0`。静态检查确认 `NetPeer.AddIncomingPacket` 按 wire 片数分配引用数组，`NetManager.EnqueueEvent` 没有队列预算。

- 通过已存在的固定 DLL 收包保护在 LiteNetLib 接收回调之前检查单包及合包内分片。单组最多 4096 片，按固定后端最大 MTU 加对象余量预留，单连接 48 MiB、全局 96 MiB。既计已进入重组表的组，也计可靠有序通道里待补缺口的分片，按连接和组 ID 去重。布局不匹配则拒绝启动。
- 对后端待轮询队列执行 512 事件和 32 MiB 可读载荷的入口水位控制。超限在后端接管、确认包之前丢弃，排空后恢复接收。已入可靠窗口的包或后端自身生成的事件可令队列短时超过水位，因此水位不是进程 RSS 硬上限。
- 未预期的 UDP 轮询/收包包装层异常只上报一次异常类型，由宿主请求正常关停并以失败状态退出。真实内存耗尽与固定 DLL 自有逻辑线程的未处理异常仍不在此恢复保证内。没有修改 DLL、子模块、应用协议或交易规则。
- 后续仍需对慢处理、持续并发洪峰以及出站可靠队列测量内存峰值与尾延迟。分片预算扫描的成本随活动连接及其可靠窗口增长，当前没有专用环境压力数据。

隔离回归使用真实 1 MiB 可靠有序 UDP 载荷、固定轮询屏障、固定的直接/合包异常分片、重复建立未完成组、事件数与载荷字节水位，并在真实后端队列注入单一异常条目。输入为合成数据，故障注入仅在测试中。专项命令为 `dotnet Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --udp-receive-budget`。初次测试用错了固定后端的最大 MTU 假设，导致“至少一个未完成组”的前提失败。修正上限组合后回归通过。证据目录 `/tmp/phinix-udp-budget.AKK1KY`。

`python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-udp-budget.AKK1KY/stage --timeout 120` 通过 49 步及 62 文件，含模型 seed `20261015` / `20261017`、架构检查和既有 Rework UDP 强杀恢复。独立进程 `tools/quality/host_smoke.py` 通过 4 项启停和失败检查。没有重新部署或进行真实客户端及长期负载验收。
