# Relay 开发测试服务部署记录

2026-11-09 按用户授权，通过默认 SSH 私钥部署到 hunyuan2333@47.93.172.62。部署来源为 codex/command-capability-relay 当前工作树，包含尚未提交的服务端与 Common 修改。

- 服务为 phinix-relay-dev，地址为 47.93.172.62:16200/UDP，重启策略为 unless-stopped。
- 镜像为 phinix-relay-dev:20261109，镜像 ID 为 sha256:041e60714a1ab6c67fbe6a435e6bec9ac888b21b4a05c99c8b3b73845115f78a。基于远端既有 phinix-runtime:10.0.13-ca20261022 构建，实际运行时为 .NET 10.0.13。
- 程序来源为 /opt/phinix/releases/relay-capability-20261109。完整部署组合含 62 个文件及 Chat、Trade、LegacyAdapter 插件，容器内全部文件哈希核对通过。
- 沿用 /opt/phinix/data 挂载到 /data。新服务已加载原有凭据、用户及聊天数据。部署未修改原数据为旧快照。
- 原先运行中的 phinix-server 和 phinix-hub-test 均正常退出，exit=0、oom=false。停止后分别备份，再在新服务验证通过后删除这两个容器。
- 备份目录为 /opt/phinix/backups/relay-capability-20261109，包含 main-data.tar.gz、main-app.tar.gz、hub-data.tar.gz 和校验清单。hub 原命名卷保留。备份仅保存在服务器。
- 公网真实 UDP 验证通过认证、登录、能力协商、完整快照、广播、玩家单播、可信发送者、接收者会话及 NoCapability。探针创建两个合成测试账号 RelayProbeA、RelayProbeB，未执行交易。两个探针已断开，其测试身份记录留在开发数据中。
- 最终检查容器 running=true、restartCount=0。实际游戏中的 Example 面板和业务验收继续由客户端黑盒测试完成。

部署包 SHA-256 为 cf79e95e4fdc87e9d8f70d9d9c7ecc15674329b2eb1f210c407a414b219c61c0。本地机器记录位于 /tmp/phinix-relay-deploy-20261109/deployment.json，公网探针结果位于 /tmp/phinix-remote-relay-probe/result.log。

原有其他停止状态的历史容器未纳入本次替换。新开发服务通过 Framework 自身提供 Relay，Example 测试插件安装在参与测试的客户端。
