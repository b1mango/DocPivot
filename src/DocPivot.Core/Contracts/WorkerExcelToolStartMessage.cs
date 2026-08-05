namespace DocPivot.Core.Contracts;

public sealed record WorkerExcelToolStartMessage(
    int V,
    string Type,
    Guid JobId,
    string Operation,
    IReadOnlyList<string> Inputs,
    string Output,
    WorkerExcelToolOptions Options)
{
    public static WorkerExcelToolStartMessage Create(
        Guid jobId,
        string operation,
        IReadOnlyList<string> inputs,
        string output,
        WorkerExcelToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return new(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.Start,
            jobId,
            operation,
            inputs,
            output,
            options ?? WorkerExcelToolOptions.SafeDefaults);
    }
}
