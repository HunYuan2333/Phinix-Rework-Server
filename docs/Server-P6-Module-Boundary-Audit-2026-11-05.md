# P6 服务端模块边界与生命周期审查

审查对象为 Server `dev` 工作树，基线 `5a0508acb703319e60dd5600e3e0facd0397b294`，含既有未提交改动。参考 Design-Philosophy、Compatibility-Boundaries 及 P5 审查。聚焦宿主组合、传输、Framework、插件及存储之间的静态依赖和资源所有权。

| 范围 | 结论 |
| --- | --- |
| 宿主与 Autofac | `ServerComposition` 负责装配和作用域，插件从通用上下文取服务。原先启动失败时，`Run` 先离开 Autofac 作用域，`Main` 才关闭插件，导致插件关闭可能访问已释放的借用服务。已把失败清理移入作用域。 |
| 保存与关停 | 原先定时器早于监听启动，SIGTERM 回调直接关停服务。现由主线程执行关停，信号只发退出请求。定时器在启动成功后启用，失败清理和正常关停共用保存锁，前者跳过最终保存。保留原有先停止传输、关闭扩展、再保存的顺序。 |
| 事件与资源所有权 | 保存定时器、poll 转发、宿主日志转发、控制台退出监听以及 dispatcher 闭包在对应作用域结束时解绑。Framework 已有数据加载失败与 transport 订阅撤销路径。 |
| 插件与存储 | Chat 和 Trade 分别依赖共享契约和通用存储；Legacy Adapter 同时依赖两者的服务接口和部分实现所在程序集，依赖方向朝向业务提供者。宿主没有 Chat/Trade 专用启动或恢复分支。 |
| 可拆分边界 | ServerRuntime 的传输接口目前有一部分定义在 Connections.Server；宿主扩展上下文还暴露具体 ServerUserManager。提取中立接口或撤掉旧注册会改变程序集或插件依赖面，先盘点二进制消费者，再决定是否迁移。 |
| 关停契约 | Framework 为最终保存保留已注销的持久对象。第三方若在 Shutdown 中先 Dispose 自己的持久对象，最终保存可能失败。应先定义扩展持久对象的关停契约，再设计迁移，避免调整顺序时改变现有持久化语义。 |
| 线程与结构 | 有界工作队列约束单轮回调数量，任意插件回调仍可能阻塞轮询和停止。宿主入口仍使用静态运行状态；本次将运行循环和关停拆成明确入口，宿主仍按单进程单实例运行。 |

代码变更限于宿主和保存生命周期，测试增加了无最终保存的中止分支，以及独立进程占用 UDP 端口后拒绝启动的回归。后者区分正常的空存储初始化和额外的关停保存。原来的业务、报价和 ACK 判定、账本事务及协议字段保持原样。

## 实际验证

使用现有 `p4-work/run` 包装器运行，证据在 `/tmp/phinix-p6-audit.iANhoU`。

- `dotnet build Tests/ServerLifecycleTests -c Release -m:1 -nr:false -p:BuildInParallel=false -p:NuGetAudit=false` 通过。随后运行生成的 `ServerLifecycleTests.dll` 和 `--framework-lifecycle-audit` 均通过。首次无本机端口权限的运行失败，开放测试端口后执行通过。
- `python3 tools/quality/host_smoke.py --server Server/bin/Release/net10.0/PhinixServer.dll --test-tool Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-p6-audit.iANhoU/host-final` 四项通过：EOF/SIGTERM、控制台 stop、保存失败、UDP 端口占用。首次端口占用测试误将启动阶段初始化空快照视为最终保存，已按实际生命周期修正测试，要求初始化只写一次。
- `python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-p6-audit.iANhoU/stage --timeout 120` 通过 49 步及 62 个发布文件检查，包含架构约束、模型 seed `20261015` / `20261017`、现有 Rework UDP 独立进程恢复。新增启动故障点使用真实占用端口，无随机 seed。
- 本轮没有重建 Docker 镜像、部署服务端或重跑真实游戏联测。
