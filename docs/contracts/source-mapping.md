# 来源映射（PRD §4.2 归一化内部契约 抄录 + KSOA 候选列映射）

来源：`docs/PRD-TraceFlow-单门店-V1.0.md` §4.2。以下五个对象是**本系统内部字段**，不声称与 KSOA 或 TOP 原字段完全一致。
所有 KSOA 候选列映射均为**候选，待 G-ERP 取证**；取证前真实 ERP 同步禁用（见 `adapter-facts.md`）。

## 1. 归一化内部契约五个对象（PRD §4.2 字段表抄录）

### SourceDocument（来源单据头）

| 字段 | 说明（依 PRD §2/§4 语义） |
| --- | --- |
| sourceSystem | 来源系统标识（如 KSOA） |
| sourceDocumentKey | 来源单据稳定键（字符串，SRC-02 候选键核实后固定） |
| documentType | 单据类型（销售/采购入库等） |
| businessDateTime | 业务时间（UTC datetime2(3)，DB-02） |
| sourceTimeRaw | 来源时间原值（保留原样） |
| sourceTimeZone | 来源时区（Asia/Shanghai 解析，DB-02） |
| completenessStatus | 完整性状态（DISCOVERED/STABILIZING/COMPLETE/SOURCE_CHANGED/INVALID，SYNC-02/05） |
| contentHash | 快照内容摘要（去重与版本证据，SYNC-01） |
| observedAt | 观测时间 |

### SourceLine（来源单据行）

| 字段 | 说明 |
| --- | --- |
| sourceLineKey | 来源行稳定键（字符串；禁止查询行号冒充，SRC-02） |
| productId | 本地商品稳定 ID（spid，字符串） |
| quantityOriginal | 原始数量（decimal(18,6) 定点，SRC-05；正数销售/负数退货） |
| unitOriginal | 原始单位 |
| batchRaw | 批号原值 |
| expiryRaw | 效期原值（精度 DAY/MONTH/UNKNOWN，SRC-05） |
| traceCodeRaw | 追溯码原值（varchar(50) 按字符串取出，SRC-04） |
| lineHash | 行内容摘要 |
| eventKind | 事件类型（SALE/CONSUMER_RETURN/NO_EVENT/PURCHASE_IN，DEF-06/RET-01） |

### InboundDocument 扩展（入库单据头附加字段）

| 字段 | 说明 |
| --- | --- |
| supplierId | 本地供应商稳定 ID（dwbh，字符串） |
| sourceBillNo | 来源单号（djbh 原值） |
| 来源单号关联字段 | 实际存在才映射（不猜字段，SRC-03/G-ERP） |

### Product（商品档案）

| 字段 | 说明 |
| --- | --- |
| productId | 本地商品稳定 ID（spid） |
| displayCode | 显示编码（不能替代 spid，PRD §2） |
| name | 名称 |
| approvalNo | 批准文号（保留 raw 与 normalized，MATCH-02） |
| manufacturer | 生产企业（身份未确认时只有近似名称则降级候选，MATCH-01） |
| dosageForm | 剂型 |
| strength | 含量规格 |
| packageSpec | 销售包装规格 |
| unit | 单位 |
| traceRequiredStatus | 追溯分类（UNKNOWN/REQUIRED/EXEMPT，MD-01） |

未知属性为 null 而非猜值（PRD §4.2 原文约束）。

### Supplier（供应商档案）

| 字段 | 说明 |
| --- | --- |
| supplierId | 本地供应商稳定 ID（dwbh） |
| name | 名称 |
| creditCode | 信用代码（精确匹配候选依据，ENT-01） |
| role | 企业角色（采购供应商/发货方/货主/配送方分别存储，ENT-02） |
| 原始字段摘要 | 来源原值摘要（raw_json） |

## 2. KSOA 候选列 → 内部字段映射

**每行均为候选，待 G-ERP 取证。** 取证内容：脱敏 schema、列类型、组合键/连接唯一性查询结果、稳定提交约定、主档来源、jlgg/sxrq 语义、时间样例（PRD §15.1 G-ERP）。

| KSOA 候选列 | 内部字段 | 状态 |
| --- | --- | --- |
| salemast.lshh | SourceDocument.sourceDocumentKey（销售头候选键组成） | 候选，待 G-ERP 取证 |
| salemxls.plh | SourceLine.sourceLineKey（销售行候选键组成；NULL→接入异常，不造行号） | 候选，待 G-ERP 取证 |
| salemxls.spid / jh_rkmx.spid | SourceLine.productId / Product.productId | 候选，待 G-ERP 取证 |
| salemxls.shl / jh_rkmx.shl | SourceLine.quantityOriginal（正数销售、负数退货、0 保留 NO_EVENT） | 候选，待 G-ERP 取证 |
| salemxls.TraceCode | SourceLine.traceCodeRaw（varchar(50) 字符串取出；raw+normalized 双存，SRC-04） | 候选，待 G-ERP 取证 |
| jh_rkhz.dwbh | InboundDocument 扩展.supplierId / Supplier.supplierId | 候选，待 G-ERP 取证 |
| jh_rkmx.pihao | SourceLine.batchRaw | 候选，待 G-ERP 取证 |
| jh_rkhz.djbh | InboundDocument 扩展.sourceBillNo（入库头候选键组成） | 候选，待 G-ERP 取证 |
| jh_rkmx.dj_sn | SourceLine.sourceLineKey（入库行候选键组成） | 候选，待 G-ERP 取证 |
| salemast.rq / ontime、jh_rkhz.rq / ontime | SourceDocument.businessDateTime / sourceTimeRaw（实际归属待核对） | 候选，待 G-ERP 取证 |
| jh_rkhz.yanshr | 验收相关人员原值（保留原值，语义待验证） | 候选，待 G-ERP 取证 |
| sxrq / dw / jlgg（所属对象待验证） | expiryRaw / unitOriginal / UnitRatio 换算证据（方向与精度待验证） | 候选，待 G-ERP 取证 |
| 头 is_fpwc / 行 yishj | 不映射为写回标记、不用于增量判断（类型含义待验证） | 候选，待 G-ERP 取证 |
| 商品/供应商主档表 | Product / Supplier 档案来源 | 未给出，不猜表名（SRC-03），待 G-ERP 取证 |

候选组合键（SRC-02）：销售 `(source_system, lshh, plh)`、入库 `(source_system, djbh, dj_sn)`。实施先验证 NULL、重复、跨日复用和跨表关联；若不唯一，需补充有证据字段形成稳定键并更新适配合同。ID 全程字符串，无数值解析或前导零丢失。