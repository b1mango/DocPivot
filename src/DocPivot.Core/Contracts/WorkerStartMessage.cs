using System.Text.Json.Serialization;

namespace DocPivot.Core.Contracts;

public sealed record WorkerStartMessage(
    int V,
    string Type,
    Guid JobId,
    string Operation,
    string Input,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Output,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkerRequestOptions? Options = null)
{
    public static WorkerStartMessage Create(Guid jobId, string operation, string input, string output) =>
        new(WorkerProtocol.CurrentVersion, WorkerMessageTypes.Start, jobId, operation, input, output);

    public static WorkerStartMessage CreateConversion(
        Guid jobId,
        string operation,
        string input,
        string output,
        string? worksheetName = null) =>
        new(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.Start,
            jobId,
            operation,
            input,
            output,
            worksheetName is null ? null : new WorkerRequestOptions(worksheetName));

    public static WorkerStartMessage CreateExcelWorksheetList(Guid jobId, string input) =>
        new(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.Start,
            jobId,
            OfficeWorkerOperations.ListExcelWorksheets,
            input,
            null);
}
