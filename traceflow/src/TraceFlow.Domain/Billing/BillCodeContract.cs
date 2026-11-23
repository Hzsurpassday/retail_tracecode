namespace TraceFlow.Domain.Billing;

/// <summary>平台单号合同：字母数字前缀 + 最大长度。限值必须来自 G-TOP 验证，不得凭旧文档假定。</summary>
public sealed record BillCodeContract(string Prefix, int MaxLength);
