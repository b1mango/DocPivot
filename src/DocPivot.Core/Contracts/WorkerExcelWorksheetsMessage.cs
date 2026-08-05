namespace DocPivot.Core.Contracts;

public sealed record WorkerExcelWorksheetsMessage(
    int V,
    string Type,
    Guid JobId,
    IReadOnlyList<ExcelWorksheetDescriptor> Worksheets,
    ExcelWorkbookCompatibilityReport? Compatibility)
{
    public static WorkerExcelWorksheetsMessage Create(
        Guid jobId,
        IReadOnlyList<ExcelWorksheetDescriptor> worksheets,
        ExcelWorkbookCompatibilityReport? compatibility = null) =>
        new(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.ExcelWorksheets,
            jobId,
            worksheets,
            compatibility);
}
