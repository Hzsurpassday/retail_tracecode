namespace TraceFlow.Domain.Matching;

/// <summary>MATCH-04 七态（0003 CK_pmap_state/CK_smap_state 逐字一致）。</summary>
public static class MappingStates
{
    public const string Unresolved = "UNRESOLVED";
    public const string Candidate = "CANDIDATE";
    public const string AutoConfirmed = "AUTO_CONFIRMED";
    public const string ManualConfirmed = "MANUAL_CONFIRMED";
    public const string Conflict = "CONFLICT";
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string Disabled = "DISABLED";

    public static readonly IReadOnlyList<string> Active = new[] { AutoConfirmed, ManualConfirmed };
}
