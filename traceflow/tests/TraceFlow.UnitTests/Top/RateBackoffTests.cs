using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.UnitTests.Top;

public class RateBackoffTests
{
    [Theory]
    [InlineData(0, 0, 30)]
    [InlineData(1, 0, 60)]
    [InlineData(4, 0, 480)]
    [InlineData(5, 0, 600)]   // 30×2^5=960 → 上限 600
    [InlineData(9, 5, 605)]
    public void Backoff_formula(int attempt, int jitter, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RateBudgetService.Backoff(attempt, jitter));

    [Fact]
    public void Backoff_jitter_within_0_to_5_seconds()
    {
        for (var i = 0; i < 50; i++)
        {
            var t = RateBudgetService.Backoff(0);
            Assert.InRange(t.TotalSeconds, 30, 35);
        }
    }
}
