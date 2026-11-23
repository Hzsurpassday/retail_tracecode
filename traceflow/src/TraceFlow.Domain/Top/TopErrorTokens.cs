namespace TraceFlow.Domain.Top;

/// <summary>
/// AuthError/PartialSuccess 为规格 §3.3 与 PRD TOP-03 点名 token（已确认）。
/// RateLimitPrefix/DuplicateBillCode 为候选值，待 G-TOP 取证后修订（docs/contracts/top-contract.md）。
/// </summary>
public static class TopErrorTokens
{
    public const string AuthError = "FAIL_BIZ_AUTH_ERROR";
    public const string PartialSuccess = "WARN_BIZ_PARTSUCCESS";
    public const string RateLimitPrefix = "FAIL_BIZ_RATE";
    public const string DuplicateBillCode = "FAIL_BIZ_BILL_CODE_DUPLICATE";
}
