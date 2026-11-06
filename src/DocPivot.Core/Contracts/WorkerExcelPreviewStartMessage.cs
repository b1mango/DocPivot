namespace DocPivot.Core.Contracts;

public sealed record WorkerExcelPreviewStartMessage(
    int V,
    string Type,
    Guid JobId,
    string Operation,
    string Input,
    string OutputDirectory)
{
    public static WorkerExcelPreviewStartMessage Create(Guid jobId, string input, string outputDirectory) =>
        new(WorkerProtocol.CurrentVersion, WorkerMessageTypes.Start, jobId,
            OfficeWorkerOperations.RenderExcelWorksheetPreviews, input, outputDirectory);
}

public sealed record WorkerExcelPreviewResizeStartMessage(
    int V,
    string Type,
    Guid JobId,
    string Operation,
    IReadOnlyList<string> ImagePaths,
    int PixelWidth)
{
    public static WorkerExcelPreviewResizeStartMessage Create(
        Guid jobId, IReadOnlyList<string> imagePaths, int pixelWidth) =>
        new(WorkerProtocol.CurrentVersion, WorkerMessageTypes.Start, jobId,
            OfficeWorkerOperations.ResizeExcelWorksheetPreviews, imagePaths, pixelWidth);
}
