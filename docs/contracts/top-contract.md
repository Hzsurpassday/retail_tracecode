# TOP（码上放心）平台合同 — M2/M3 候选声明

状态：**G-TOP 未取证**。本文件所有"候选"值在真实通道启用前必须经平台文档/联调取证修订；
M2/M3 真实通道（env=LIVE）一律禁用（RealTopGateway 占位拒绝，422 TOP_NOT_CERTIFIED）。
M3 全部发送/回查/逐码证据均来自 fixture（MOCK），未调用真实平台；文中每个方法、字段、错误码
一律视为 `_verified: false` 候选，未经取证不得据此判定 LIVE 行为。

## 已确认（规格/PRD 点名）

| 项 | 值 | 来源 |
| --- | --- | --- |
| 授权错误 token | `FAIL_BIZ_AUTH_ERROR` | 设计 §3.3 |
| 部分成功 token | `WARN_BIZ_PARTSUCCESS` | PRD TOP-03 |
| 系统/业务错误分离 | error_response vs response_success=false | PRD TOP-01 |
| 查询超时≠受理结论 | TransportUncertain 不得判成功/失败 | PRD TOP-02 |

## 候选（待 G-TOP 取证）

| 项 | 候选值 | 说明 |
| --- | --- | --- |
| bill_code 前缀 | `TF` | settings `top.billPrefix` 可配；须平台验证合法字符集 |
| bill_code 最大长度 | 32 | settings `top.billMaxLength` 可配；超限抛错禁截断 |
| 限流错误 sub_code 前缀 | `FAIL_BIZ_RATE` | 命中即 RateLimited |
| 重复单据号 sub_code | `FAIL_BIZ_BILL_CODE_DUPLICATE` | 命中即 DuplicateBillCode→AcceptanceUnknown |
| 最小包装层级 token | `MIN` | 其余值/null=非最小或不明，阻断分配 |
| 分页参数 | `page_no`/`page_size`（默认 50） | 列表/详情通用 |
| 方法名 | `listupout`、`listupout.detail`、`query.listparts`、`getentinfolist` | 短名；真实 API 名取证后映射 |
| 列表响应 result | `{total_pages,total_count,page_no,bills:[{platform_bill_id,bill_no,upstream_ref_ent_id,bill_date,from_ent_id,to_ent_id,owner_ent_id,deliver_ent_id,source_version}]}` | 候选形状 |
| 详情响应 result | `{total_pages,page_no,lines:[{platform_line_id,platform_drug_id,batch_no,quantity,unit,package_level,codes:[{code,package_level,parent_code}]}]}` | 候选形状；parent_code 仅平台显式提供时落库（UP-02） |
| 限流限值 | 未知（本地预算列 NULL=不设帽） | 平台限流错误仍触发改期（JOB-03） |

## 预算键与暂停范围

- 预算键：`appkey:total`、`method:{method}`、`ent:{refEntId}:method:{method}`（门店 ref_ent_id 已知时）。
- 门禁检查范围 = `method:{method}`、`credential:top`、`ent:{refEntId}`（有主体时）；授权错误自动暂停写入范围 = 仅 `method:{method}`（+`ent:{refEntId}`），`credential:top` 仅人工全局暂停使用。
- 平台明确等待秒数优先；否则 30s×2^min(attempt,5)（上限 600s）+0..5s 抖动。

## M2 定稿状态（Task 15）

- 本文件所列接口入参/签名/限流限值/回执语义仍为**候选（_verified:false）**，M2 全程 fixture 通道验证，未取得平台合同证据（G-TOP）。
- M2 新增消费面：listupout / listupout.detail（上游下载，Task 5）、query.getbyrefentid / query.getentinfo（企业同步，Task 6）；fixture 见 `docs/contracts/fixtures/top/`。
- uploadinoutbill 102 载荷组装（Task 7 InboundTaskFactory）按候选合同生成并持久化；真实发送在 M3 upload-dispatch 落地且 G-TOP 取证前保持 TOP_NOT_CERTIFIED 禁用。
- 定稿条件：按设计附录 B 清单逐项平台测试工具核实并留存证据后，将对应行 _verified 置 true 并记录取证日期；未核实项不得进入 LIVE。

## M3 新增消费面（Task 10 定稿）

> 以下全部为**候选（`_verified: false`）**；M3 的发送、回查、逐码查询均在 MOCK 通道以 fixture 验证，
> **G-TOP 未取证**，不得据此宣称真实平台契约成立。

### 1. uploadretail（321 零售销售 / 116 消费者退货）

