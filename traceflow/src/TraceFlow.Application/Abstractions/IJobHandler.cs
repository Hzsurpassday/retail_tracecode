namespace TraceFlow.Application.Abstractions;
public interface IJobHandler
{
    string Kind { get; }
    Task HandleAsync(ClaimedJob job, CancellationToken ct);
}
