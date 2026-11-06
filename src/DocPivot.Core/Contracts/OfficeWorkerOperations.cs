namespace DocPivot.Core.Contracts;

public static class OfficeWorkerOperations
{
    public const string WordToPdf = "word-to-pdf";
    public const string ExcelToPdf = "excel-to-pdf";
    public const string ListExcelWorksheets = "excel-list-worksheets";
    public const string MergeExcelWorkbooks = "excel-merge";
    public const string SplitExcelWorkbook = "excel-split";
    public const string CompressExcelWorkbook = "excel-compress";

    public const string RenderExcelWorksheetPreviews = "excel-render-previews";
    public const string ResizeExcelWorksheetPreviews = "excel-resize-previews";
    public const string EstimateExcelCompression = "excel-estimate-compression";

    public static bool IsExcelToolOperation(string operation) => operation is
        MergeExcelWorkbooks or
        SplitExcelWorkbook or
        CompressExcelWorkbook;
}
