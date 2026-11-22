using TraceFlow.Domain.Matching;

namespace TraceFlow.UnitTests.Matching;

public class DrugIdentityMatcherTests
{
    private static readonly DrugIdentity Full =
        new("国药准字H20051234", "ENT-MFR-001", "片剂", "0.25g", "12片/盒");

    [Fact]
    public void All_five_equal_is_full_match() // AT-012 单元侧
    {
        var m = DrugIdentityMatcher.Evaluate(Full, Full with { });
        Assert.True(m.IsFullMatch);
        Assert.False(m.HasConflict);
        Assert.Equal(5, m.Equal.Count);
    }

    [Fact]
    public void Same_name_different_package_conflicts_not_full() // AT-013：名称不在五项内，包装矛盾→冲突字段
    {
        var m = DrugIdentityMatcher.Evaluate(Full, Full with { PackageSpec = "24片/盒" });
        Assert.False(m.IsFullMatch);
        Assert.True(m.HasConflict);
        Assert.Contains("packageSpec", m.Conflicts);
        Assert.Equal(4, m.Equal.Count);
    }

    [Fact]
    public void Missing_field_never_wildcards() // AT-014
    {
        var m = DrugIdentityMatcher.Evaluate(Full with { ManufacturerIdentity = null }, Full);
        Assert.False(m.IsFullMatch);
        Assert.False(m.HasConflict);
        Assert.Contains("manufacturerIdentity", m.Missing);
        Assert.Equal(4, m.Equal.Count);
    }

    [Fact]
    public void Empty_string_counts_as_missing_not_equal()
    {
        var m = DrugIdentityMatcher.Evaluate(Full with { Strength = "  " }, Full);
        Assert.Contains("strength", m.Missing);
        Assert.False(m.IsFullMatch);
    }

    [Fact]
    public void Zero_overlap_has_no_equal_no_conflict()
    {
        var m = DrugIdentityMatcher.Evaluate(
            new DrugIdentity(null, null, null, null, null),
            new DrugIdentity("X", "Y", "Z", "W", "V"));
        Assert.False(m.HasOverlap);
        Assert.Equal(5, m.Missing.Count);
    }

    [Fact]
    public void Trim_before_compare()
    {
        var m = DrugIdentityMatcher.Evaluate(Full with { DosageForm = " 片剂 " }, Full);
        Assert.True(m.IsFullMatch);
    }
}
