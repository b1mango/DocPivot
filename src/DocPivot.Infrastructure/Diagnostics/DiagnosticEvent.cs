namespace DocPivot.Infrastructure.Diagnostics;

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    DiagnosticLevel Level,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, string> Properties);
