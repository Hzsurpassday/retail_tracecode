using System.Text.Json;

namespace TraceFlow.Domain.Top;

/// <summary>TOP-01：系统错误（error_response）与业务错误（response_success=false）分离；任何解析失败都不得当成功。</summary>
public static class TopResponseParser
{
    public static TopOutcome Parse(bool transportOk, string? errorJson, string? businessJson, int? retryAfterSeconds)
    {
        if (!transportOk)
            return new TopOutcome(TopOutcomeKind.TransportUncertain, null, null, "传输不确定，受理状态未知", null, null, retryAfterSeconds);

        if (!string.IsNullOrWhiteSpace(errorJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(errorJson);
                var r = doc.RootElement;
                var code = GetString(r, "code");
                var subCode = GetString(r, "sub_code");
                var msg = GetString(r, "sub_msg") ?? GetString(r, "msg");
                var reqId = GetString(r, "request_id");
                var token = subCode ?? code;
                var kind = ClassifyToken(token) ?? TopOutcomeKind.SystemError;
                return new TopOutcome(kind, code, subCode, msg, null, reqId, retryAfterSeconds);
            }
            catch (JsonException)
            {
                return new TopOutcome(TopOutcomeKind.SystemError, null, null, "error_response 不可解析", null, null, retryAfterSeconds);
            }
        }

        if (!string.IsNullOrWhiteSpace(businessJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(businessJson);
                var r = doc.RootElement;
                var msgCode = GetString(r, "msg_code");
                var subMsgCode = GetString(r, "sub_msg_code");
                var msg = GetString(r, "sub_msg") ?? GetString(r, "msg");
                var reqId = GetString(r, "request_id");
                string? resultJson = r.TryGetProperty("result", out var result) ? result.GetRawText() : null;

                if (msgCode == TopErrorTokens.PartialSuccess || subMsgCode == TopErrorTokens.PartialSuccess)
                    return new TopOutcome(TopOutcomeKind.PartialEvidence, msgCode, subMsgCode, msg, resultJson, reqId, retryAfterSeconds);
                if (subMsgCode == TopErrorTokens.DuplicateBillCode)
                    return new TopOutcome(TopOutcomeKind.DuplicateBillCode, msgCode, subMsgCode, msg, resultJson, reqId, retryAfterSeconds);

                var tokenKind = ClassifyToken(subMsgCode) ?? ClassifyToken(msgCode);
                if (tokenKind is TopOutcomeKind.AuthError or TopOutcomeKind.RateLimited)
                    return new TopOutcome(tokenKind.Value, msgCode, subMsgCode, msg, resultJson, reqId, retryAfterSeconds);

                var success = r.TryGetProperty("response_success", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (success)
                    return new TopOutcome(TopOutcomeKind.Success, msgCode, subMsgCode, msg, resultJson, reqId, retryAfterSeconds);
                var reason = flag.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "缺少 response_success" : msg;
                return new TopOutcome(TopOutcomeKind.BusinessRejected, msgCode, subMsgCode, reason, resultJson, reqId, retryAfterSeconds);
            }
            catch (JsonException)
            {
                return new TopOutcome(TopOutcomeKind.SystemError, null, null, "业务响应不可解析", null, null, retryAfterSeconds);
            }
        }

        return new TopOutcome(TopOutcomeKind.SystemError, null, null, "空响应", null, null, retryAfterSeconds);
    }

    private static TopOutcomeKind? ClassifyToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (token == TopErrorTokens.AuthError) return TopOutcomeKind.AuthError;
        if (token.StartsWith(TopErrorTokens.RateLimitPrefix, StringComparison.Ordinal)) return TopOutcomeKind.RateLimited;
        return null;
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
         : e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText()
         : null;
}
