namespace DocPivot.Core.Contracts;

public sealed record WorkerErrorMessage(
    int V,
    string Type,
    Guid JobId,
    string Code,
    string Message,
    bool Retryable)
{
    public static WorkerErrorMessage Create(
        Guid jobId,
        string code,
        string message,
        bool retryable) =>
        new(WorkerProtocol.CurrentVersion, WorkerMessageTypes.Error, jobId, code, message, retryable);
}

