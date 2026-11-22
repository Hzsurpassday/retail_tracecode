using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Security;

[SupportedOSPlatform("windows")] // DPAPI 仅 Windows；产品部署目标为 Windows 服务（ADR-0001）
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TraceFlow.v1");

    public byte[] Protect(string plaintext)
        => ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] cipher)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException ex)
        {
            throw new SecretUndecryptableException(
                "凭据密文不可解密（换机/恢复/服务账号变更？）。停止对应调用并告警；走重新录入向导，禁止默认凭据兜底。", ex);
        }
    }
}