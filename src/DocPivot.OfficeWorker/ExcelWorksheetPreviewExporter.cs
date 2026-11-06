using System.Globalization;
using System.Runtime.InteropServices;
using DocPivot.Core.Contracts;

namespace DocPivot.OfficeWorker;

internal static class ExcelWorksheetPreviewExporter
{
    private const int ExcelWorksheetType = -4167;
    private const int ExcelSheetVisible = -1;

    public static IReadOnlyList<WorkerExcelWorksheetPreview> Export(
        string inputPath,
        string outputDirectory,
        Guid jobId,
        Action<int> reportProcessId,
        Action<string> reportStage)
    {
        using var session = ExcelWorkbookSession.Open(inputPath, jobId, reportProcessId, reportStage);
        dynamic workbook = session.Workbook;
        dynamic? sheets = null;
        try
        {
            sheets = workbook.Sheets;
            var previews = new List<WorkerExcelWorksheetPreview>();
            for (var position = 1; position <= (int)sheets.Count; position++)
            {
                dynamic? sheet = null;
                try
                {
                    sheet = sheets.Item[position];
                    if ((int)sheet.Visible != ExcelSheetVisible)
                    {
                        continue;
                    }

                    var name = (string)sheet.Name;
                    string? pdfPath = null;
                    int? pageCount = null;
                    if ((int)sheet.Type == ExcelWorksheetType)
                    {
                        try
                        {
                            if (HasPrintableContent(sheet))
                            {
                                var path = Path.Combine(outputDirectory,
                                    $"sheet-{position.ToString("D4", CultureInfo.InvariantCulture)}.pdf");
                                sheet.ExportAsFixedFormat(
                                    Type: 0,
                                    Filename: path,
                                    Quality: 0,
                                    IncludeDocProperties: false,
                                    IgnorePrintAreas: false,
                                    From: 1,
                                    To: 1,
                                    OpenAfterPublish: false);
                                pdfPath = path;
                                pageCount = ReadPageCount(sheet);
                            }
                        }
                        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
                        {
                            // Excel can reject an individual print area without affecting other sheets.
                        }
                    }

                    previews.Add(new WorkerExcelWorksheetPreview(name, position, pdfPath, pageCount));
                    reportStage("excel-worksheet-preview-exported");
                }
                finally
                {
                    ComObject.FinalRelease(sheet);
                }
            }

            return previews;
        }
        finally
        {
            ComObject.FinalRelease(sheets);
        }
    }

    private static bool HasPrintableContent(dynamic worksheet)
    {
        dynamic? usedRange = null;
        dynamic? firstContentCell = null;
        dynamic? shapes = null;
        try
        {
            shapes = worksheet.Shapes;
            if ((int)shapes.Count > 0)
            {
                return true;
            }

            usedRange = worksheet.UsedRange;
            firstContentCell = usedRange.Find(What: "*", LookIn: -4123, LookAt: 2,
                SearchOrder: 1, SearchDirection: 1, MatchCase: false, SearchFormat: false);
            return firstContentCell is not null;
        }
        finally
        {
            ComObject.FinalRelease(shapes);
            ComObject.FinalRelease(firstContentCell);
            ComObject.FinalRelease(usedRange);
        }
    }

    private static int? ReadPageCount(dynamic worksheet)
    {
        dynamic? pageSetup = null;
        dynamic? pages = null;
        try
        {
            pageSetup = worksheet.PageSetup;
            pages = pageSetup.Pages;
            return (int)pages.Count;
        }
        catch (COMException)
        {
            // Some printer drivers cannot enumerate pages even after a successful PDF export.
            return null;
        }
        finally
        {
            ComObject.FinalRelease(pages);
            ComObject.FinalRelease(pageSetup);
        }
    }
}
