namespace DocPivot.OfficeWorker;

internal static class ExcelWorkbookInspector
{
    private const int ExcelSheetVisible = -1;
    private const int ExcelLinks = 1;

    public static ExcelWorkbookFacts Inspect(object workbookValue)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        dynamic? charts = null;
        object? links = null;
        try
        {
            worksheets = workbook.Worksheets;
            var worksheetCount = (int)worksheets.Count;
            var worksheetFacts = new List<ExcelWorksheetFact>(worksheetCount);
            for (var index = 1; index <= worksheetCount; index++)
            {
                dynamic? worksheet = null;
                try
                {
                    worksheet = worksheets[index];
                    worksheetFacts.Add(new ExcelWorksheetFact(
                        (string)worksheet.Name,
                        index,
                        (int)worksheet.Visible == ExcelSheetVisible));
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }

            charts = workbook.Charts;
            links = workbook.LinkSources(Type: ExcelLinks);
            return new ExcelWorkbookFacts(
                worksheetFacts,
                (int)charts.Count,
                ReadExternalLinkSources(links),
                (bool)workbook.HasVBProject,
                (bool)workbook.Date1904);
        }
        finally
        {
            ComObject.FinalRelease(links);
            ComObject.FinalRelease(charts);
            ComObject.FinalRelease(worksheets);
        }
    }

    private static List<string> ReadExternalLinkSources(object? links)
    {
        if (links is not Array linkArray)
        {
            return [];
        }

        var sources = new List<string>(linkArray.Length);
        foreach (var link in linkArray)
        {
            if (link is string source && !string.IsNullOrWhiteSpace(source))
            {
                sources.Add(source);
            }
        }

        return sources;
    }
}
