namespace TraceFlow.Domain.Codes;

public enum CodeFormat { Empty, Supported8Prefix20, Unsupported }

public sealed record NormalizedCode(string Raw, string? Normalized, CodeFormat Format);