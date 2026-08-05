namespace DocPivot.Core.Contracts;

public sealed record ExcelWorkbookCompatibilityReport(
    int WorksheetCount,
    int HiddenWorksheetCount,
    int ChartSheetCount,
    int ExternalLinkCount,
    bool HasVbaProject,
    bool Uses1904DateSystem,
    IReadOnlyList<string> CompressionRisks);
