namespace DocPivot.OfficeWorker;

internal sealed record ExcelWorksheetCleanupResult(
    bool WasCleaned,
    bool WasSkipped,
    string? SkipReason)
{
    public static ExcelWorksheetCleanupResult Cleaned { get; } = new(true, false, null);

    public static ExcelWorksheetCleanupResult Unchanged { get; } = new(false, false, null);

    public static ExcelWorksheetCleanupResult Skipped(string reason) => new(false, true, reason);
}
