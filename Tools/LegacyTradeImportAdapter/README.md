# 停写离线导入（P3 / SV-34）

依赖方向：仅此独立工具编译原 Phinix 的六份原始 DTO，来源与逐文件 SHA 在 `Proto/provenance.json`。核心只接收 `TradeImportRecord`，没有旧协议字段或默认规则。没有改动 vendored protobuf 或 SDK pin。

```sh
dotnet run --project Tools/LegacyTradeImportAdapter -- protobuf /isolated/backup/trades /isolated/new-candidate
# 现有 Rework XML 使用 xml 替代 protobuf
dotnet run --project Tools/LegacyTradeImportAdapter -- --verify-restore /isolated/new-candidate
```

所有写入者必须停止，输入必须来自一致备份，输出必须是不存在的新目录。工具保留 `source.original`，生成 XML 中间证据和唯一权威 SQLite，回读全部交易数量、规范 snapshot 与 opaque payload，写 `import.json`；不修改原输入、不访问活动服务目录、不执行切换。

原 DTO 会丢弃未知字段，因此先要求整个 protobuf 编解码字节往返完全一致。非规范编码、重复字段、未知字段、损坏引用、不能无损表达的 stateful tunnel 使整个源进入 quarantine；不会跳过坏物品后导入。已知基础字段映射到 `core.item.vanilla`，质量枚举明确偏移。旧 accepted 不绑定报价版本，活跃报价保留、同意重置并计数。历史 terminal 的旧发送记录不能证明持久接收，双方均保留 OutcomeUnknown，不盲目重投。

切换是 P7 的独立部署步骤：停止全部写入者；备份当前应用与完整数据目录；审查 input SHA、import.json 和候选只读检查；只安装成套候选 `.sqlite3` / `.authority` / XML 证据。不要把源 XML 作为另一份长期可写权威。`--verify-restore` 仅核对保存的原始输入 hash，绝不自动覆盖活动数据。新库产生业务提交后，不可恢复旧备份来“回退程序”；必须停写、核对新增结果/资产后制定一致回退。

本轮只运行合成 fixture；真实历史玩家备份迁移、卷故障和实际切换未验收。工具最多接受 4 MiB 输入与热账本；超限明确拒绝，不能分批漏导。
