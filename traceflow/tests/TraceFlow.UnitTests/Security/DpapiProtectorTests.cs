using System.Runtime.Versioning;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.UnitTests.Security;

[SupportedOSPlatform("windows")] // 被测组件为 Windows-only DPAPI
public class DpapiProtectorTests
{
    [Fact]
    public void Roundtrip_under_current_user()
    {
        var p = new DpapiProtector();
        var cipher = p.Protect("app-secret-测试");
        Assert.Equal("app-secret-测试", p.Unprotect(cipher));
        Assert.NotEqual("app-secret-测试"u8.ToArray(), cipher);
    }

    [Fact]
    public void Tampered_ciphertext_throws_no_fallback()
    {
        var p = new DpapiProtector();
        var cipher = p.Protect("secret");
        cipher[^1] ^= 0xFF;
        Assert.Throws<SecretUndecryptableException>(() => p.Unprotect(cipher));
    }
}
