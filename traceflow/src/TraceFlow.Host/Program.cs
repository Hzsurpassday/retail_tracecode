using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using TraceFlow.Application.Abstractions;
using TraceFlow.Host;
using TraceFlow.Host.Endpoints;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 104_857_600); // 100 MiB，与 import.maxFileBytes 上界一致（R11e）

if (args.Contains("--init-admin"))
    return await AdminInitializer.RunAsync(args, builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("TraceFlow");
if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("缺少 ConnectionStrings:TraceFlow（部署配置提供，不入 Git）");
// 配置大小写不敏感，启动规范化为大写 LIVE/MOCK（非法值启动失败）；安全默认 MOCK，LIVE 须门禁后显式开启
var env = (builder.Configuration["TraceFlow:Env"] ?? "Mock").ToUpperInvariant();
if (env is not ("MOCK" or "LIVE"))
    throw new InvalidOperationException("TraceFlow:Env 必须为 MOCK 或 LIVE（大小写不敏感）");
var workerOnly = args.Contains("--worker-only");
var webOnly = args.Contains("--web-only");

builder.Services.AddSingleton(new DbOptions(connectionString));
builder.Services.AddSingleton(new EnvOptions(env));
builder.Services.AddSingleton<IJobQueue>(sp => new JobQueue(connectionString, sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<ISendRecoveryService>(sp => new SendRecoveryService(connectionString, sp.GetRequiredService<IJobQueue>()));
builder.Services.AddSingleton<IPermissionService>(new PermissionService(connectionString));
builder.Services.AddSingleton<IAuditLog, AuditLog>();
builder.Services.AddSingleton<ISecretProtector, DpapiProtector>();
builder.Services.AddSingleton<IIdempotencyStore, IdempotencyStore>();
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Settings.SettingsService(
    connectionString, sp.GetRequiredService<ISecretProtector>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Master.ImportService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(),
    sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.ImportStageJobHandler(connectionString));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.MasterMatchJobHandler(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), sp.GetRequiredService<IAuditLog>()));
var sourceMode = builder.Configuration["TraceFlow:SourceMode"] ?? "fixture";
builder.Services.AddSingleton<ISourceAdapter>(TraceFlow.Infrastructure.Sources.SourceAdapterFactory.Create(
    sourceMode, Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa")));
var topMode = builder.Configuration["TraceFlow:TopMode"] ?? "fixture";
builder.Services.AddSingleton<ITopGateway>(TraceFlow.Infrastructure.Top.TopGatewayFactory.Create(
    topMode, Path.Combine(AppContext.BaseDirectory, "fixtures", "top")));
builder.Services.AddSingleton<TraceFlow.Infrastructure.Top.RateBudgetService>();
builder.Services.AddSingleton<TraceFlow.Infrastructure.Top.ChannelPauseService>();
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Top.TopClientService(
    connectionString, sp.GetRequiredService<ITopGateway>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Top.RateBudgetService>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Top.ChannelPauseService>(),
    sp.GetRequiredService<IAuditLog>()));
// 发送协议 upload-dispatch（IDEM-04 / §3.2）：未注册则 Worker 不会认领该类作业，outbox 行会永久 PENDING。
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Uploads.UploadDispatchService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Top.TopClientService>(),
    sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<ISendRecoveryService>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Sales.RetailTaskFactory(
    sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), sp.GetRequiredService<IJobQueue>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Sales.SaleVerificationService(
    connectionString, env, sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Top.TopClientService>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Sales.RetailTaskFactory>(),
    sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<IAuditLog>()));
// 退货首次人工确认（RET-02/DEF-02）：端点直接调用（服务内部自带 rc: 命名空间幂等，不能叠加 HTTP IdempotencyExecutor 事务）。
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Sales.ReturnConfirmService(
    connectionString, env, sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Sales.RetailTaskFactory>(),
    sp.GetRequiredService<IIdempotencyStore>(), sp.GetRequiredService<IAuditLog>()));
// 上传中心人工核对/受控重试（UI-09/IDEM-05）：在端点幂等事务内推进状态机并复位发送作业。
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Uploads.UploadManualService(
    sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(),
    sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.SaleVerifyJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Sales.SaleVerificationService>(),
    sp.GetRequiredService<IJobQueue>(), connectionString));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.UploadDispatchJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Uploads.UploadDispatchService>(),
    sp.GetRequiredService<IJobQueue>(), connectionString));
// billstatus 回查（TOP-02..04、JOB-04）：未注册则 query:part/query:scan 作业永久 PENDING，受理结论永远悬空。
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Uploads.BillQueryService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Top.TopClientService>(),
    sp.GetRequiredService<IJobQueue>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Sales.SaleVerificationService>(),
    sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.BillQueryJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Uploads.BillQueryService>(), connectionString));
