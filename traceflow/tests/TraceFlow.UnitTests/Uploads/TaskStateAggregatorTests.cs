using TraceFlow.Domain.Uploads;
using Xunit;

namespace TraceFlow.UnitTests.Uploads;

public class TaskStateAggregatorTests
{
    [Theory]
    [InlineData(new[] { UploadState.Succeeded, UploadState.Succeeded }, UploadState.Succeeded)]
    [InlineData(new[] { UploadState.Succeeded, UploadState.Processing }, UploadState.Processing)]
    [InlineData(new[] { UploadState.Succeeded, UploadState.PartialSuccess }, UploadState.PartialSuccess)]
    [InlineData(new[] { UploadState.AcceptanceUnknown, UploadState.Failed }, UploadState.AcceptanceUnknown)]
    [InlineData(new[] { UploadState.Failed, UploadState.Failed }, UploadState.Failed)]
    [InlineData(new[] { UploadState.Dispatching, UploadState.Ready }, UploadState.Dispatching)]
    public void Aggregates_parts_with_strict_severity(UploadState[] parts, UploadState expected)
        => Assert.Equal(expected, TaskStateAggregator.Aggregate(parts));

    // 以下为 brief 6 个用例之外的补充覆盖：ManualAction 覆盖规则 / Cancelled 忽略 / 待发送分片不落终态 / 空集合。

    [Theory]
    [InlineData(new[] { UploadState.ManualAction, UploadState.Failed }, UploadState.ManualAction)]
    [InlineData(new[] { UploadState.ManualAction, UploadState.Dispatching }, UploadState.ManualAction)]
    [InlineData(new[] { UploadState.ManualAction, UploadState.Succeeded }, UploadState.ManualAction)]
    [InlineData(new[] { UploadState.ManualAction, UploadState.AcceptanceUnknown }, UploadState.AcceptanceUnknown)]
    public void Manual_action_overrides_all_but_acceptance_unknown(UploadState[] parts, UploadState expected)
        => Assert.Equal(expected, TaskStateAggregator.Aggregate(parts));

    [Theory]
    [InlineData(new[] { UploadState.Succeeded, UploadState.Cancelled }, UploadState.Succeeded)]
    [InlineData(new[] { UploadState.Cancelled, UploadState.Failed, UploadState.Cancelled }, UploadState.Failed)]
    [InlineData(new[] { UploadState.Ready, UploadState.Cancelled }, UploadState.Ready)]
    [InlineData(new[] { UploadState.Cancelled, UploadState.Cancelled }, UploadState.Cancelled)]
    public void Cancelled_parts_are_ignored_unless_all_cancelled(UploadState[] parts, UploadState expected)
        => Assert.Equal(expected, TaskStateAggregator.Aggregate(parts));

    [Theory]
    [InlineData(new[] { UploadState.Succeeded, UploadState.Ready }, UploadState.Ready)]
    [InlineData(new[] { UploadState.Accepted, UploadState.RetryWait }, UploadState.RetryWait)]
    public void Pending_parts_outrank_completed_parts(UploadState[] parts, UploadState expected)
        => Assert.Equal(expected, TaskStateAggregator.Aggregate(parts));

    [Fact]
    public void Empty_collection_throws_argument_exception()
    {
        Assert.Throws<ArgumentException>(() => TaskStateAggregator.Aggregate(Array.Empty<UploadState>()));
        Assert.Throws<ArgumentNullException>(() => TaskStateAggregator.Aggregate(null!));
    }

    [Fact]
    public void Every_state_except_cancelled_is_ranked()
    {
        // 严重度表必须覆盖除 Cancelled 外的全部枚举值（新增状态时此处失败，避免静默落到"未排名"）。
        foreach (var state in Enum.GetValues<UploadState>())
        {
            if (state == UploadState.Cancelled) continue;
            Assert.Equal(state, TaskStateAggregator.Aggregate(new[] { state }));
        }
    }
}