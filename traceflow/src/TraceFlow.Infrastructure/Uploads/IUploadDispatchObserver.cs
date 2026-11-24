namespace TraceFlow.Infrastructure.Uploads;

/// <summary>
/// 发送边界观察者（窄缝，仅供故障注入测试注入）：三个回调恰好对应 <see cref="UploadDispatchService"/> 的三处崩溃点，
/// 顺序与位置固定——<list type="number">
/// <item><see cref="BeforeSendAsync"/>：尝试行已提交 + 分片已置 Dispatching 之后、调网关之前（此刻崩溃 = 请求从未发出）；</item>
/// <item><see cref="AfterSendAsync"/>：网关调用已返回（含限流/暂停归一）之后、T3 写回之前（此刻崩溃 = 请求可能已发出、回执未落库）；</item>
/// <item><see cref="BeforeCommitAsync"/>：T3 事务内回执/状态已写入、提交之前（此刻崩溃 = 整个 T3 回滚，尝试行保持未收尾）。</item>
/// </list>
/// 生产 DI 不注册、构造不传入（null = 无操作，行为与无观察者完全一致）；观察者抛出的异常原样上抛，绝不吞掉。
/// </summary>
public interface IUploadDispatchObserver
{
    Task BeforeSendAsync(CancellationToken ct);
    Task AfterSendAsync(CancellationToken ct);
    Task BeforeCommitAsync(CancellationToken ct);
}