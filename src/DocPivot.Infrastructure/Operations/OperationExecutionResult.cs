using DocPivot.Core.Contracts;

namespace DocPivot.Infrastructure.Operations;

public sealed record OperationExecutionResult(
    bool IsSucceeded,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<WorkerNotice> Notices,
    IReadOnlyDictionary<string, string> Metrics,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsRetryable)
{
    public static OperationExecutionResult Succeeded(
        IReadOnlyList<string> artifacts,
        IReadOnlyList<WorkerNotice>? notices = null,
        IReadOnlyDictionary<string, string>? metrics = null) =>
        new(true, artifacts, notices ?? [], metrics ?? new Dictionary<string, string>(), null, null, false);

    public static OperationExecutionResult Failed(
        string errorCode,
        string errorMessage,
        bool isRetryable) =>
        new(false, [], [], new Dictionary<string, string>(), errorCode, errorMessage, isRetryable);
}