| 项 | 候选值 | 说明 |
| --- | --- | --- |
| 方法名 | `alibaba.alihealth.drugtrace.top.lsyd.uploadretail` | `UploadDispatchService.UploadRetailMethod` |
| bill_type | `321`（RETAIL_321）、`116`（CONSUMER_RETURN_116） | 102/202 仍走 uploadinoutbill（M2 合同） |
| 入参 | `bill_code`、`ref_ent_id`、`ent_id`、`bill_type`、`bill_time`、`detail` | `bill_code` 本地生成并复用（重试不改单号）；零售单据由门店主体提交，主体缺失直接拒绝发送（本地异常，不发请求） |
| detail | 分片载荷 JSON 字符串：`{eventKey, taskType, partIndex, businessDateTimeUtc, ruleVersion, lines:[{sourceLineKey, productSourceId, platformDrugId, quantity("0.000000" 字符串), unit, batchNo, codes:[码, ...]}], predecessorBillCodes?}` | 116 携带原销售单号 `predecessorBillCodes`（RET-04）；321 不含该字段；分片按最大码数拆分且同一行码不跨片 |
| 受理判定 | HTTP 成功/业务成功≠处理成功 | 上传成功只进入 Accepted/Processing 等待 billstatus 回查；传输不确定→AcceptanceUnknown，绝不自动重发（R-M3-10/11） |
| 重复单号 | 候选错误 `FAIL_BIZ_BILL_CODE_DUPLICATE` → DuplicateBillCode→AcceptanceUnknown，先回查 | M2 已登记，M3 发送路径沿用 |

### 2. billstatus（分片处理结论回查）

| 项 | 候选值 | 说明 |
| --- | --- | --- |
| 方法名 | `alibaba.alihealth.drugtrace.top.lsyd.query.billstatus` | `BillQueryService.BillStatusMethod` |
| 入参 | `bill_code`，可选 `ref_ent_id` | 按分片单号查询；只排回查作业，不重发 |
| 响应体 | 业务体 `{result_type, sub_process_flag, code_list:[{code, status}]}` 或完整信封（回退 `model` 节点） | 缺 `result_type` 且无 `model`→Unresolved |

billstatus → 分片结论（`BillStatusParser`，证据不足绝不升级）：

| 平台证据 | 分片结论 | 备注 |
| --- | --- | --- |
| `result_type="1"` | Processing | 上传成功≠处理成功，继续退避回查 |
| `result_type="2"` 且逐码均 SUCCESS | AllSuccess | 唯一可判整体成功的分支 |
| `result_type="2"` 但逐码含 FAILED/UNKNOWN | Partial（降级） | 不一致时按部分成功，不升级 |
| `result_type="2"` 但无 `code_list` | AllSuccess，逐码结论记 UNKNOWN | 整单成功但逐码证据缺失 |
| `result_type="3"` 或 `sub_process_flag="52"` | Partial | 逐码证据缺失同样 Partial |
| `result_type="4"` | Failed | 失败须留平台错误证据 |
| 无 `result_type` / `model` 为空 / JSON 不可解析 | Unresolved | 保持等待，继续回查，绝不视为成功 |

逐码 `code_list[].status`：`"1"`=SUCCESS、`"2"`=FAILED、缺失/其他=UNKNOWN（三态，`code_result` 表）。

回查节奏（本地策略，非平台合同）：平台明确等待秒数优先；否则 `top.query.backoffSeconds`（默认 30）× 2^min(先前回查次数, 5)，上限 600s，加 0..5s 抖动；达到 `top.query.maxAttempts`（默认 20）或 `top.query.manualAfterHours`（默认 24）仍无可判结论 → ManualAction + `reconciliation_difference(ACCEPTANCE_UNKNOWN)`。

### 3. codedetail（逐码依赖证据，零售校验）

| 项 | 候选值 | 说明 |
| --- | --- | --- |
| 方法名 | `alibaba.alihealth.drugtrace.top.lsyd.query.codedetail` | `SaleVerificationService.CodedetailMethod` |
| 入参 | `code`，可选 `ref_ent_id` | 每码每次校验运行至多一次 |
| 响应证据 | `{model:{exists, instore}}`；`model` 缺省时回退根节点 | 字段缺失=证据不足 |

codedetail → 逐码结论（`SaleVerificationService.Classify`）：

| 平台证据 | 逐码结论 | 备注 |
| --- | --- | --- |
| `exists=false` | FAIL `PLATFORM_NOT_FOUND` | 平台明确码不存在 |
| `exists=true` 且 `instore=true` | PASS `PLATFORM_INSTORE` | 依赖已满足 |
| `exists=true` 但 `instore=false`/缺失 | UNKNOWN `WAIT_DEPENDENCY` | 等采购入库成功，不伪造通过 |
| 业务拒绝（response_success=false） | FAIL `PLATFORM_REJECTED` | 不无限重排 |
| 传输不确定/限流/其他 | UNKNOWN `WAIT_RETRY` | 保持未决，退避重试 |

### 4. M3 明确未接入项

- **202 采购退货**：`PURCHASE_RETURN_202` 仅服务/测试路径（`uploadinoutbill` bill_type=202），**未接入 KSOA 采集**（R-M3-4）。
- **真实通道**：`top.retryPathCertified=false`（默认，fail-closed），受控重试在取证前 422 `RETRY_PATH_NOT_CERTIFIED`；`RealTopGateway` 对 LIVE 一律拒绝（422 `TOP_NOT_CERTIFIED`）。
- 平台限流限值、错误码全集、bill_time 精度/时区、bill_code 合法字符集仍未知；M3 不猜测平台限值。
