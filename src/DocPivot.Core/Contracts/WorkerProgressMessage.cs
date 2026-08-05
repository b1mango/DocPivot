namespace DocPivot.Core.Contracts;

public sealed record WorkerProgressMessage(
    int V,
    string Type,
    Guid JobId,
    string Stage,
    int Current,
    int Total)
{
    public static WorkerProgressMessage Create(
        Guid jobId,
        string stage,
        int current,
        int total) =>
        new(WorkerProtocol.CurrentVersion, WorkerMessageTypes.Progress, jobId, stage, current, total);
}

