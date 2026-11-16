# 阿里云部署结果（2026-11-16）

按用户“服务端传到阿里云并部署，确保只有一个 Phinix 实例”的后续授权，已完成部署，独立复核时间：2026-11-16T18:53:34.627762+08:00。

- 服务地址：47.93.172.62:16200/UDP。
- 运行容器：phinix-relay-dev；唯一运行中的 Phinix 容器，唯一 PhinixServer 服务进程；重启次数 0。
- 已沿用 /opt/phinix/data，保留现有身份、配置、聊天与交易数据；没有创建合成账号、执行测试交易或回写旧数据快照。
- 旧服务正常停止，退出码 0；停服冷备及压缩完整性、SHA-256 校验通过。备份仅保存在服务器：/opt/phinix/backups/error-relay-test-20261116-b1bada。
- 旧容器保留为 phinix-relay-dev-rollback-error-relay-test-20261116-b1bada，处于停止状态。所有 4 个历史 Phinix 容器的自动重启均已关闭，只有新服务使用 unless-stopped。
- Docker 归档 SHA-256：c67d805f93ecaae34776b050da70d6ff8acb5bbe01ab6b289a51f9d8faa9234c。
- 云端镜像 ID：sha256:81c5869fa562c621ca0b16d0e6cc45a0f5b4cb3698ffff7bbc54ed6f466b54e5；源码隔离快照 a6677e2c818386312226f0f364cca152bf0c1893，包含已授权的原可靠性修改及本轮诊断修复，Common pin ff0593e81ef44250669d0ff81df1389ccc056d87。
- 镜像和运行容器内全部 62 个程序文件 SHA-256 与最终白盒发布目录一致；容器内 UDP 16200 已绑定，宿主发布端口已复核；启动错误级别记录 0。

本机 Docker 29 导出归档后，云端 Docker 26 采用归档配置摘要作为镜像 ID，与本机原构建 ID sha256:0e171b74402f433ca4eebc53009d6e2a64215548e6d0fedda781f238ee5a26cc 不同。第一次前置检查因此中止，现有服务未停。随后严格核对归档自身配置摘要、全部镜像层和启动配置、62 个程序文件后完成切换；没有绕过内容校验或另行重建。

可回退容器和冷备均留在服务器。若回退，应先停止新服务并保留当前数据，再恢复旧容器名称及启动策略；禁止同时运行两个服务，也不要直接覆盖成旧冷备，以免丢弃切换后的新写入。实际游戏弹窗、剪贴板和联机验收仍由本次客户端候选继续完成。

无凭据部署摘要：/home/hunyuan2333/Phinix/Deliverables/Phinix-Rework-Error-Relay-Test-2026-11-16/evidence/deployment-aliyun.json。生产原始日志、私钥与数据备份未复制到本地交付目录。
