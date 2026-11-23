using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TraceFlow.Infrastructure.Uploads;

/// <summary>
/// 平台原始响应脱敏（§3.2 / IDEM-04）：<c>upload_attempt.raw_response</c> 落库前必须去除 AppSecret、
/// 完整签名与任何会话令牌。规则：
/// 1) JSON 结构：键名（按 <c>_</c>/<c>-</c> 分段或整体）命中敏感词 → 值整体替换为 <c>***</c>，递归处理数组/对象；
/// 2) 非 JSON 文本：按 <c>key=value</c> / <c>key:value</c> 形态对敏感键打码，其余保留（便于排障）；
/// 3) 无法解析的超长文本先截断再脱敏，绝不因解析失败而跳过脱敏。
/// </summary>
public static class TopResponseSanitizer
{
    public const string Mask = "***";
    private const int MaxRawLength = 8000;

    /// <summary>敏感键词：整体相等或分段相等即命中（避免 assign/design 之类误伤）。</summary>
    private static readonly HashSet<string> SensitiveTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "secret", "appsecret", "appkeysecret", "sign", "signature", "sig",
        "password", "passwd", "pwd", "token", "accesstoken", "sessionkey", "credential",
    };

    private static readonly Regex SensitivePair = new(
        @"(?<key>app[_\-]?secret|appsecret|access[_\-]?token|session[_\-]?key|sign|signature|password|passwd|pwd|token)\s*(?<sep>[=:])\s*(?<value>[^&\s,;""'}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var text = raw.Length > MaxRawLength ? raw.Substring(0, MaxRawLength) : raw;
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(trimmed);
                SanitizeNode(node);
                return node?.ToJsonString() ?? trimmed;
            }
            catch (JsonException)
            {
                // 非法 JSON：退回文本脱敏，绝不放行原文。
            }
        }
        return SensitivePair.Replace(text, m => m.Groups["key"].Value + m.Groups["sep"].Value + Mask);
    }

    private static void SanitizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsSensitiveKey(key)) obj[key] = Mask;
                    else SanitizeNode(obj[key]);
                }
                break;
            case JsonArray array:
                foreach (var item in array) SanitizeNode(item);
                break;
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("_", "").Replace("-", "");
        if (SensitiveTokens.Contains(normalized)) return true;
        var segments = key.Split('_', '-');
        foreach (var segment in segments)
            if (segment.Length > 0 && SensitiveTokens.Contains(segment)) return true;
        return false;
    }
}