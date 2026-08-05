using System.Text.Json.Serialization;

namespace DocPivot.Core.Contracts;

public static class PdfTableWorkerOperations
{
    public const string PdfToExcel = "pdf-to-excel";
}

public enum WorkerPdfOcrMode
{
    Automatic = 0,
    DigitalTextOnly = 1,
    ForceOcr = 2,
}

public enum WorkerPdfWorksheetMode
{
    OneWorksheetPerTable = 0,
    OneWorksheetPerPage = 1,
}

public sealed record WorkerPdfToExcelOptions(
    WorkerPdfOcrMode OcrMode,
    WorkerPdfWorksheetMode WorksheetMode,
    string OcrLanguage = "chi_sim+eng");

public sealed record WorkerPdfToExcelStartMessage(
    int V,
    string Type,
    Guid JobId,
    string Operation,
    string Input,
    string Output,
    WorkerPdfToExcelOptions Options)
{
    public static WorkerPdfToExcelStartMessage Create(
        Guid jobId,
        string input,
        string output,
        WorkerPdfToExcelOptions options) =>
        new(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.Start,
            jobId,
            PdfTableWorkerOperations.PdfToExcel,
            input,
            output,
            options);
}

public sealed record WorkerPdfTableProbeResult(
    int V,
    string Type,
    string Worker,
    string Status,
    IReadOnlyDictionary<string, bool> Capabilities,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null);
