# KSOA/TOP 适配事实矩阵（M0 初版 + M1 更新）

状态约定：所有"核验证据"为空 = 未取证；对应真实通道禁用（G-ERP/G-TOP/G-POS/G-RETURN）。

| 事项 | 已知信息 | 核验证据/日期 | 采用规则 | 未核实时开关 |
| --- | --- | --- | --- | --- |
| 销售来源键 | salemast.lshh + salemxls.plh 候选（PRD SRC-02） | 待取证：NULL/重复/跨日复用/跨表关联查询 | 核实后固定；plh 空→接入异常 | 真实销售同步禁用 |
| 入库来源键 | jh_rkhz.djbh + jh_rkmx.dj_sn 候选 | 同上 | 核实后固定 | 真实入库发送禁用 |
| jlgg/sxrq/dw | 包装换算/效期/单位，所属对象待验证 | 待取证：单位与样例 | 明确方向与精度（UnitRatio） | 相关行阻断 |
| 主档表/分类 | 未给出（SRC-03） | 待取证：schema+脱敏样例 | 不猜表名 | UNKNOWN 阻断 |
| is_fpwc/yishj | 类型含义待验证 | 待取证 | 不作写回标记、不假设递增 | 不用于增量判断 |
| POS 采码 | salemxls.TraceCode varchar(50) 已预留；PRD SCOPE-09 称尚未采码，用户口述已扫码 | 待取证：逐盒扫码落库样例（G-POS） | 以样例为准 | 销售生产自动上报禁用 |
| TOP 合同 | 零售权限包指南已读；字段未全验证 | 待取证：附录 B 8 项 | connector 映射 | 真实发送禁用 |
| 消费者退货 | uploadretail/116（PRD） | 待取证：完整传参 | 人工首次确认 | 未验证不发送 |
| 采购退货 | uploadinoutbill/202；来源表未给出（DEF-03） | 待取证 | 显示未接入 | 首次上报禁用 |
| 负数退货 | salemxls shl<0 代表退货 | 待取证：原码与原单样例 | 人工首次确认 | 未验证类型不发送 |

## M1 落地状态（fixture-only，真实通道仍禁用）

- **来源同步通道**：M1 实现 `ISourceAdapter` 抽象 + `FixtureKsoaAdapter`（读 `docs/contracts/fixtures/ksoa/*.json`）+ `RealKsoaAdapter`（抛 `AdapterNotCertifiedException`）。`AdapterFactory` 按配置路由：fixture 模式可用，real 模式拒绝。LIVE 环境 `SourceSyncService`/`SyncScheduler` 拒绝同步（`Live_env_refuses_sync_in_m1`、`Live_env_tick_enqueues_nothing`）。**真实 KSOA 只读连接与键唯一性取证（G-ERP）仍未做**，上表所有"待取证"项不变。
- **主数据积累**：M1 平台药品/企业版本仅经 CSV 导入（`ImportService`，source_kind=MANUAL/IMPORT），非平台实时下载（G-TOP 未取证，M2 实现 listupout/药品下载）。
- **匹配/分类/设置/审计**：M1 已实现并集成测试（见 matrix.md M1 行）；均运行于 SQL 2019 授权降级，2008 R2 待补验。
- **POS 采码/退货/TOP 合同**：仍待取证，对应生产通道禁用不变。
