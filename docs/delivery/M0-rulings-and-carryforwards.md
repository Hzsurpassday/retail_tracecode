# M0 执行裁定与携带项（持久记录）

> 来源：SDD 台账（.superpowers/sdd/2026-11-22-m0-foundation/progress.md，执行完毕后按流程删除）。本文件持久保存全部控制者裁定与 M1+ 携带项，供后续里程碑计划引用。分支 m0-foundation，最终 HEAD 23a53d3。

## 裁定（Rulings）

- Ruling: 目录原无 git 仓库 → 控制器已 `git init -b m0-foundation`（2026-11-22）。新仓库无既有代码，不另建 worktree；全部 M0 工作在 m0-foundation 分支。— 依据：计划 Task 1 本就含 git init；用户已批计划。— 若错：分支名不合用户习惯，可改名，无数据损失。
- Ruling: git 全局/本地 user.name/email 未配置 → 提交一律用 `git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit`（身份取自 FDE-self-study\.qoder\env，noreply 邮箱），不改 git config。— 若错：作者署名不合用户意愿，可 amend 署名。
- Ruling: 本 harness 的 Agent 工具不支持 model 参数 → 所有子代理继承会话模型（记录为 fallback，无法按任务分层选模型）。
| File Structure 草图 vs 任务正文 | `Infrastructure\Jobs\JobQueueSql.cs`、`Application\Jobs\*` | **不一致**。Ruling: 以任务正文为准——JobQueue 实现在 `TraceFlow.Infrastructure/Jobs/JobQueue.cs`（T9 测试 using 即此命名空间），SendRecoveryService 在 Infrastructure/Jobs（T10）；Application 只放 Abstractions。草图为过时速记。— 若错：无，任务正文自洽且测试引用即证据。 |
- Task 1: implementer DONE — commit 46a7753 (root, 21 files); build 0 warn 0 err; 关注点：brief 散文"8 项目"与命令实际 7 项目不符，实现者以命令为准（正确）。Ruling: 项目数=7（Domain/Application/Infrastructure/Host/DbMigrator/UnitTests/IntegrationTests），散文计数笔误，以 Step 3 命令为准。— 若错：无。
- Task 4: Ruling: 立即修复（fix round 1）——spec SRC-05/AT-029 为绑定权威，计划代码在极端域违背它；用 BigInteger 精确整数换算（值×10^6 为精确整数，分子乘积整除分母才成功，结果域校验后构造 Qty），任何失败统一 UnitConversionException；公共契约不变；补极端回归测试。— 若错：换算实现重写引入新缺陷，由 scoped re-review + 既有 15 测试兜底。
- Task 7: Ruling: format nvarchar(16) vs CodeFormat.Supported8Prefix20(18字符) 为计划缺陷，非本任务执行缺陷——M0 DDL 保持逐字；M1 计划必须含显式任务（持久层枚举→短 token 映射，或正式加宽迁移），不得只留报告脚注。— 若错：M1 朴素 enum.ToString() 持久化必然 2628/8152 失败，届时被迫返工。
Ruling: 采纳审查员一行修复 —— FAILED 更新改 job_state IN (N'RUNNING', N'PENDING')，候选谓词不变（仅有效 RUNNING 租约保护分片）；补回归测试（PENDING dispatch + Dispatching 分片 → 恢复后分片 AcceptanceUnknown、dispatch 作业 FAILED、query 作业唯一）。理由：本服务是 query-first 不变量的指定属主，宁可把合法预发送重试也转为先回查（安全方向），不留重传向量。代价若错：M2/M3 合法 reschedule 重试被转为回查路径，多一次平台查询延迟，无数据风险。
Ruling: 两项 Important 现在修（Task 11 fix round 1），不携带到 Task 13 —— 修复各为一行级、局部于本任务文件，现在修使 AUTH-02 不变量在创建点即结构性成立，避免跨任务必修复依赖。端点 compile-only 无行为测试属 Task 13 ApiGateTests 范围；携带要求：Task 13 须加超长用户名登录测试（断言 401+审计行存在）。代价若错：多一轮修复开销；无数据/安全风险。
Ruling: 现在补一个负向测试（已登录客户端移除 X-CSRF-TOKEN → POST /admin/users → 400 CSRF_INVALID）——本任务交付物就是安全门禁，门禁必须能证伪自身；一测试成本极低。代价若错：多一轮修复开销。
Ruling: 采纳修复者对裁定前提的实证纠正 —— 我预判 viewer 会 400 CSRF_INVALID，实际 UseAuthorization 先于 CSRF 中间件（viewer 恒 403 不可证伪）；修复者改用 admin+有效 Idempotency-Key（无中间件→200 可证伪）。与 Task 4 同类：裁定数学/管线前提错误由修复者实证纠正，接受。代价若错：无（测试已实证双向）。
Ruling: 授权先于 CSRF 的管线顺序非防护漏洞 —— 未授权请求在变更前已被 403 拒绝；已授权变更仍须有效 CSRF 令牌（中间件在端点执行前）。无需重排 Program.cs。代价若错：若未来有端点绕过 UseAuthorization 则需重审（M1 新端点审查项）。
Ruling: I-1 属计划强制缺陷（plan L499 Base36 字母表含 'P' 且分片分隔符也是 'P'；Base36(2197)="1P1" 与 Generate(TF,1,1)="TF1P1" 确定性碰撞，已独立验算）— 修复：字母表剔除 'P' 改 Base35，分隔符保留 'P'，加碰撞回归+1..100000×0..3 唯一性扫描 — 代价：同一 taskId 的码值与旧公式不同，但 M0 无任何持久化生产码，零迁移成本；确定性/唯一性不变量更强。
Ruling: I-2 env 大小写 — 修复：Program.cs 启动 ToUpperInvariant 规范化 + 非 {MOCK,LIVE} 快速失败；ApiGateTests 断言改 "MOCK"；openapi/data-dictionary 描述同步 — 代价：配置写 "Mock"/"Live" 仍可用（规范化），非法值启动即报错（期望行为）；BIN2 变体 fixture 列为 M1/交付前投资，不在本波。
Ruling: I-3 worker 静默 catch — 修复：注入 ILogger，吞异常记 Warning — 代价：仅日志噪音。
Ruling: I-4 将 T11 延期 minor 升级为合并前修复 — HasAsync/ForUserAsync 加 is_active=1 过滤 + 停用即拒测试 — 代价：几乎为零，fail-closed 方向。

