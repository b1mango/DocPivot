using DocPivot.Core.Contracts;
using DocPivot.Infrastructure.Operations;

namespace DocPivot.Infrastructure.Tables;

public sealed record PdfToExcelRequest(
    string InputPath,
    string OutputPath,
    WorkerPdfOcrMode OcrMode,
    WorkerPdfWorksheetMode WorksheetMode,
    string OcrLanguage = "chi_sim+eng",
    string? Password = null);

public sealed record PdfTableEngineStatus(
    bool IsDigitalTextReady,
    bool IsLocalOcrReady,
    bool IsXlsxReady,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsReady => IsDigitalTextReady && IsLocalOcrReady && IsXlsxReady;

    public static PdfTableEngineStatus Unavailable(string errorCode, string errorMessage) =>
        new(false, false, false, errorCode, errorMessage);
}

public interface IPdfTableOperationsClient
{
    Task<PdfTableEngineStatus> ProbeAsync(CancellationToken cancellationToken = default);

    Task<OperationExecutionResult> ConvertAsync(
        PdfToExcelRequest request,
        IProgress<WorkerProgressMessage>? progress = null,
        CancellationToken cancellationToken = default);
}
