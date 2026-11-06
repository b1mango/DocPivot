namespace DocPivot.Core.Contracts;

public sealed record WorkerExcelCompressionEstimateMessage(
    int V,
    string Type,
    Guid JobId,
    long OriginalBytes,
    long EstimatedBytes)
{
    public const string MessageType = "excel-compression-estimate";

    public static WorkerExcelCompressionEstimateMessage Create(
        Guid jobId,
        long originalBytes,
        long estimatedBytes) => new(
            WorkerProtocol.CurrentVersion,
            MessageType,
            jobId,
            originalBytes,
            estimatedBytes);
}
