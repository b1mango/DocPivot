using DocPivot.Core.Contracts;

namespace DocPivot.Infrastructure.Office;

public sealed record ExcelWorksheetListResult(
    bool IsSucceeded,
    IReadOnlyList<ExcelWorksheetDescriptor> Worksheets,
    ExcelWorkbookCompatibilityReport? Compatibility,
    string? ErrorCode,
    string? ErrorMessage,
    bool Retryable)
{
    public static ExcelWorksheetListResult Succeeded(
        IReadOnlyList<ExcelWorksheetDescriptor> worksheets,
        ExcelWorkbookCompatibilityReport? compatibility = null) =>
        new(true, worksheets, compatibility, null, null, false);

    public static ExcelWorksheetListResult Failed(
        string errorCode,
        string errorMessage,
        bool retryable) =>
        new(false, [], null, errorCode, errorMessage, retryable);
}
