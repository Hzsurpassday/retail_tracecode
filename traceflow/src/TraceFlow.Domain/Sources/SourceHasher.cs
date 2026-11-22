using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TraceFlow.Domain.Sources;

/// <summary>SYNC-01/02：行/单内容摘要（变更检测与稳定期两轮一致判定）。SHA256 大写 hex。</summary>
public static class SourceHasher
{
    public static string LineHash(string lineKey, string productId, decimal qty, string? unit,
        string? batch, string? expiry, string? traceCodeRaw, string eventKind)
        => Sha(string.Join("|", lineKey, productId,
            qty.ToString("0.000000", CultureInfo.InvariantCulture),
            unit ?? "", batch ?? "", expiry ?? "", traceCodeRaw ?? "", eventKind));

    public static string DocumentHash(string docKey, DateTime? businessUtc, IReadOnlyList<string> sortedLineHashes)
        => Sha(docKey + "|" + (businessUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "")
            + "|" + string.Join(";", sortedLineHashes));

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
