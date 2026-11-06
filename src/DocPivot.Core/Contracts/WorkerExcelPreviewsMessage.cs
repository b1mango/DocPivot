namespace DocPivot.Core.Contracts;

public sealed record WorkerExcelWorksheetPreview(string Name, int Position, string? PdfPath, int? PageCount);

public sealed record WorkerExcelPreviewsMessage(
    int V,
    string Type,
    Guid JobId,
    IReadOnlyList<WorkerExcelWorksheetPreview> Worksheets)
{
    public const string MessageType = "excel-previews";

    public static WorkerExcelPreviewsMessage Create(
        Guid jobId, IReadOnlyList<WorkerExcelWorksheetPreview> worksheets) =>
        new(WorkerProtocol.CurrentVersion, MessageType, jobId, worksheets);
}
