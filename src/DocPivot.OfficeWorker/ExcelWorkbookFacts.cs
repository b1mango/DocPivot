namespace DocPivot.OfficeWorker;

internal sealed record ExcelWorkbookFacts(
    IReadOnlyList<ExcelWorksheetFact> Worksheets,
    int ChartSheetCount,
    IReadOnlyList<string> ExternalLinkSources,
    bool HasVbaProject,
    bool Uses1904DateSystem)
{
    public int ExternalLinkCount => ExternalLinkSources.Count;
}

internal sealed record ExcelWorksheetFact(
    string Name,
    int Position,
    bool IsVisible);
