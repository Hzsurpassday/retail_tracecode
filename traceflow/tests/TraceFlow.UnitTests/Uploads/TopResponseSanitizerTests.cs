using System.Text.Json;
using TraceFlow.Infrastructure.Uploads;
using Xunit;

namespace TraceFlow.UnitTests.Uploads;

/// <summary>raw_response 脱敏（IDEM-04）：AppSecret / 完整签名 / 会话令牌不得入库；非敏感证据保留以便排障。</summary>
public class TopResponseSanitizerTests
{
    [Fact]
    public void Masks_secret_and_signature_keys_recursively()
    {
        var raw = """
            {"response_success":true,"request_id":"r-1",
             "model":{"bill_code":"TF1","task_id":"MOCK-1",
                      "app_secret":"s3cr3t","sign":"AABBCCDD","nested":[{"accessToken":"tok-9"}]}}
            """;
        var sanitized = TopResponseSanitizer.Sanitize(raw)!;

        Assert.DoesNotContain("s3cr3t", sanitized);
        Assert.DoesNotContain("AABBCCDD", sanitized);
        Assert.DoesNotContain("tok-9", sanitized);
        using var doc = JsonDocument.Parse(sanitized);
        var model = doc.RootElement.GetProperty("model");
        Assert.Equal(TopResponseSanitizer.Mask, model.GetProperty("app_secret").GetString());
        Assert.Equal(TopResponseSanitizer.Mask, model.GetProperty("sign").GetString());
        Assert.Equal("MOCK-1", model.GetProperty("task_id").GetString()); // 非敏感证据保留
        Assert.Equal("r-1", doc.RootElement.GetProperty("request_id").GetString());
    }

    [Fact]
    public void Keeps_business_evidence_and_does_not_mask_lookalike_keys()
    {
        var raw = """{"success_count":3,"failed_codes":["8123"],"assignee":"员工A","design":"x"}""";
        var sanitized = TopResponseSanitizer.Sanitize(raw)!;
        Assert.DoesNotContain(TopResponseSanitizer.Mask, sanitized);
        using var doc = JsonDocument.Parse(sanitized);
        Assert.Equal(3, doc.RootElement.GetProperty("success_count").GetInt32());
        Assert.Equal("员工A", doc.RootElement.GetProperty("assignee").GetString());
        Assert.Equal("x", doc.RootElement.GetProperty("design").GetString());
        Assert.Equal("8123", doc.RootElement.GetProperty("failed_codes")[0].GetString());
    }

    [Fact]
    public void Masks_key_value_pairs_in_non_json_text()
    {
        var sanitary = TopResponseSanitizer.Sanitize("HTTP 200 app_secret=abc123&sign=FFEE&bill_code=TF9")!;
        Assert.DoesNotContain("abc123", sanitary);
        Assert.DoesNotContain("FFEE", sanitary);
        Assert.Contains("bill_code=TF9", sanitary);
        Assert.Contains("app_secret=***", sanitary);
    }

    [Fact]
    public void Invalid_json_is_still_masked_and_null_passes_through()
    {
        var sanitary = TopResponseSanitizer.Sanitize("{not json sign=ZYXT appSecret:qq11")!;
        Assert.DoesNotContain("ZYXT", sanitary);
        Assert.DoesNotContain("qq11", sanitary);
        Assert.Null(TopResponseSanitizer.Sanitize(null));
    }
}