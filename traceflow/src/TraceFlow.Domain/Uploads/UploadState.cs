namespace TraceFlow.Domain.Uploads;

/// <summary>PRD §9.2 平台子任务状态（12 态）。设计 §2.6 中文态映射见路线图。</summary>
public enum UploadState
{
    Blocked, Ready, Dispatching, Accepted, AcceptanceUnknown, Processing,
    RetryWait, PartialSuccess, ResultUnresolved, Succeeded, Failed, ManualAction
}