## 携带项 / 延期观察（Carry-forwards）

- Task 3: dispatched implementer, BASE=3ea986f; DONE commit 53e5648, 62/62 unit tests。实现者观察：Base36 字母表含 'P'，整单 body 可能含 P——后续阶段分片解析必须依赖持久化记录而非字符串拆分（设计 IDEM-02 已要求生成即持久化，不构成缺陷；M2/M3 dispatch 需携带此提示）。
- Task 3: minor (deferred): DoesNotContain("P") 断言值依赖（taskId=25 会含 P）——下游解析禁止以 P 拆分，依赖持久化记录（M2/M3 dispatch 携带）
- Task 4: dispatched implementer, BASE=53e5648; DONE commit 4cfd495, 77/77 unit tests。偏差：brief 测试中未使用的 catch 变量名触发 CS0168→去掉变量名（合理，TreatWarningsAsErrors）。观察：极端 Qty×Numerator 可抛原生 OverflowException（brief 范围外，M1 携带）；交错控制符+空白→Unsupported 为 brief 精确行为。
- Task 4: minor (deferred): UnitRatio ctor nameof(numerator) 误报分母参数名；wrap 丢 InnerException；normalizer 测试未断言 Normalized 字符串；缺 max 域值/负数/内嵌空白测试（M1 携带）；6 文件缺尾随换行
- Task 5: dispatched implementer（携带用户授权 2019 降级 override：TRACEFLOW_TEST_DB_ALLOW_NEWER=1 显式开关+如实 banner+ServerVersion/IsDowngraded），BASE=e30c276; DONE commit 230e66d，Integration 4/4（SQL 15.0.2000.5）+Unit 82/82；默认门禁拒绝 2019 已验证；无遗留测试库。观察：success=0 停止路径已实现未测（brief 仅 3 测试）；banner 需 detailed verbosity 可见；DbMigrator 自身输出不复制脚本（运行时传目录，brief 如此）。
Task 9: deferred minors → (a) plan-mandated: claim CTE 无 ROWLOCK，页锁竞争下可能虚假空取（安全性不受影响，M0 量级可接受）；(b) plan-mandated: 重复 intent 回查在 SNAPSHOT 隔离级别调用方事务下可能返回 0 —— Task 10+ 调用方选隔离级别时须加守卫/注释；(c) plan-mandated: ct 仅连接打开生效，Task 13 worker 停机需关注；(d) plan-mandated: Application 依赖具体 SqlClient 提供程序（brief 签名所迫，抽象化超出范围）；(e) 3 个新 .cs 文件缺 EOF 换行；lease>24.8 天 DATEADD int 溢出（理论值）。
Task 10: deferred minors → (a) plan-mandated: 全局扫描不分 org/env（M0 单店可接受，M2 多 env worker 时重审）；(b) plan-mandated: 版本守卫仅 state 不复查租约（TOCTOU，安全方向，fencing 保护完成写）；(c) AppendStaleEvidenceAsync 对不存在 taskId 静默 0 行、不校验 partId 归属（M2 调用方落地时加 rows==1）；(d) ct 未转发 Dapper 命令（brief 原样）；(e) 种子未断言 claim.Id==jobId（未来测试留 PENDING dispatch 时脆弱）；(f) 3 新文件缺 EOF 换行。
Ruling: 两项 Important 现在修（Task 11 fix round 1），不携带到 Task 13 —— 修复各为一行级、局部于本任务文件，现在修使 AUTH-02 不变量在创建点即结构性成立，避免跨任务必修复依赖。端点 compile-only 无行为测试属 Task 13 ApiGateTests 范围；携带要求：Task 13 须加超长用户名登录测试（断言 401+审计行存在）。代价若错：多一轮修复开销；无数据/安全风险。
Task 11: deferred minors → (a) GrantAsync 并发重复授权 2601 竞态（fail-closed，Task 12 幂等包覆盖同键重试；后续 catch-2601-as-noop 优于 2008 R2 MERGE）；(b) AdminInitializer 无测试+重复/超长用户名崩溃（tx 干净回滚，安全失败；后续友好 catch→exit 2+长度守卫）；(c) HasAsync 不查 is_active（M0 无停用端点，不可达；停用时须会话失效或 join）；(d) admin 端点 Password null→NRE 500、Reason>1000/DisplayName>100 截断 500（Task 13 验证加固）；(e) 登录时序侧信道用户名枚举+SuccessRehashNeeded 未重哈希；(f) logout 先 SignOut 后审计（审计失败 500 但会话已毁）；(g) 12 新文件缺 EOF 换行。
Task 11: fix round 1 DONE commit e11c469（2 文件 +7/−3），build 0 警告，Integration 35/35 + Unit 84/84，零遗留库；行为测试携带至 Task 13 ApiGateTests（超长用户名→401+审计行；转义 digest）。
Carry-forward → Task 13: ApiGateTests 必须含超长用户名登录测试（断言 401 + auth.login.failed 审计行存在）与转义 digest 验证；admin 端点输入验证加固（Password null、Reason>1000、DisplayName>100）。
Task 12: DONE commit 94c18fd，Integration 37/37 + Unit 84/84，sln 0 警告，零遗留库。危害处置：D1 执行器补 tx.Connection is null 守卫（prose 约定）；D2 被包端点仅收 HttpContext、action 内重读 body 反序列化（Web JSON 默认）；D3 测试补 using；D4 成功提交移至执行器（同事务 API-02）。DDL 核实：user_id 无 FK、UNIQUE(idem_key) 全局、request_hash char(64) 证 .Trim()、response_body NOT NULL 满足。携带 Task 13：JsonException（坏 JSON 原绑定 400 现抛异常）需映射；IdempotencyConflictException→409 映射；password-null NRE 保持 brief 原样；409 不记录幂等（brief 约定）。
Task 12: deferred minors → (a) plan-mandated: 并发同键竞态败者 SaveAsync 2627→裸 500（原子性成立，Task 13 AT-066 e2e 决定 catch-2627-重放语义）；(b) plan-mandated: 坏/空 JSON body JsonException→500（原框架 400，Task 13 必须映射）；(c) plan-mandated: Password 缺失 NRE（Task 13 候选）；(d) plan-mandated: 重放测试未断言 hit.Body 精确相等（补一行 Assert.Equal 可钉死不变量）；(e) ct 未转发 Dapper+执行器缺 NameIdentifier 时 NRE（接线前无实际影响）；(f) 4 新文件缺 EOF 换行；hash 不含 query string（brief 公式，POST 路由无碍）。
Carry-forward → Task 13（汇总）: JsonException→400 映射；IdempotencyConflictException→409；超长用户名登录→401+审计行测试；转义 digest 验证；admin 输入验证加固（Password null/Reason>1000/DisplayName>100）；并发同键 2627 语义裁定；hit.Body 精确重放断言。
Task 13: deferred minors → (a) plan-mandated: 空连接串 ?? throw 死代码（Task 15 部署文档注记+M1 改 IsNullOrWhiteSpace 守卫）；(b) plan-mandated: env "Mock" vs CHECK 'MOCK' 依赖 CI 排序（M1 写路径前规范化，BIN2 库会违反 CHECK）；(c) 400 响应消息落"服务内部错误"臂（语义误导，brief 原样）；(d) dashboard 测试未测拒绝侧/未种数据核计数；--worker-only 仍起 Kestrel（brief 原样）；(e) 新文件缺 EOF 换行。
Task 14: deferred minors → (a) api<T> 2xx 非 JSON body→json.data TypeError（理论路径）；(b) bootstrap 降级 csrfToken 空→登录 400 须刷新（session 恒 200 实际不可达）；(c) handleLogout 无错误处理（失败 unhandled rejection 卡壳内）；(d) AppShell health effect 无 active 守卫（React18 无害 no-op）；(e) dashboard 401 无登录跳转（M0 可接受）；(f) npm audit 4 通告（brief 锁定版本，延期升级勿 --force）；(g) 全文件缺 EOF 换行（brief 块原样）。
Task 15: deferred minors → (a) /auth/logout 继承全局 cookieAuth 未标 security:[]（代码允许匿名退出，描述自洽性瑕疵）；(b) plan-inherited: TaskActionRequest expectedVersion 可选 vs PRD §12 列为要素（M2/M3 对齐防版本检查被静默丢弃）；(c) 部分 M1+ GET 详情 200 用内联 schema 而非组件 ref（抛光）。
结论: Ready to merge? WITH FIXES。Critical 0；Important 4（I-1 bill_code 'P' 碰撞 / I-2 env 大小写接缝 / I-3 worker 零日志 / I-4 停用用户权限不失效）；Minor M-1…M-10 全部裁定 acceptable-M0 或 M1+ 携带。

