using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;

namespace DocPivot.OfficeWorker;

internal static class ExcelToolRunner
{
    public static ExcelToolExecutionResult Execute(
        WorkerExcelToolStartMessage request,
        Action<int> reportProcessId,
        Action<string> reportStage)
    {
        var validated = Validate(request);
        using var session = ExcelApplicationSession.Start(request.JobId, reportProcessId, reportStage);
        return request.Operation switch
        {
            OfficeWorkerOperations.MergeExcelWorkbooks => ExcelWorkbookOperations.Merge(
                session,
                validated.Inputs,
                validated.Output,
                validated.Options,
                request.JobId,
                reportStage),
            OfficeWorkerOperations.SplitExcelWorkbook => ExcelWorkbookOperations.Split(
                session,
                validated.Inputs[0],
                validated.Output,
                validated.Options,
                request.JobId,
                reportStage),
            OfficeWorkerOperations.CompressExcelWorkbook => ExcelWorkbookOperations.Compress(
                session,
                validated.Inputs[0],
                validated.Output,
                validated.Options,
                request.JobId,
                reportStage),
            _ => throw new OfficeWorkerException(
                "OPERATION_UNSUPPORTED",
                "Unsupported Excel tool operation."),
        };
    }

    private static ValidatedExcelToolRequest Validate(WorkerExcelToolStartMessage request)
    {
        if (request.V != WorkerProtocol.CurrentVersion ||
            !string.Equals(request.Type, WorkerMessageTypes.Start, StringComparison.Ordinal) ||
            !OfficeWorkerOperations.IsExcelToolOperation(request.Operation))
        {
            throw new OfficeWorkerException(
                "START_MESSAGE_INVALID",
                "The Excel tool start message is invalid.");
        }

        if (request.Inputs is null || request.Options is null ||
            string.IsNullOrWhiteSpace(request.Output))
        {
            throw new OfficeWorkerException(
                "START_MESSAGE_INVALID",
                "Excel tool inputs, output, and options are required.");
        }

        var expectedInputCount = request.Operation == OfficeWorkerOperations.MergeExcelWorkbooks
            ? (Minimum: 2, Maximum: DocumentLimits.MaximumBatchFiles)
            : (Minimum: 1, Maximum: 1);
        if (request.Inputs.Count < expectedInputCount.Minimum ||
            request.Inputs.Count > expectedInputCount.Maximum)
        {
            throw new OfficeWorkerException(
                "INPUT_COUNT_INVALID",
                $"This operation requires between {expectedInputCount.Minimum} and {expectedInputCount.Maximum} input files.");
        }

        var inputs = new List<string>(request.Inputs.Count);
        var uniqueInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in request.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new OfficeWorkerException("INPUT_PATH_INVALID", "An Excel input path is empty.");
            }

            var inputPath = Path.GetFullPath(input);
            if (!uniqueInputs.Add(inputPath))
            {
                throw new OfficeWorkerException(
                    "INPUT_DUPLICATE",
                    "The same Excel workbook cannot appear more than once in a job.");
            }

            var inspection = OfficeDocumentInspector.Inspect(inputPath);
            if (!inspection.IsValid ||
                inspection.Kind is not (OfficeDocumentKind.ExcelBinary or OfficeDocumentKind.ExcelOpenXml))
            {
                throw new OfficeWorkerException(
                    inspection.ErrorCode ?? "INPUT_INVALID",
                    "An input does not match its supported Excel file type.");
            }

            inputs.Add(inputPath);
        }

        var output = Path.GetFullPath(request.Output);
        if (request.Operation == OfficeWorkerOperations.SplitExcelWorkbook)
        {
            if (File.Exists(output))
            {
                throw new OfficeWorkerException(
                    "OUTPUT_DIRECTORY_INVALID",
                    "The Excel split output path is an existing file.");
            }

            Directory.CreateDirectory(output);
        }
        else
        {
            var expectedExtension = request.Operation == OfficeWorkerOperations.CompressExcelWorkbook &&
                string.Equals(Path.GetExtension(inputs[0]), ".xls", StringComparison.OrdinalIgnoreCase) &&
                !request.Options.ConvertLegacyWorkbookToOpenXml
                ? ".xls"
                : ".xlsx";
            if (!string.Equals(Path.GetExtension(output), expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new OfficeWorkerException(
                    "OUTPUT_EXTENSION_INVALID",
                    $"This Excel operation requires the {expectedExtension} output extension.");
            }

            if (inputs.Contains(output, StringComparer.OrdinalIgnoreCase))
            {
                throw new OfficeWorkerException(
                    "SOURCE_OVERWRITE_BLOCKED",
                    "The source workbook cannot be overwritten.");
            }

            if (File.Exists(output) || Directory.Exists(output))
            {
                throw new OfficeWorkerException(
                    "OUTPUT_ALREADY_EXISTS",
                    "The Excel output path already exists.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        }

        return new ValidatedExcelToolRequest(inputs, output, request.Options);
    }

    private sealed record ValidatedExcelToolRequest(
        IReadOnlyList<string> Inputs,
        string Output,
        WorkerExcelToolOptions Options);
}
