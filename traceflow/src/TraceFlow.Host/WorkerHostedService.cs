using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host;

public sealed class WorkerHostedService : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly ISendRecoveryService _recovery;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;
    private readonly string _env;
    private readonly string _workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private DateTime _lastRecovery = DateTime.MinValue;

    public WorkerHostedService(IJobQueue queue, ISendRecoveryService recovery, IEnumerable<IJobHandler> handlers, EnvOptions envOptions)
    {
        _queue = queue; _recovery = recovery; _env = envOptions.Env;
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
                    await _recovery.RecoverAsync(stoppingToken); // 失租发送→受理未知→只回查（JOB-02）
                    _lastRecovery = DateTime.UtcNow;
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
                            // 有限退避（JOB-03/04 参数化在 M2/M3 细化）；不 tight loop
                            var delay = TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(job.AttemptCount, 5))));
                            await _queue.RescheduleAsync(job.Id, _workerId, job.FencingToken, DateTime.UtcNow + delay, ex.Message, stoppingToken);
                        }
                        continue; // 立即尝试下一个
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 数据库瞬断：保留任务，下轮重试（JOB-05） */ }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}