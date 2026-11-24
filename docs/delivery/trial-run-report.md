# 试运行报告（≥7 天连续运行）

日期：2026-11-24。分支：`m4-closure`（M4 基线 `d0cac84`）。协议：`docs/delivery/trial-run-plan.md`。
脚本：`traceflow/tools/trial/`（`Start-Trial.ps1` / `Collect-Evidence.ps1` / `New-TrialSummary.ps1` / `Stop-Trial.ps1`）。

## 1. 结论

**7 天连续试运行未执行。**

本次交付只完成了**脚本可执行性冒烟**（M4 Task 6，2026-11-24）：在独立库 `TraceFlow_Trial_Smoke_04024924` 上
完成 建库 → 迁移 0001–0009 → 建管理员 → 启动宿主 → 单次取证（`ok:true`）→ 汇总（"未满 7 天，不构成试运行验收"）
→ 停止并删库。该冒烟证明脚本可用，**不构成试运行验收，也不代表系统已在真实时间跨度下稳定运行**。

> 在没有 7 天证据文件（每日 JSONL + `summary.md` + 宿主日志）之前，任何"已完成试运行/已连续运行 7 天"的说法都不成立。

## 2. 试运行口径（按 `trial-run-plan.md`）

| 项 | 口径 |
| --- | --- |
| 窗口 | 连续 ≥7 个自然日；证据覆盖不足 7 天即不构成验收 |
| 通道 | **恒为 MOCK**（`TraceFlow__Env=MOCK`）；禁止 LIVE、禁止向正式平台上传虚构交易 |
| 库 | 独立实例/独立库 `TraceFlow_Trial_*`；禁止使用 `TraceFlow_Dev`/`TraceFlow_M2Manual`/`TraceFlow_M2Gate`/`TraceFlow_M3Ui` 与任何 `TraceFlow_Test_*` |
| 每日证据 | `Collect-Evidence.ps1` 输出 `evidence/YYYY-MM-DD.jsonl`（`tsUtc/ok/status/metrics/alerts/dashboard/error`），建议 60 分钟一次 |
| 汇总 | `New-TrialSummary.ps1 -IntervalMinutes 60 -ExpectedDays 7` 生成 `summary.md`（覆盖天数、样本数/失败数、缺口数、backlog 最大/中位、alerts 集合、是否出现对账 INCOMPLETE） |
| 验收指标 | ① 覆盖 7 天且采样缺口为 0（或缺口有书面原因）；② 无未解释 FAILED 累积（每条失败有原因与处置）；③ 每个 alert code 可对应当日事实与处理动作；④ 重启后作业恢复、租约回收可见、审计可读；⑤ INCOMPLETE 对账与开放差异均有窗口/证据/归属；⑥ 证据完整归档 |
| 每日检查 | `/api/v1/ops/alerts`、`/api/v1/ops/metrics`（积压/24h 失败/租约回收/预算等待）、对账状态、`/api/v1/audit-logs`、采样连续性 |
| 已知 MOCK 限制 | 无真实 TOP/KSOA 调用；`uploadretail`/`billstatus`/`codedetail` 全为 fixture 形状（G-TOP 未取证）；发送成功率、平台受理、逐码结果为**本地模拟**；不能据此推断真实平台时延、限流与错误码分布 |

## 3. 试运行结果登记表

> 除"脚本冒烟"一行外均为**待填**：由执行人按实际运行填写，一行一天（或一次运行），不得预填/编造。
> 库名只允许 `TraceFlow_Trial_*`/`TraceFlow_Perf_*`；证据目录填写相对仓库根的路径（如
> `traceflow/tools/trial/.runtime/evidence`，归档后移动到交付证据目录）。

| # | 日期 | 运行人 | 库名 | 证据目录 | 异常数（未解释 FAILED / 告警） | 结论 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | 2026-11-24 | M4 Task 6（脚本冒烟，非试运行） | `TraceFlow_Trial_Smoke_04024924` | `.runtime/evidence/2026-11-24.jsonl`（1 个样本） | 0 / 0 | **冒烟通过**（脚本可用；未满 7 天，不构成验收） | 建库→迁移→管理员→启动→取证 `ok:true`→汇总→停止删库；详见 M4 Task 6 报告 §4 |
| 1 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 |
| 2 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 |
| 3 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 |
| 4 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 |
| 5 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 |
| 6 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 |
| 7 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 | 待填 |

填写完成后，本报告应追加：`summary.md` 路径、覆盖天数、样本总数/失败数、缺口数、出现的 alert code 清单与
逐条解释、`reconIncomplete24h` 与开放差异处置、重启演练（`trial-run-plan.md` §5.1）记录。

## 4. MOCK 与 LIVE 的差异（为何冒烟不能替代 LIVE 结论）

| 维度 | MOCK（本次冒烟/试运行通道） | LIVE（上线后） |
| --- | --- | --- |
| 平台合同 | `_verified:false` 候选（G-TOP 未取证） | 需完成 G-TOP 取证（方法名/入参/错误码/限值/回执语义） |
| 发送 | Fake/fixture，无真实 HTTP | 真实 `uploadretail`（321/116）调用，受限流预算与回查策略约束 |
| 回查/逐码 | fixture 形状 | 真实 `query.billstatus` / `query.codedetail` |
| 传输 | 本机回环 HTTP（无 TLS） | **必须 HTTPS**（`Cookie.SecurePolicy` 需改为 `Always`，代码改动未实施） |
| 账号 | 本机运行账号 | 专用服务账号（DPAPI `CurrentUser` 绑定，换账号需重录密钥） |
| 数据 | 试运行专用库 | 生产库（DBA 建库、备份/恢复策略按 `backup-restore.md`） |
| 审计 | 真实落库（MOCK 作业 env=MOCK，人工操作 env=LIVE） | 同左，但含真实业务动作 |

因此：**LIVE 可用性必须在 G-TOP/G-ERP 取证 + HTTPS + 专用服务账号 + 目标机部署（G-DEPLOY）全部完成后，
以真实业务窗口重新验证**；MOCK 试运行的指标只能说明本地作业引擎与观测面可用。

## 5. 未完成项

1. **7 天连续试运行未执行**（本报告结论；登记表除冒烟行外为空）。
2. G-TOP/G-ERP/G-POS/G-RETURN/G-OPENING 取证未完成（试运行无法接入真实平台）。
3. 目标部署机与 HTTPS 未取证（G-DEPLOY），试运行只在本机回环进行。