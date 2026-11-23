# TOP（码上放心）平台合同 — M2 候选声明

状态：**G-TOP 未取证**。本文件所有"候选"值在真实通道启用前必须经平台文档/联调取证修订；
M2 真实通道（env=LIVE）一律禁用（RealTopGateway 占位拒绝，422 TOP_NOT_CERTIFIED）。

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
