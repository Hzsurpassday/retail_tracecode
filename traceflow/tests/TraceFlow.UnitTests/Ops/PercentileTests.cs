using TraceFlow.Application.Ops;

namespace TraceFlow.UnitTests.Ops;

/// <summary>
/// NFR-03 性能门禁的百分位口径：nearest-rank（sorted[ceil(p/100*n)-1]），不做插值；
/// 确定性用例覆盖 n=1/2/20/100，空样本与非法百分位显式失败。
/// </summary>
public class PercentileTests
{
    [Fact]
    public void P95_single_sample_returns_that_sample()
    {
        Assert.Equal(42d, Percentile.P95(new[] { 42d }));
    }

    [Fact]
    public void P95_two_samples_uses_ceiling_rank()
    {
        // ceil(0.95*2)=2 → 第二小（最大值），验证 nearest-rank 而非插值（插值会是 1.95）。
        Assert.Equal(2d, Percentile.P95(new[] { 1d, 2d }));
    }

    [Fact]
    public void P95_twenty_samples_takes_nineteenth_smallest()
    {
        var values = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        Assert.Equal(19d, Percentile.P95(values));
    }

    [Fact]
    public void P95_hundred_samples_takes_ninety_fifth_smallest()
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Assert.Equal(95d, Percentile.P95(values));
    }

    [Fact]
    public void P95_is_input_order_independent()
    {
        var shuffled = new[]
        {
            20d, 3d, 15d, 1d, 19d, 7d, 12d, 5d, 18d, 2d, 14d, 9d, 17d, 4d, 11d, 6d, 16d, 8d, 13d, 10d,
        };
        Assert.Equal(Percentile.P95(Enumerable.Range(1, 20).Select(i => (double)i).ToArray()),
            Percentile.P95(shuffled));
    }

    [Fact]
    public void P50_equals_median_lower_for_even_count()
    {
        // n=4：ceil(0.5*4)=2 → 第二小。
        Assert.Equal(2d, Percentile.P50(new[] { 4d, 1d, 3d, 2d }));
    }

    [Fact]
    public void NearestRank_p100_returns_max()
    {
        Assert.Equal(9d, Percentile.NearestRank(new[] { 9d, 1d, 5d }, 100d));
    }

    [Fact]
    public void Empty_sample_throws()
    {
        Assert.Throws<ArgumentException>(() => Percentile.P95(Array.Empty<double>()));
    }

    [Fact]
    public void Out_of_range_percentile_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Percentile.NearestRank(new[] { 1d }, 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => Percentile.NearestRank(new[] { 1d }, 101d));
    }

    [Fact]
    public void Null_sample_throws()
    {
        Assert.Throws<ArgumentNullException>(() => Percentile.P95(null!));
    }
}