using DocPivot.Core.Contracts;

namespace DocPivot.OfficeWorker;

internal static class ExcelWorksheetCatalog
{
    private const int ExcelWorksheetType = -4167;
    private const int ExcelSheetVisible = -1;
    private const int ExcelSheetHidden = 0;
    private const int ExcelSheetVeryHidden = 2;

    public static IReadOnlyList<ExcelWorksheetDescriptor> Read(object workbookObject)
    {
        dynamic workbook = workbookObject;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            var count = (int)worksheets.Count;
            var result = new List<ExcelWorksheetDescriptor>(count);
            for (var position = 1; position <= count; position++)
            {
                dynamic? worksheet = null;
                try
                {
                    worksheet = worksheets.Item[position];
                    result.Add(Describe(worksheet, position));
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }

            return result;
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }
    }

    public static object FindExportable(object workbookObject, string worksheetName)
    {
        dynamic workbook = workbookObject;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            var count = (int)worksheets.Count;
            for (var position = 1; position <= count; position++)
            {
                dynamic? worksheet = null;
                var keepWorksheet = false;
                try
                {
                    worksheet = worksheets.Item[position];
                    var descriptor = Describe(worksheet, position);
                    if (!string.Equals(descriptor.Name, worksheetName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if ((int)worksheet.Type != ExcelWorksheetType)
                    {
                        throw new OfficeWorkerException(
                            "EXCEL_WORKSHEET_TYPE_UNSUPPORTED",
                            "The selected Excel sheet type cannot be exported.");
                    }

                    if ((int)worksheet.Visible != ExcelSheetVisible)
                    {
                        throw new OfficeWorkerException(
                            "EXCEL_WORKSHEET_HIDDEN",
                            "The selected Excel worksheet is hidden.");
                    }

                    keepWorksheet = true;
                    return worksheet;
                }
                finally
                {
                    if (!keepWorksheet)
                    {
                        ComObject.FinalRelease(worksheet);
                    }
                }
            }
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }

        throw new OfficeWorkerException(
            "EXCEL_WORKSHEET_NOT_FOUND",
            "The selected Excel worksheet no longer exists.");
    }

    private static ExcelWorksheetDescriptor Describe(dynamic worksheet, int position)
    {
        var name = (string)worksheet.Name;
        var sheetType = (int)worksheet.Type;
        var visible = (int)worksheet.Visible;
        return new ExcelWorksheetDescriptor(
            name,
            position,
            visible switch
            {
                ExcelSheetVisible => "visible",
                ExcelSheetHidden => "hidden",
                ExcelSheetVeryHidden => "very-hidden",
                _ => "unknown",
            },
            sheetType == ExcelWorksheetType && visible == ExcelSheetVisible);
    }
}
