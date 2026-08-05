namespace DocPivot.OfficeWorker;

internal static class ExcelPdfExporter
{
    public static void Export(
        string inputPath,
        string outputPath,
        string? worksheetName,
        Guid jobId,
        Action<int> reportProcessId,
        Action<string> reportStage)
    {
        using var session = ExcelWorkbookSession.Open(inputPath, jobId, reportProcessId, reportStage);
        dynamic workbook = session.Workbook;
        dynamic? worksheet = null;
        try
        {
            dynamic exportTarget = workbook;
            if (worksheetName is not null)
            {
                worksheet = ExcelWorksheetCatalog.FindExportable(workbook, worksheetName);
                exportTarget = worksheet;
                reportStage("worksheet-resolved");
            }

            exportTarget.ExportAsFixedFormat(
                Type: 0,
                Filename: outputPath,
                Quality: 0,
                IncludeDocProperties: true,
                IgnorePrintAreas: false,
                OpenAfterPublish: false);
            reportStage("pdf-exported");
        }
        finally
        {
            ComObject.FinalRelease(worksheet);
        }
    }
}
