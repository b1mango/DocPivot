namespace DocPivot.Core.Contracts;

public sealed record WorkerNotice(
    string Code,
    string Message,
    string Severity);
