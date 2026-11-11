# 服务端 Docker 发布 — 2026-11-11

本批发布包含服务端缓存回收、恢复分页继续、临时 SQLite 争用恢复和通知/连接回调隔离。具体范围与限制见 Server-Reliability-Audit-2026-11-11.md。

本地 Docker 构建和两次相同数据卷下的启动、SIGTERM 停止检查通过。生产数据及现有容器未被修改。

## Actions 的单次发布

仓库的 SERVER_IMAGE_PUBLISH_ENABLED 全局自动发布开关保持原值。

工作流 docker.yml 新增 workflow_dispatch 输入 publish（默认 false）与 publish_tag（默认 sha-only，可选 dev）。所有质量、依赖审核、镜像构建与 smoke 检查仍是发布前置条件；手动 publish=true 只授权当次流程登录 Docker Hub 并推送。PR 永不发布。

先对发布分支运行 publish=false 验证；成功后在同一提交运行 publish=true、publish_tag=dev，发布 hunyuan2333/phinix-rework:dev 和 sha-完整提交号标签。该分支流程不更新 latest；没有另行部署线上服务器。

README 中原有全局开关说明仍适用于 push 自动发布；上述输入是显式手动流程的新增例外。最终运行链接、提交和镜像 digest 以交付记录为准。
