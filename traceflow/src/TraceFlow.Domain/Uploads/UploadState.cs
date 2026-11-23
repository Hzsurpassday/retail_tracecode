namespace TraceFlow.Domain.Uploads;

/// <summary>PRD §9.2 平台子任务状态（12 态）+ 本地行政终态 Cancelled（IN-09 裁定，非平台状态）。</summary>
public enum UploadState
{
    Blocked, Ready, Dispatching, Accepted, AcceptanceUnknown, Processing,
    RetryWait, PartialSuccess, ResultUnresolved, Succeeded, Failed, ManualAction,
    Cancelled
}
