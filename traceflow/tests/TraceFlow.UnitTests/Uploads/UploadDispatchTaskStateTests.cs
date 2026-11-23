using TraceFlow.Domain.Uploads;
using TraceFlow.Infrastructure.Uploads;
using Xunit;

namespace TraceFlow.UnitTests.Uploads;

/// <summary>
/// 发送服务的任务级结算规则（<see cref="UploadDispatchService.AggregateTaskState"/>）：
/// 全部可结算时按 <see cref="TaskStateAggregator"/> 严密度聚合；存在不可结算分片时先归一再聚合，
/// 保证任务既不会提前落定（Blocked/Ready 被当作"已完成"），也不会因跳过写入滞留 Dispatching。
/// </summary>
public class UploadDispatchTaskStateTests
{
    [Theory]
    // 全部可结算：原样聚合（Succeeded 不会被降级为 Accepted）。
    [InlineData(new[] { UploadState.Succeeded, UploadState.Succeeded }, UploadState.Succeeded)]
    [InlineData(new[] { UploadState.Succeeded, UploadState.Failed }, UploadState.Failed)]
    [InlineData(new[] { UploadState.ManualAction, UploadState.Succeeded }, UploadState.ManualAction)]
    [InlineData(new[] { UploadState.AcceptanceUnknown, UploadState.Failed }, UploadState.AcceptanceUnknown)]
    // 存在不可结算分片：Succeeded → Accepted，Ready/RetryWait/Blocked → Dispatching。
    [InlineData(new[] { UploadState.Succeeded, UploadState.Blocked }, UploadState.Dispatching)]
    [InlineData(new[] { UploadState.Succeeded, UploadState.Ready }, UploadState.Dispatching)]
    [InlineData(new[] { UploadState.Succeeded, UploadState.RetryWait }, UploadState.Dispatching)]
    [InlineData(new[] { UploadState.Succeeded, UploadState.Accepted }, UploadState.Accepted)]
    // 更坏结论不得被待发送分片掩盖。
    [InlineData(new[] { UploadState.AcceptanceUnknown, UploadState.Blocked }, UploadState.AcceptanceUnknown)]
    [InlineData(new[] { UploadState.AcceptanceUnknown, UploadState.Ready }, UploadState.AcceptanceUnknown)]
    // Cancelled 分片不参与；全 Cancelled → 任务 Cancelled。
    [InlineData(new[] { UploadState.Cancelled, UploadState.Succeeded }, UploadState.Succeeded)]
    [InlineData(new[] { UploadState.Succeeded, UploadState.Blocked, UploadState.Cancelled }, UploadState.Dispatching)]
    [InlineData(new[] { UploadState.Cancelled, UploadState.Cancelled }, UploadState.Cancelled)]
    public void Aggregates_task_state_with_settle_mapping(UploadState[] parts, UploadState expected)
        => Assert.Equal(expected, UploadDispatchService.AggregateTaskState(parts));

    [Fact]
    public void Empty_collection_throws_argument_exception()
    {
        Assert.Throws<ArgumentException>(() => UploadDispatchService.AggregateTaskState(Array.Empty<UploadState>()));
        Assert.Throws<ArgumentNullException>(() => UploadDispatchService.AggregateTaskState(null!));
    }
}