using DocPivot.Core.Contracts;

namespace DocPivot.Infrastructure.Office;

public interface IOfficeWorkerClient
{
    Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default);

    Task<ExcelWorksheetListResult> ListExcelWorksheetsAsync(
        string inputPath,
        CancellationToken cancellationToken = default);

    Task<OfficeConversionResult> ConvertAsync(
        string inputPath,
        string outputPath,
        OfficeConversionOptions? options = null,
        IProgress<WorkerProgressMessage>? progress = null,
        CancellationToken cancellationToken = default);
}
