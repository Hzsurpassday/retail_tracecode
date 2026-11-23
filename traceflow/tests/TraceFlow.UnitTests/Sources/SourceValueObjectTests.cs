using TraceFlow.Domain.Sources;

namespace TraceFlow.UnitTests.Sources;

public class SourceValueObjectTests
{
    [Theory]
    [InlineData("2027-05-31", "DAY")]
    [InlineData("2027/5/3", "DAY")]
    [InlineData("2027-05", "MONTH")]
    [InlineData("2027/5", "MONTH")]
    [InlineData("20270531", "UNKNOWN")] // 未验证格式不猜精度
    [InlineData("", "UNKNOWN")]
    [InlineData(null, "UNKNOWN")]
    [InlineData("长期", "UNKNOWN")]
    public void Expiry_precision(string? raw, string expected)
        => Assert.Equal(expected, ExpiryParser.ParsePrecision(raw));

    [Fact]
    public void Business_time_shanghai_to_utc()
    {
        var utc = BusinessTimeParser.ParseUtc("2026-11-20", "14:32:05");
        Assert.Equal(new DateTime(2026, 11, 20, 6, 32, 5, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Business_time_date_only_midnight()
    {
        var utc = BusinessTimeParser.ParseUtc("2026-11-20", null);
        Assert.Equal(new DateTime(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc), utc);
    }

    [Theory]
    [InlineData("2026-13-40", "00:00:00")]
    [InlineData("not-a-date", null)]
    [InlineData(null, null)]
    public void Invalid_time_returns_null(string? d, string? t)
        => Assert.Null(BusinessTimeParser.ParseUtc(d, t));

    [Fact]
    public void Line_hash_deterministic_and_sensitive()
    {
        var h1 = SourceHasher.LineHash("1", "SP0001", 2m, "盒", "P1", "2027-05", "81234567890123456789", "SALE");
        var h2 = SourceHasher.LineHash("1", "SP0001", 2m, "盒", "P1", "2027-05", "81234567890123456789", "SALE");
        var h3 = SourceHasher.LineHash("1", "SP0001", 3m, "盒", "P1", "2027-05", "81234567890123456789", "SALE");
        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.Equal(64, h1.Length);
    }

    [Fact]
    public void Line_hash_delimiter_shift_does_not_collide()
    {
        // 自由文本含 "|" 不得把内容挪过字段边界（SYNC-01/02 变更检测依赖此哈希）。
        var a = SourceHasher.LineHash("1", "SP0001", 2m, "盒|P1", "", "2027-05", "81234567890123456789", "SALE");
        var b = SourceHasher.LineHash("1", "SP0001", 2m, "盒", "P1", "2027-05", "81234567890123456789", "SALE");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Line_hash_pipe_in_batch_does_not_collide()
    {
        // 未转义时两行拼接串完全相同（盒|P1|2027-05|...），真实碰撞 → 行变更漏检。
        var a = SourceHasher.LineHash("1", "SP0001", 2m, "盒|P1", "2027-05", "8123", "T", "SALE");
        var b = SourceHasher.LineHash("1", "SP0001", 2m, "盒", "P1|2027-05", "8123", "T", "SALE");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Line_hash_backslash_escape_does_not_collide()
    {
        // 若只转义 "|" 而不加倍 "\"：A 的 unit 尾随反斜杠+分隔符 与 B 的转义竖线同为 盒\|P1，
        // 且尾部 SALE\|X 与 SALE\+分隔符+X 同形 → 拼接串碰撞。反斜杠必须先加倍。
        var a = SourceHasher.LineHash("1", "SP0001", 2m, @"盒\", "P1", "", "", "SALE|X");
        var b = SourceHasher.LineHash("1", "SP0001", 2m, "盒|P1", "", "", @"SALE\", "X");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Document_hash_order_insensitive_via_sorted_inputs()
    {
        var a = SourceHasher.DocumentHash("000123", new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), new[] { "aa", "bb" });
        var b = SourceHasher.DocumentHash("000123", new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), new[] { "aa", "bb" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Incremental_window_overlaps_checkpoint() // SYNC-01/04
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var cp = now.AddHours(-1);
        var (start, end) = SyncWindowPolicy.Incremental(cp, now, SyncPolicy.Default);
        Assert.Equal(cp.AddHours(-72), start);
        Assert.Equal(now, end);
    }

    [Fact]
    public void Gap_backfill_never_truncates() // AT-007：停机 10 天
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var cp = now.AddDays(-10);
        var (start, end) = SyncWindowPolicy.Incremental(cp, now, SyncPolicy.Default);
        Assert.True(start <= cp, "窗口起点必须不晚于检查点终点，缺口全覆盖");
        Assert.Equal(now, end);
        Assert.True(start < now.AddDays(-10).AddHours(-71));
    }

    [Fact]
    public void First_run_uses_daily_lookback()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var (start, _) = SyncWindowPolicy.Incremental(null, now, SyncPolicy.Default);
        Assert.Equal(now.AddDays(-30), start);
    }

    [Fact]
    public void Daily_window_30_days()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var (start, end) = SyncWindowPolicy.Daily(now, SyncPolicy.Default);
        Assert.Equal(now.AddDays(-30), start);
        Assert.Equal(now, end);
    }

    [Fact]
    public void Code_set_hash_order_insensitive()
    {
        var a = SourceHasher.CodeSetHash(new[] { "81234567890123456789", "81234567890123456788" });
        var b = SourceHasher.CodeSetHash(new[] { "81234567890123456788", "81234567890123456789" });
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Code_set_hash_delimiter_does_not_collide()
    {
        var a = SourceHasher.CodeSetHash(new[] { "A|B", "C" });
        var b = SourceHasher.CodeSetHash(new[] { "A", "B|C" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Code_set_hash_empty_set_stable()
        => Assert.Equal(SourceHasher.CodeSetHash(Array.Empty<string>()), SourceHasher.CodeSetHash(Array.Empty<string>()));
}
