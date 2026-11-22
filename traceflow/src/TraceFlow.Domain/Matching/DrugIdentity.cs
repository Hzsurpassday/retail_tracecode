namespace TraceFlow.Domain.Matching;

/// <summary>MATCH-01 五项身份。全部可空：缺字段≠通配（AT-014）。</summary>
public sealed record DrugIdentity(string? ApprovalNoNorm, string? ManufacturerIdentity,
    string? DosageForm, string? Strength, string? PackageSpec);

public sealed record IdentityMatch(IReadOnlyList<string> Equal, IReadOnlyList<string> Missing, IReadOnlyList<string> Conflicts)
{
    public bool IsFullMatch => Equal.Count == 5;
    public bool HasConflict => Conflicts.Count > 0;
    public bool HasOverlap => Equal.Count > 0;
}
