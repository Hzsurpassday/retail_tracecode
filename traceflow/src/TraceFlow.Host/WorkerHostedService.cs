using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host;

public sealed class WorkerHostedService : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly ISendRecoveryService _recovery;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;
    private readonly ILogger<WorkerHostedService> _logger;
    private readonly string _env;
    private readonly string _workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private DateTime _lastRecovery = DateTime.MinValue;

    public WorkerHostedService(IJobQueue queue, ISendRecoveryService recovery, IEnumerable<IJobHandler> handlers, EnvOptions envOptions, ILogger<WorkerHostedService> logger)
    {
        _queue = queue; _recovery = recovery; _env = envOptions.Env; _logger = logger;
        _handlers = handlers.ToDictionary(h => h.Kind);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _lastRecovery > TimeSpan.FromSeconds(30))
                {
                    await _queue.RecoverExpiredLeasesAsync(stoppingToken);
                    // 失租发送→受理未知→只回查（JOB-02）；只恢复本机环境的分片（MOCK worker 不动 LIVE）。
                    await _recovery.RecoverAsync(_env, stoppingToken);
                    _lastRecovery = DateTime.UtcNow;
                    _logger.LogDebug("Worker 恢复扫描完成（租约回收 + 发送恢复回查）");
                }
                if (_handlers.Count > 0)
                {
                    var job = await _queue.ClaimAsync(_workerId, _env, _handlers.Keys.ToList(), TimeSpan.FromMinutes(5), stoppingToken);
                    if (job is not null && _handlers.TryGetValue(job.JobKind, out var handler))
                    {
                        try
                        {
                            await handler.HandleAsync(job, stoppingToken);
                            await _queue.CompleteAsync(job.Id, _workerId, job.FencingToken, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            var delay = ex is TopRateLimitedException rle
                                ? rle.Wait
                                : TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(job.AttemptCount, 5))));
                            await _queue.RescheduleAsync(job.Id, _workerId, job.FencingToken, DateTime.UtcNow + delay, ex.Message, stoppingToken);
                        }
                        continue; // 立即尝试下一个
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 数据库瞬断：保留任务，下轮重试（JOB-05）；不再静默吞异常（I-3）
                _logger.LogWarning(ex, "Worker 循环异常，保留任务下轮重试");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
