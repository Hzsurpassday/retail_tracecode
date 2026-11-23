using System.Text.Json;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Top;

/// <summary>文件 fixture：{method}.json = [{"match":{参数子集},"transportOk":bool?,"retryAfterSeconds":int?,"response":{平台信封}}]。候选信封形状，待 G-TOP。</summary>
public sealed class TopFixtureHandler
{
    private readonly string _dir;
    public TopFixtureHandler(string dir) => _dir = dir;

    public TopRawResponse Handle(TopCall call)
    {
        var path = Path.Combine(_dir, call.Method + ".json");
        if (!File.Exists(path))
            return new TopRawResponse(true, 200, null, """{"response_success":true,"result":{}}""", null);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("match", out var match))
            {
                var ok = true;
                foreach (var p in match.EnumerateObject())
                    if (!call.Parameters.TryGetValue(p.Name, out var v) || !string.Equals(v, p.Value.GetString(), StringComparison.Ordinal))
                    { ok = false; break; }
                if (!ok) continue;
            }
            var transportOk = !entry.TryGetProperty("transportOk", out var t) || t.GetBoolean();
            int? retry = entry.TryGetProperty("retryAfterSeconds", out var r) ? r.GetInt32() : null;
            string? errorJson = null, businessJson = null;
            if (entry.TryGetProperty("response", out var resp))
            {
                if (resp.TryGetProperty("error_response", out var err))
                    errorJson = err.GetRawText();
                else
                    foreach (var p in resp.EnumerateObject())
                        if (p.Name.EndsWith("_response", StringComparison.Ordinal))
                        { businessJson = p.Value.GetRawText(); break; }
            }
            return new TopRawResponse(transportOk, transportOk ? 200 : null, errorJson, businessJson, retry);
        }
        return new TopRawResponse(true, 200, null, """{"response_success":true,"result":{}}""", null);
    }
}