## 最终全分支审查携带项（M1+ 必须显式建任务）

**M1 硬性任务：**
1. format 列加宽/映射迁移（强制）：source_line.code_format / trace_code.format nvarchar(16) 放不下 Supported8Prefix20（18 字符）。任何真实 format 值写入前必须完成持久层枚举→短 token 映射或正式加宽迁移（Task 7 裁定延续）。
2. env 常量与写路径规范化已完成（I-2）；M1 新端点须复审授权管线顺序（授权先于 CSRF 的裁定前提）。
3. 幂等 API 加固：键按 user_id 作用域（或书面确认全局键）、并发同键 2627→409 语义、hash 纳入 query string、IdempotencyExecutor 走 DI。
4. 输入验证加固：登录空用户名 NRE→400/401；admin 列宽（Username>64/DisplayName>100/Reason>1000）→422；ApiErrors 400 专属文案；空连接串 IsNullOrWhiteSpace 守卫+部署文档。
5. 通用租约回收补审计行（设计 §4.8）；GrantAsync 2601→409 映射。
6. expectedVersion 可选 vs PRD §12 要素对齐（乐观并发任务）。
7. BIN2 排序规则 fixture 变体（Sql2008Fixture opt-in）——机械拦截 env/字面量大小写类接缝；同时暴露测试中 N'BLOCKED' 等 CI 依赖字面量。
8. dev 代理端口对齐（vite 5080 vs launchSettings 5152）；前端壳抛光（api<T> 非 JSON 防御、logout 错误处理、401 跳转）。
9. 静态门禁两根目录存在性断言；卫生项（UnitTest1 占位、EOF 换行、Application 冗余 Identity.Core 包）。

