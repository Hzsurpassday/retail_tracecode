using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.Host;

/// <summary>SYNC-04：30 秒调度循环。先 Delay 后 Tick——短命测试宿主不产生后台作业；生产节奏不变。</summary>
public sealed class SyncSchedulerHostedService : BackgroundService
{
    private readonly SyncScheduler _scheduler;
    private readonly ILogger<SyncSchedulerHostedService> _logger;

    public SyncSchedulerHostedService(SyncScheduler scheduler, ILogger<SyncSchedulerHostedService> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                await _scheduler.TickAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "同步调度 Tick 异常，30 秒后重试");
            }
        }
    }
}
