namespace TraceFlow.Application.Abstractions;
public sealed class SecretUndecryptableException : Exception
{
    public SecretUndecryptableException(string message, Exception inner) : base(message, inner) { }
}
public interface ISecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] cipher); // 失败抛 SecretUndecryptableException：停调用+告警，无默认凭据
}
