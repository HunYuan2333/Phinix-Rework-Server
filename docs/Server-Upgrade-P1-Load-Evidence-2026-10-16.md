# P1 持久状态加载失败保护证据（2026-10-16）

SV-15 / TQ-10 / FR-06 子集。基于 Server `5a0508acb703319e60dd5600e3e0facd0397b294` 的 dirty 工作区；精确输入 hash、命令、退出码、gitlink 和发布文件 hash 见 [机器记录](Server-Upgrade-P1-Load-Evidence-2026-10-16.json)。没有提交、部署或跨仓库修改。

## 变更及不变量

实际 Framework 原先捕获插件 Load 异常后继续监听，随后定时/最终 Save 可以覆盖原状态。现在任一已登记持久对象加载失败或无存储路径都阻止 Framework 构造成功；构造失败统一关闭已登记插件，不调用保存。加载失败标记也阻止后续 TrySave；没有实现在线 reload 回滚或允许部分状态继续服务的承诺。

核心 credentials/users 在任何插件激活前加载。Main 捕获启动失败、返回退出码 1，不运行正常最终保存；错误仅输出类型，不输出解析异常 message/inner exception 中的私密载荷。宿主没有 Chat/Trade 特判；没有更改插件、Common、线协议、依赖版本或 SDK pin。

## 实际执行

- 构建：`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet build Tests/ServerLifecycleTests/ServerLifecycleTests.csproj -c Release --no-restore -m:1 -p:BuildInParallel=false` 通过，0 error；保留 vendor 既有 NETSDK1212 warning。
- 修复前：`python3 tools/quality/host_load_smoke.py --server Server/bin/Release/net10.0/PhinixServer.dll --test-tool Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-load-before-20261016` **非零**，首例报 `Invalid persistent state reached listening`。负对照没有改成错误预期。
- 修复后同入口 `/tmp/phinix-load-after-20261016`：6 项通过。先用真实空宿主启动/stop 生成文件，再分别损坏交易 XML、聊天 protobuf、credentials、users、配置，或把交易文件改为目录。检查监听前退出 1、错误明确、全部原持久文件 SHA256 不变，合成私密载荷未进 stdout/server.log。临时目录自动清理；不触碰玩家数据。
- `python3 tools/quality/check.py --suite stage --isolated --output /tmp/phinix-load-stage-20261016`：27 步全部通过，33 个发布制品通过检查；包含原控制台、golden、真实安全组件、独立模型/属性、架构、生命周期和快照回归。既有属性 seed `(20261015,20261017)`，加载检查 seed 为 null，无生产 fault hook。
- 发布：`MSBuildSDKsPath=/usr/share/dotnet/sdk/10.0.112/Sdks dotnet publish Server/Server.csproj -c Release --no-restore --no-self-contained -m:1 -p:BuildInParallel=false -o /tmp/phinix-load-publish-20261016` 通过；从 build 输出复制四个官方插件到 Extensions。
- 实际发布宿主：同 load 入口把 `--server` 换为 `/tmp/phinix-load-publish-20261016/PhinixServer.dll`、output 换为 `/tmp/phinix-load-published-smoke-20261016`，6 项通过；`python3 tools/quality/host_smoke.py --server /tmp/phinix-load-publish-20261016/PhinixServer.dll --test-tool Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll --output /tmp/phinix-load-lifecycle-smoke-20261016`，3 项通过（EOF/SIGTERM、console stop、插件保存失败）。宿主使用真实 UDP，属于 Linux 进程启动/停止证据，不是客户端协议 Harness 或资产验收。

## 范围和下一依赖

缺失文件仍按首次运行创建；本轮不能识别持久卷未挂载与真正初次启动。无跨文件回滚/事务、逐操作 COMMIT、SQLite/outbox、强杀后的交易恢复或游戏内物化/库存/存档验证。第三方插件若自己吞掉 Load 错误或在 Load/Shutdown 中写数据，不在本轮保证内。未验证 Windows、远端 CI、Docker 和真实客户端联合版本。

只改服务端；当前客户端已独立推进到 `27878e7fb78c11fec4f390db317dc0db5b0b10c9`、Common gitlink `c165f61d749189f700ca896d63d9366d5c71dbf7`，未将它标为兼容通过。Server 固定 Common `67f243d` / protobuf `4b0c3aa` 不变。

P1 继续 SV-11/13 缓存与发送结果边界，再进入最小 P2/P3。旧端尽力交付策略保持：离线保留待交付，可能已发而无回执保留未知；本轮未实现该交付路径。
