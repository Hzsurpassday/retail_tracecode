namespace TraceFlow.Domain.Matching;

/// <summary>
/// MATCH-01/02：五项一致才可能自动确认；任一字段矛盾→冲突；任一字段缺失→不完整（不通配）。
/// 名称/条码/批号/前七位不在五项内，绝不单独自动绑定。
/// </summary>
public static class DrugIdentityMatcher
{
    public static IdentityMatch Evaluate(DrugIdentity local, DrugIdentity platform)
    {
        var equal = new List<string>();
        var missing = new List<string>();
        var conflicts = new List<string>();
        Compare("approvalNo", local.ApprovalNoNorm, platform.ApprovalNoNorm, equal, missing, conflicts);
        Compare("manufacturerIdentity", local.ManufacturerIdentity, platform.ManufacturerIdentity, equal, missing, conflicts);
        Compare("dosageForm", local.DosageForm, platform.DosageForm, equal, missing, conflicts);
        Compare("strength", local.Strength, platform.Strength, equal, missing, conflicts);
        Compare("packageSpec", local.PackageSpec, platform.PackageSpec, equal, missing, conflicts);
        return new IdentityMatch(equal, missing, conflicts);
    }

    private static void Compare(string field, string? a, string? b,
        List<string> equal, List<string> missing, List<string> conflicts)
    {
        var x = a?.Trim();
        var y = b?.Trim();
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) missing.Add(field);
        else if (string.Equals(x, y, StringComparison.Ordinal)) equal.Add(field);
        else conflicts.Add(field);
    }
}
