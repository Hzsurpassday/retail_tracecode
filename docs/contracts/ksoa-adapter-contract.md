# KSOA 来源适配器合同 v1（M1）

状态：**fixture-only**。真实适配器（ksoa-real-0）在 G-ERP 取证门禁通过前一律抛
AdapterNotCertifiedException，不执行任何 SQL。本文件是 G-ERP 取证的核对清单。

## 端口契约

- `ISourceAdapter.FetchAsync(entity, windowStartUtc, windowEndUtc, ct)`，entity ∈ sale / purchase-in。
- 返回 `SourceDocumentDto`：单据键字符串、DocumentType（SALE/PURCHASE_IN）、UTC 业务时间、
  raw 单头 JSON、行列表（raw 行 JSON 原样）、键异常列表。
- 窗口语义由真实适配器负责（按 rq+ontime 过滤）；fixture 适配器读全量静态文件，
  重复由服务侧 content_hash 去重，不产生重复版本。

## 候选结构（未取证，仅 Mock）

| 实体 | 单头 | 明细 | 单据键 | 行键 | 数量 | 码 |
|---|---|---|---|---|---|---|
| sale | salemast | salemxls | lshh | plh | shl（正售/负退/零无事件） | TraceCode（可 null） |
| purchase-in | jh_rkhz | jh_rkmx | djbh | dj_sn | shl | 无（上游出库单匹配，M2） |

业务时间 = rq(日期) + ontime(时刻)，按 Asia/Shanghai → UTC（DB-02）。
批号 pihao、效期 sxrq、单位 dw、往来单位 dwbh、验收人 yanshr 为候选字段。

## 键异常规则（SRC-02，服务侧落 ingest_exception）

- MISSING_LINE_KEY：plh/dj_sn 为空 → 不造行号，raw 行进异常台账，单据 INVALID。
- DUPLICATE_LINE_KEY：同单内行键重复 → 首条保留，其余进异常，单据 INVALID。
- ORPHAN_LINE：明细行无对应单头 → 按孤儿单据键聚合进异常。
- CROSS_DAY_REUSE：同单据键跨业务日复用且内容变化 → 异常 + INVALID（DB 侧亦校验）。
- KEY_REUSE：同单据键跨单据类型（lshh 与 djbh 碰撞）→ 服务侧 DB 校验（Task 7）。
- BAD_BUSINESS_TIME：rq+ontime 无法解析 → 不猜时间，异常 + INVALID。
- 行键按单内唯一（UQ_line_key(snapshot_id, source_line_key)）；plh/dj_sn 跨单重复属正常。

## G-ERP 取证清单（真实适配器实现前必须逐项验证并记录证据）

1. 真实表名/列名与候选结构差异（salemast/salemxls/jh_rkhz/jh_rkmx 是否为实际对象）。
2. lshh/plh、djbh/dj_sn 的 NULL、重复、跨日复用、跨表关联实测（SRC-02）。
3. rq/ontime 的真实类型与时区语义；是否存在 datetime 单列替代。
4. shl 的计量单位与 jlgg 换算方向（采购数量换算，M2 前必须验证）。
5. TraceCode 字段真实名称与扫码绑定粒度（单盒）。
6. 只读账号权限与连接超时（ConnectTimeout=5）、轮询对 POS 性能影响。
7. 增量窗口依据（是否有修改时间列；无则全窗口回扫策略确认）。

取证完成后：更新本合同版本号 → 实现 RealKsoaAdapter → 用户显式批准 LIVE 门禁。
