using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.UnitTests.Top;

public class TopGatewayTests
{
    private static TopCall Call(string env = "MOCK", string method = "listupout")
        => new(method, new Dictionary<string, string> { ["page_no"] = "1" }, env);

    [Fact]
    public async Task Fake_gateway_rejects_live_env()
    {
        var gw = new FakeTopGateway(_ => new TopRawResponse(true, 200, null, """{"response_success":true}""", null));
        await Assert.ThrowsAsync<TopNotCertifiedException>(() => gw.CallAsync(Call("LIVE"), default));
    }

    [Fact]
    public async Task Real_gateway_is_not_certified_placeholder()
        => await Assert.ThrowsAsync<TopNotCertifiedException>(() => new RealTopGateway().CallAsync(Call(), default));

    [Fact]
    public void Factory_unknown_mode_throws()
        => Assert.Throws<ArgumentException>(() => TopGatewayFactory.Create("sandbox", "unused"));

    [Fact]
    public void Fixture_handler_matches_params_and_extracts_error_node()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t4top." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "listupout.json"), """
                [
                  {"match":{"page_no":"9"},"response":{"error_response":{"code":7,"sub_code":"FAIL_BIZ_RATE_LIMIT"}},"retryAfterSeconds":8},
                  {"match":{"page_no":"1"},"response":{"alibaba_alihealth_drugtrace_top_listupout_response":{"response_success":true,"request_id":"r1","result":{"total_pages":2}}}}
                ]
                """);
            var h = new TopFixtureHandler(dir);
            var ok = h.Handle(Call());
            Assert.True(ok.TransportOk);
            Assert.Null(ok.ErrorJson);
            Assert.Contains("total_pages", ok.BusinessJson);
            var rate = h.Handle(new TopCall("listupout", new Dictionary<string, string> { ["page_no"] = "9" }, "MOCK"));
            Assert.Contains("FAIL_BIZ_RATE_LIMIT", rate.ErrorJson);
            Assert.Equal(8, rate.RetryAfterSeconds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Fixture_handler_missing_file_defaults_success_and_transport_entry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t4top." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var h = new TopFixtureHandler(dir);
            var def = h.Handle(Call(method: "no.such.method"));
            Assert.True(def.TransportOk);
            Assert.Contains("response_success", def.BusinessJson);
            File.WriteAllText(Path.Combine(dir, "down.json"), """[{"transportOk":false}]""");
            var down = h.Handle(Call(method: "down"));
            Assert.False(down.TransportOk);
        }
        finally { Directory.Delete(dir, true); }
    }
}
