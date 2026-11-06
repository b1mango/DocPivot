namespace DocPivot.Core.Contracts;

public sealed record WorkerExcelCompressionEstimateStartMessage(
    int V,
    string Type,
    Guid JobId,
    string Operation,
    string Input,
    int Strength,
    string CacheDirectory)
{
    public static WorkerExcelCompressionEstimateStartMessage Create(
        Guid jobId,
        string input,
        int strength,
        string cacheDirectory) => new(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.Start,
            jobId,
            OfficeWorkerOperations.EstimateExcelCompression,
            input,
            strength,
            cacheDirectory);
}
