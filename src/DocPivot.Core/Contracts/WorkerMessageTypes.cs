namespace DocPivot.Core.Contracts;

public static class WorkerMessageTypes
{
    public const string Start = "start";
    public const string Progress = "progress";
    public const string Result = "result";
    public const string Error = "error";
    public const string ProbeResult = "probe-result";
    public const string ExcelWorksheets = "excel-worksheets";
}
