using System.Runtime.Versioning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TraceFlow.Application.Abstractions;
using TraceFlow.Host;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Uploads;
using Xunit;

namespace TraceFlow.IntegrationTests.Jobs;

/// <summary>
/// 回归门禁（Task 4 fix 1）：<c>upload-dispatch</c> 必须真正接线进宿主。
/// Worker 只认领"已注册 IJobHandler 的 Kind"，漏注册会让该类 outbox 行永久 PENDING；
/// 这里从真实宿主容器解析处理器与发送服务来证明，而不是只看类能编译。
/// </summary>
[SupportedOSPlatform("windows")] // 宿主为 Windows-only（DPAPI/Windows 服务，ADR-0001）
public class WorkerHostedServiceTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;

    public WorkerHostedServiceTests(Sql2008Fixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        // 与 ApiGateTests 相同：UseSetting 由 WAF 注入为 CreateBuilder 命令行参数，顶层语句读取可见。
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public void Upload_dispatch_handler_and_service_resolve_from_host_container()
    {
        var handlers = _factory.Services.GetServices<IJobHandler>().ToList();
        var handler = Assert.Single(handlers, h => h.Kind == UploadDispatchService.JobKind);
        Assert.IsType<UploadDispatchJobHandler>(handler);

        // WorkerHostedService 构造函数按 Kind 建索引并将其作为 ClaimAsync 的认领集合：
        // 能从容器构造成功（Kind 无重复）且处理器集合含 upload-dispatch ⇒ 该作业不会被漏认领。
        var worker = Assert.Single(_factory.Services.GetServices<IHostedService>().OfType<WorkerHostedService>());
        Assert.NotNull(worker);
        Assert.NotNull(_factory.Services.GetRequiredService<UploadDispatchService>());
    }
}