**M2 硬性：** AppendStaleEvidenceAsync rows==1 守卫 + partId 归属校验（回执证据接线时非协商）；恢复扫描 org/env 作用域重审；SNAPSHOT 隔离下重复 intent 回查守卫。
**M3 硬性：** 真实发送前短事务复查租约/状态（M0 恢复不变量必要非充分）；bill_code 分片解析只依赖持久化记录，禁止按 'P' 拆分（Base35 后 body 无 P，仍守此约）。
**交付前（G-DEPLOY/用户裁定）：** 真实 SQL Server 2008 R2 全量集成复验（AT-070 + 六 G-* 门禁）；DPAPI 服务账户身份固定（CurrentUser）；audit_log 对应用登录 DENY UPDATE/DELETE；AllowedHosts 覆盖；cookie SecurePolicy.Always + HTTPS；SqlClient 5.2.2 Encrypt/TrustServerCertificate 决策。

## 最终审查 Minor 清单（M-1…M-10，均裁定 acceptable-M0 或入上表）
M-1 跨用户幂等重放；M-2 登录空用户名 NRE+列宽 500；M-3 400 文案误导；M-4 执行器绕过 DI+hash 缺 query；M-5 dev 代理端口；M-6 stale-evidence 静默 0 行+无归属校验；M-7 租约回收无审计；M-8 控制面审计 env 硬编码 LIVE（约定需文档化）；M-9 静态门禁根目录可静默空扫；M-10 卫生项（占位测试/EOF/冗余包/GO 正则理论边界/无并发迁移锁——PK 竞态 fail-safe）。

## 执行结果
15/15 任务完成；最终审查 WITH FIXES → 修复波（9a39091）+ 文档对齐（3faf92f、23a53d3）→ 限定复审 Accepted（I-1…I-4 全部独立验证，含 40 万码唯一性复算）。
最终测试：Unit 87/87，Integration 45/45（SQL 2019 授权降级，2008 R2 待交付前补验），sln Release 0 警告，tsc 0 错误。分支 m0-foundation，22 commits。
