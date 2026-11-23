# KSOA 适配器合同（M1 定稿：fixture 形状 + 取证清单）

状态：**fixture-only**。真实 KSOA 只读适配（G-ERP）未取证，`RealKsoaAdapter` 抛 `AdapterNotCertifiedException`，LIVE 同步禁用。本合同固定 M1 内部接口与 fixture 形状，取证后仅替换适配器实现，不改内部契约。

## 1. 内部接口（TraceFlow.Application.Abstractions）

- `ISourceAdapter.FetchAsync(string entity, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct) → Task<IReadOnlyList<SourceDocumentDto>>`；另有 `ContractVersion` 属性标识合同版本。
- `SourceDocumentDto`：SourceDocumentKey, DocumentType, BusinessDateTimeUtc(DateTime?), SourceTimeRaw, SourceTimeZone, RawHeaderJson, Lines(IReadOnlyList<SourceLineDto>), KeyIssues(IReadOnlyList<SourceKeyIssue>)
- `SourceLineDto`：SourceLineKey(string?), ProductSourceId, QuantityOriginal(decimal), UnitOriginal, BatchRaw, ExpiryRaw, TraceCodeRaw, EventKind, RawLineJson（raw 行 JSON 原样保留，SRC-04）
- `SourceKeyIssue`：SourceLineKey(string?), IssueKind, Detail, RawLineJson（键缺陷留证，不造行）
- entity 有界枚举：`sale`/`purchase-in`；未知 entity 抛 ArgumentException。
- KeyIssues 非空→服务侧判 INVALID；窗口语义由真实适配器负责（按 rq+ontime 过滤），fixture 适配器读全量静态文件，重复由服务侧 content_hash 去重，不产生重复版本。

## 2. Fixture 形状（docs/contracts/fixtures/ksoa/*.json）

| fixture | 覆盖 |
| --- | --- |
| sale-normal.json | 正常销售：lshh 头键 + plh 行键 + 逐行 TraceCode |
| sale-missing-plh.json | plh 空→SourceKeyIssue（不造行号） |
| sale-mixed-return.json | 正负 shl 混单→SALE/CONSUMER_RETURN 分事件不抵消 |
| purchase-in.json | 入库：djbh 头键 + dj_sn 行键 + pihao 批号 + PURCHASE_IN 事件 |

fixture 为 **Mock 形状**，非真实 KSOA schema 事实；列名/类型以 G-ERP 取证为准。

### 候选结构（未取证，仅 Mock；与 source-mapping.md §2 一致）

| 实体 | 单头 | 明细 | 单据键 | 行键 | 数量 | 码 |
|---|---|---|---|---|---|---|
| sale | salemast | salemxls | lshh | plh | shl（正售/负退/零无事件） | TraceCode（可 null） |
| purchase-in | jh_rkhz | jh_rkmx | djbh | dj_sn | shl | 无（上游出库单匹配，M2） |

业务时间 = rq(日期) + ontime(时刻)，按 Asia/Shanghai → UTC（DB-02）。批号 pihao、效期 sxrq、单位 dw、往来单位 dwbh、验收人 yanshr 为候选字段。

## 3. G-ERP 取证清单（取证前真实通道禁用）

1. 脱敏 schema：salemast/salemxls/jh_rkhz/jh_rkmx 列名与类型（尤其 lshh/plh/djbh/dj_sn/spid/shl/TraceCode/pihao/rq/ontime）。
2. 键唯一性查询：`(lshh,plh)`/`(djbh,dj_sn)` 的 NULL/重复/跨日复用/跨表碰撞实测。
3. 稳定提交约定：is_fpwc/yishj 类型含义（不作写回标记、不假设递增）。
4. 主档来源表（SRC-03，不猜表名）。
5. jlgg/sxrq/dw 所属对象与换算方向/精度（UnitRatio 证据）。
6. 时间样例：rq/ontime 归属与时区（Asia/Shanghai 解析验证）。
7. 只读账号权限验证（AT-001）：确认适配器账号仅 SELECT，无写权限；连接超时（ConnectTimeout=5）与轮询对 POS 性能影响。
8. POS 逐盒扫码落库样例（G-POS，SCOPE-09 冲突裁决）。
9. 增量窗口依据（是否有修改时间列；无则全窗口回扫策略确认）。

取证完成后：更新 adapter-facts.md 证据列与日期、固定 source-mapping.md 候选键、实现 RealKsoaAdapter、解除 LIVE 同步门禁（用户显式批准）。

## 4. 键异常规则（SRC-02，服务侧落 ingest_exception；T5 携带，保持有效）

- MISSING_LINE_KEY：plh/dj_sn 为空 → 不造行号，raw 行进异常台账，单据 INVALID。
- DUPLICATE_LINE_KEY：同单内行键重复 → 首条保留，其余进异常，单据 INVALID。
- ORPHAN_LINE：明细行无对应单头 → 按孤儿单据键聚合进异常。
- CROSS_DAY_REUSE：同单据键跨业务日复用且内容变化 → 异常 + INVALID（DB 侧亦校验）。
- KEY_REUSE：同单据键跨单据类型（lshh 与 djbh 碰撞）→ 服务侧 DB 校验。
- BAD_BUSINESS_TIME：rq+ontime 无法解析 → 不猜时间，异常 + INVALID。
- 行键按单内唯一（UQ_line_key(snapshot_id, source_line_key)）；plh/dj_sn 跨单重复属正常。