// 日对账（REC-01/02/03）：服务 + 作业处理器 + 调度器。未注册处理器则 recon:run 作业会永久 PENDING。
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Reconciliation.ReconciliationService(
    connectionString, sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Reconciliation.ReconciliationJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Reconciliation.ReconciliationService>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Reconciliation.ReconciliationScheduler(
    connectionString, sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Upstream.UpstreamDownloadService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Top.TopClientService>(),
    sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.UpstreamDownloadJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Upstream.UpstreamDownloadService>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Upstream.EnterpriseSyncService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Top.TopClientService>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.EnterpriseSyncJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Upstream.EnterpriseSyncService>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Inbound.InboundTaskFactory(
    sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), sp.GetRequiredService<IJobQueue>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Inbound.InboundMatchService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Inbound.InboundTaskFactory>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.InboundMatchJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Inbound.InboundMatchService>()));
builder.Services.AddSingleton<TraceFlow.Infrastructure.Inbound.AllocationService>(sp =>
    new TraceFlow.Infrastructure.Inbound.AllocationService(sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Sources.SyncScheduler(
    connectionString, sp.GetRequiredService<IJobQueue>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), env));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Sources.SourceSyncJobHandler(
    connectionString, sp.GetRequiredService<ISourceAdapter>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), sp.GetRequiredService<IAuditLog>(),
    sp.GetRequiredService<IJobQueue>()));
builder.Services.AddSingleton(sp => new IdempotencyExecutor(connectionString, sp.GetRequiredService<IIdempotencyStore>()));
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

if (!workerOnly)
{
    builder.Services.AddAuthentication("tf").AddCookie("tf", o =>
    {
        o.Cookie.Name = ".tf.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // G-DEPLOY 上线时强制 HTTPS（Always）
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
    builder.Services.AddAuthorization(o =>
    {
        foreach (var p in TraceFlow.Application.Security.PermissionCatalog.All)
            o.AddPolicy("perm:" + p, pb => pb.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(p)));
    });
    builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
}
if (!webOnly)
{
    builder.Services.AddHostedService<WorkerHostedService>();
    builder.Services.AddHostedService<SyncSchedulerHostedService>();
}

var app = builder.Build();
await TraceFlow.Infrastructure.Bootstrap.OrgBootstrap.EnsureAsync(connectionString);
app.UseExceptionHandler(errApp => errApp.Run(ApiErrors.HandleAsync));

if (!workerOnly)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    // 全局 CSRF 防护（AUTH-02）：已认证请求的所有 /api 变更方法必须带有效 antiforgery 令牌；
    // 匿名请求先由端点鉴权返回 401；登录/退出端点内部自行 ValidateRequestAsync（防登录 CSRF）。
    app.Use(async (ctx, next) =>
    {
        var m = ctx.Request.Method;
        if (ctx.User.Identity?.IsAuthenticated == true &&
            ctx.Request.Path.StartsWithSegments("/api") && (m == "POST" || m == "PUT" || m == "DELETE" || m == "PATCH"))
            await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
        await next();
    });
    app.MapAuthEndpoints(connectionString);
    app.MapAdminEndpoints(connectionString);
    app.MapSettingsEndpoints(connectionString, env);
    app.MapImportEndpoints(connectionString, env);
    app.MapMasterEndpoints(connectionString);
    app.MapOpsEndpoints(connectionString, env);
    app.MapOpsStatusEndpoints(connectionString, env);
    app.MapInboundEndpoints(connectionString, env);
    app.MapSalesEndpoints(connectionString, env);
    app.MapCodesEndpoints(connectionString, env);
    app.MapUploadEndpoints(connectionString, env);
    app.MapUpstreamEndpoints(connectionString, env);
    app.MapReconciliationEndpoints(connectionString, env);
    app.MapExportEndpoints(connectionString, env);
    app.MapExceptionsEndpoints(connectionString, env);
    app.MapDashboardEndpoints(connectionString, env);
    app.MapHealthEndpoints(connectionString, env);
    app.MapFallbackToFile("index.html");
}
app.Run();
return 0;

// 产品部署目标为 Windows 服务（ADR-0001）：顶层语句装配 DpapiProtector（Windows-only），
// 沿用 Task 11 的 [SupportedOSPlatform] 调用方标注模式消除 CA1416（TreatWarningsAsErrors）。
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class Program { }

public sealed record DbOptions(string ConnectionString);
public sealed record EnvOptions(string Env);
