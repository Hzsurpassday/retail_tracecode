using TraceFlow.Domain.Top;
using Xunit;

namespace TraceFlow.UnitTests.Top;

public class TopResponseParserTests
{
    private const string OkBusiness = """
        {"response_success":true,"request_id":"req-1","result":{"total_pages":1}}
        """;

    [Fact]
    public void Transport_failure_is_uncertain_never_success()
    {
        var o = TopResponseParser.Parse(false, null, OkBusiness, null);
        Assert.Equal(TopOutcomeKind.TransportUncertain, o.Kind);
    }

    [Fact]
    public void Success_keeps_result_raw_and_request_id()
    {
        var o = TopResponseParser.Parse(true, null, OkBusiness, null);
        Assert.Equal(TopOutcomeKind.Success, o.Kind);
        Assert.Equal("req-1", o.RequestId);
        Assert.Contains("total_pages", o.ResultJson);
    }

    [Fact]
    public void Error_envelope_auth_token_maps_auth_error()
    {
        var err = """{"code":15,"msg":"Remote service error","sub_code":"FAIL_BIZ_AUTH_ERROR","sub_msg":"无权限","request_id":"req-2"}""";
        var o = TopResponseParser.Parse(true, err, null, null);
        Assert.Equal(TopOutcomeKind.AuthError, o.Kind);
        Assert.Equal("FAIL_BIZ_AUTH_ERROR", o.SubCode);
        Assert.Equal("req-2", o.RequestId);
    }

    [Fact]
    public void Error_envelope_rate_prefix_maps_rate_limited_with_retry_after()
    {
        var err = """{"code":7,"msg":"App Call Limited","sub_code":"FAIL_BIZ_RATE_LIMIT","request_id":"req-3"}""";
        var o = TopResponseParser.Parse(true, err, null, 12);
        Assert.Equal(TopOutcomeKind.RateLimited, o.Kind);
        Assert.Equal(12, o.RetryAfterSeconds);
    }

    [Fact]
    public void Error_envelope_other_maps_system_error()
    {
        var err = """{"code":2,"msg":"Service unavailable","sub_code":"isp.top-remote-connection-timeout"}""";
        var o = TopResponseParser.Parse(true, err, null, null);
        Assert.Equal(TopOutcomeKind.SystemError, o.Kind);
        Assert.Equal("isp.top-remote-connection-timeout", o.SubCode);
    }

    [Fact]
    public void Business_failure_is_never_success()
    {
        var biz = """{"response_success":false,"msg_code":"FAIL_BIZ_CHECK","sub_msg_code":"FAIL_BIZ_BATCH_CONFLICT","sub_msg":"批号不符","request_id":"req-4"}""";
        var o = TopResponseParser.Parse(true, null, biz, null);
        Assert.Equal(TopOutcomeKind.BusinessRejected, o.Kind);
        Assert.Equal("FAIL_BIZ_CHECK", o.Code);
        Assert.Equal("FAIL_BIZ_BATCH_CONFLICT", o.SubCode);
        Assert.Equal("批号不符", o.Message);
    }

    [Fact]
    public void Missing_response_success_flag_is_business_rejected()
    {
        var o = TopResponseParser.Parse(true, null, """{"result":{}}""", null);
        Assert.Equal(TopOutcomeKind.BusinessRejected, o.Kind);
    }

    [Fact]
    public void Partial_success_token_maps_partial_evidence_even_when_flag_false()
    {
        var biz = """{"response_success":false,"msg_code":"WARN_BIZ_PARTSUCCESS","result":{"success_count":3}}""";
        var o = TopResponseParser.Parse(true, null, biz, null);
        Assert.Equal(TopOutcomeKind.PartialEvidence, o.Kind);
        Assert.Contains("success_count", o.ResultJson);
    }

    [Fact]
    public void Duplicate_bill_code_sub_token_maps_duplicate()
    {
        var biz = """{"response_success":false,"sub_msg_code":"FAIL_BIZ_BILL_CODE_DUPLICATE","sub_msg":"单据号重复"}""";
        var o = TopResponseParser.Parse(true, null, biz, null);
        Assert.Equal(TopOutcomeKind.DuplicateBillCode, o.Kind);
    }

    [Fact]
    public void Business_level_auth_and_rate_tokens_also_mapped()
    {
        var auth = TopResponseParser.Parse(true, null, """{"response_success":false,"sub_msg_code":"FAIL_BIZ_AUTH_ERROR"}""", null);
        Assert.Equal(TopOutcomeKind.AuthError, auth.Kind);
        var rate = TopResponseParser.Parse(true, null, """{"response_success":false,"sub_msg_code":"FAIL_BIZ_RATE_APP"}""", 5);
        Assert.Equal(TopOutcomeKind.RateLimited, rate.Kind);
        Assert.Equal(5, rate.RetryAfterSeconds);
    }

    [Fact]
    public void Unparseable_json_is_system_error_not_success()
    {
        var o = TopResponseParser.Parse(true, "{not json", null, null);
        Assert.Equal(TopOutcomeKind.SystemError, o.Kind);
        var o2 = TopResponseParser.Parse(true, null, "<html>502</html>", null);
        Assert.Equal(TopOutcomeKind.SystemError, o2.Kind);
    }

    [Fact]
    public void Empty_envelope_is_system_error()
    {
        var o = TopResponseParser.Parse(true, null, null, null);
        Assert.Equal(TopOutcomeKind.SystemError, o.Kind);
    }
}
