# M1 裁决与携带项（交接 M2）

## 裁决（执行期如偏离须记录新裁决）
1. **每商品至多一条映射行**：product_mapping 以 UX_pmap_pair(product_id,platform_drug_id) + 既有过滤唯一索引保证；历史版本在 product_mapping_history。UpsertProductMappingAsync UPDATE 既有行而非多行并存。
2. **供应商解除在途守卫 org 级保守**：M1 无 dwbh→任务直连路径，按 org 下非终态 PURCHASE_IN_102/PURCHASE_RETURN_202 计数阻断；M2 入库分配落地后细化到供应商级。
3. **affectedNonTerminalTasks 恒 0**：M1 无任务产生路径，trace-policy 返回 0 并如实标注；M2 接线真实失效计数。
4. **导入 jobId "0" 约定**：已提交/失败批次重复上传返回 jobId="0"（不产生作业），batchId 仍返回；前端 ImportWizard 以 jobId!=="0" 决定是否轮询。
5. **幂等键用户作用域**：0007 将 UQ 改为 (idem_key,user_id)；请求哈希含查询串；并发败者回滚并重放胜者/409。
6. **BIN2 双变体**：集成套件默认 collation 与 TRACEFLOW_TEST_DB_BIN2=1 各跑一遍；外部 ID/码列显式 BIN2，库级 collation 仅影响未显式指定列。
7. **记录型响应 camelCase**：Host 无全局 JSON 命名策略；返回 C# 记录的端点一律 Results.Json(..., Web)；匿名对象端点保持字面 camelCase。
8. **CodeFormat 持久化 token**：EMPTY/S8P20/UNSUP（CodeFormatStorage），禁 enum.ToString 落库；0007 拓宽列并迁移 'Empty'→'EMPTY'。
9. **内部代理 id 为 JSON number**：审计记录 id（audit_log.id）与 JobStatus.fencingToken 等内部 int64 代理键在 API JSON 中为 number（int64 内部代理，非外部标识）；API-01 的"ID 字符串"约定仅适用于外部平台 ID（platformDrugId/refEntId/entId/billId 等）、业务单号/批次/作业 ID（jobId/batchId）与 rowversion 版本串（16 位大写 hex）。openapi.yaml 已在 SettingView.id、JobStatusResponse.fencingToken、/audit-logs description 显式标注。

## 携带项（M2 必须处理）
- RealKsoaAdapter 实现 + G-ERP 取证清单（ksoa-adapter-contract.md §3）；取证前 LIVE 同步保持禁用。
- trace-policy 失效接线（受影响非终态任务计数与阻断）。
- 供应商解除守卫细化到 dwbh 级。
- upload-dispatch/bill-query 处理器（M0 仅表结构与恢复路径）。
- 2008 R2 真实实例补验（AT-070，交付前；替换 M0/M1 门禁报告 ①③ 证据）。
- SYNC-03 上游下载分页落库同事务（0004 download_progress 就绪）。
- 迁移 checksum 漂移处置：T4 卫生提交（2495e47）为 0002–0005 补齐 EOF 换行（各 1 字节，无 DDL 语义变更），checksum 与 M0 账本快照不同；新建库无影响，存量 M0 库重跑 DbMigrator 会触发 checksum 不一致即停，升级前须按账本流程显式处置（M1 交付面向新建库）。
- **spec §4.7 运行账号/DPAPI 作用域显示**：protection_scope 已存储但无端点/UI 展示；M1 未实现，正式声明携带 M2（设置加固包）。最终全分支审查发现的唯一未声明缺口，本条即声明。

## 最终全分支审查新增携带项（M2 酌情，全部 Minor）
1. 手动同步同窗口重触发：UQ_job_intent 全局唯一，终态意图重入队返回既有 DONE jobId（202 不重跑）；G-ERP 取证后需 nonce 或终态 409（JobQueue.cs:29-34、SyncScheduler.cs:48）。
2. ImportStageJobHandler 审计 env 硬编码 "LIVE"（计划原文如此），与自动作业 env 约定不一致，M2 统一（ImportStageJobHandler.cs:52-54）。
3. 导入上限配置缝：import.maxFileBytes 目录上限 100MB，但 Kestrel 默认 ~30MB  body 限制未显式配置；调高设置超 ~22MB 原始文件会先触发不透明 413。M2 配置 Kestrel MaxRequestBodySize 与设置联动。
4. ImportWizard 预览固定前 50 行无分页（已如实标注"仅展示前 50 行"，UI-13 部分）；M2 补分页。
5. trace-policy 商品查找未加 org 过滤（MasterEndpoints.cs:155-198，单活跃组织不变量下良性）；M2 防御性补 org 作用域（与 T10 GET-by-id 同类）。

### 优先携带项（最终审查标定）
- **T6(e)**：sync.paused 非规范载荷（如 " true "）解析失败→暂停门禁 fail-open；M1 仅 MOCK 通道且 UI 写规范值，M2 G-ERP 前必须修。
- **T12(c)**：同信用代码 ≥2 企业版本行→CONFLICT 死胡同（确认 409 且 M1 无版本行清理路径）；属已声明产品决策，M2 提供处置路径。
