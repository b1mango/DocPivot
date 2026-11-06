using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;

namespace DocPivot.OfficeWorker;

internal static class ExcelCompressionEstimateRunner
{
    public static int Run(
        WorkerExcelCompressionEstimateStartMessage request,
        Action<object> writeMessage)
    {
        try
        {
            if (request.V != WorkerProtocol.CurrentVersion ||
                request.Type != WorkerMessageTypes.Start ||
                request.Operation != OfficeWorkerOperations.EstimateExcelCompression ||
                string.IsNullOrWhiteSpace(request.Input) ||
                string.IsNullOrWhiteSpace(request.CacheDirectory) ||
                request.Strength is < 0 or > 100)
            {
                throw new OfficeWorkerException("START_MESSAGE_INVALID", "The Excel estimate request is invalid.");
            }

            var inspection = OfficeDocumentInspector.Inspect(request.Input);
            if (!inspection.IsValid ||
                inspection.Kind is not (OfficeDocumentKind.ExcelBinary or OfficeDocumentKind.ExcelOpenXml))
            {
                throw new OfficeWorkerException(
                    inspection.ErrorCode ?? "INPUT_INVALID",
                    "The input does not match a supported Excel file type.");
            }

            var estimate = ExcelCompressionSizeEstimator.Estimate(
                request.Input,
                request.Strength,
                request.CacheDirectory);
            writeMessage(WorkerExcelCompressionEstimateMessage.Create(
                request.JobId,
                estimate.OriginalBytes,
                estimate.EstimatedBytes));
            return 0;
        }
        catch (OfficeWorkerException exception)
        {
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                exception.Code,
                exception.Message,
                exception.Retryable));
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "EXCEL_ESTIMATE_IO_FAILURE",
                "The Excel workbook or estimate cache could not be read or written.",
                true));
            return 4;
        }
    }
}
