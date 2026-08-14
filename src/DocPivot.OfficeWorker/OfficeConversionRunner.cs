using System.Runtime.InteropServices;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Infrastructure.Storage;

namespace DocPivot.OfficeWorker;

internal static class OfficeConversionRunner
{
    public static int Run(
        WorkerExcelToolStartMessage request,
        Action<object> writeMessage,
        Action<int> reportOfficeProcessId)
    {
        try
        {
            writeMessage(WorkerProgressMessage.Create(request.JobId, "preflight", 1, 4));
            void ReportExcelStage(string stage) =>
                writeMessage(WorkerProgressMessage.Create(request.JobId, stage, 2, 4));
            var result = ExcelToolRunner.Execute(
                request,
                reportOfficeProcessId,
                ReportExcelStage);
            writeMessage(WorkerProgressMessage.Create(request.JobId, "verifying", 3, 4));
            writeMessage(WorkerProgressMessage.Create(request.JobId, "completed", 4, 4));
            writeMessage(WorkerResultMessage.Succeeded(
                request.JobId,
                result.Artifacts,
                result.Notices,
                result.Metrics));
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
        catch (COMException exception)
        {
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "OFFICE_COM_FAILURE",
                $"Office automation failed with HRESULT 0x{exception.HResult:X8}.",
                true));
            return 3;
        }
        catch (IOException)
        {
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "OUTPUT_IO_FAILURE",
                "The Excel output could not be written.",
                true));
            return 4;
        }
        catch (UnauthorizedAccessException)
        {
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "OUTPUT_ACCESS_DENIED",
                "Access to an Excel input or output path was denied.",
                false));
            return 5;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "WORKER_UNEXPECTED_FAILURE",
                "The Excel worker encountered an unexpected failure.",
                true));
            return 70;
        }
    }

    public static int Run(
        WorkerStartMessage request,
        Action<object> writeMessage,
        Action<int> reportOfficeProcessId)
    {
        string? stagingPath = null;
        try
        {
            ValidateProtocol(request);
            if (request.Operation == OfficeWorkerOperations.ListExcelWorksheets)
            {
                var worksheetListInputPath = ValidateExcelWorksheetList(request);
                writeMessage(WorkerProgressMessage.Create(request.JobId, "preflight", 1, 3));
                void ReportExcelStage(string stage) =>
                    writeMessage(WorkerProgressMessage.Create(request.JobId, stage, 2, 3));
                using var session = ExcelWorkbookSession.Open(
                    worksheetListInputPath,
                    request.JobId,
                    reportOfficeProcessId,
                    ReportExcelStage);
                var worksheets = ExcelWorksheetCatalog.Read(session.Workbook);
                var workbookFacts = ExcelWorkbookInspector.Inspect(session.Workbook);
                var compatibility = new ExcelWorkbookCompatibilityReport(
                    workbookFacts.Worksheets.Count,
                    workbookFacts.Worksheets.Count(static worksheet => !worksheet.IsVisible),
                    workbookFacts.ChartSheetCount,
                    workbookFacts.ExternalLinkCount,
                    workbookFacts.HasVbaProject,
                    workbookFacts.Uses1904DateSystem,
                    ExcelCompressionRiskInspector.Inspect(session.Workbook));
                writeMessage(WorkerProgressMessage.Create(request.JobId, "completed", 3, 3));
                writeMessage(WorkerExcelWorksheetsMessage.Create(request.JobId, worksheets, compatibility));
                return 0;
            }

            var (inputPath, outputPath, kind) = Validate(request);
            writeMessage(WorkerProgressMessage.Create(request.JobId, "preflight", 1, 3));

            stagingPath = AtomicOutputFile.CreateStagingPath(outputPath, request.JobId);
            writeMessage(WorkerProgressMessage.Create(request.JobId, "office-export", 2, 3));
            void ReportOfficeStage(string stage) =>
                writeMessage(WorkerProgressMessage.Create(request.JobId, stage, 2, 3));
            if (kind is OfficeDocumentKind.WordBinary or OfficeDocumentKind.WordOpenXml)
            {
                WordPdfExporter.Export(
                    inputPath,
                    stagingPath,
                    reportOfficeProcessId,
                    ReportOfficeStage);
            }
            else
            {
                ExcelPdfExporter.Export(
                    inputPath,
                    stagingPath,
                    request.Options?.WorksheetName,
                    request.JobId,
                    reportOfficeProcessId,
                    ReportOfficeStage);
            }

            var pdfInspection = PdfFileInspector.Inspect(stagingPath);
            if (!pdfInspection.IsValid)
            {
                throw new OfficeWorkerException(
                    pdfInspection.ErrorCode ?? "PDF_OUTPUT_INVALID",
                    "Office produced an invalid PDF output.",
                    true);
            }

            AtomicOutputFile.Commit(stagingPath, outputPath);
            stagingPath = null;
            writeMessage(WorkerProgressMessage.Create(request.JobId, "completed", 3, 3));
            writeMessage(WorkerResultMessage.Succeeded(request.JobId, [outputPath]));
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
        catch (COMException exception)
        {
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "OFFICE_COM_FAILURE",
                $"Office automation failed with HRESULT 0x{exception.HResult:X8}.",
                true));
            return 3;
        }
        catch (IOException)
        {
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "OUTPUT_IO_FAILURE",
                "The output file could not be written.",
                true));
            return 4;
        }
        catch (UnauthorizedAccessException)
        {
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "OUTPUT_ACCESS_DENIED",
                "Access to the input or output path was denied.",
                false));
            return 5;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            writeMessage(WorkerErrorMessage.Create(
                request.JobId,
                "WORKER_UNEXPECTED_FAILURE",
                $"{exception.GetType().Name}: {exception.Message}",
                true));
            return 70;
        }
        finally
        {
            if (stagingPath is not null && File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static (string InputPath, string OutputPath, OfficeDocumentKind Kind) Validate(
        WorkerStartMessage request)
    {
        if (string.IsNullOrWhiteSpace(request.Output))
        {
            throw new OfficeWorkerException("OUTPUT_PATH_REQUIRED", "Office conversion requires an output path.");
        }

        var inputPath = Path.GetFullPath(request.Input);
        var outputPath = Path.GetFullPath(request.Output);
        if (!string.Equals(Path.GetExtension(outputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new OfficeWorkerException("OUTPUT_EXTENSION_INVALID", "Office conversion output must be a PDF.");
        }

        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new OfficeWorkerException("SOURCE_OVERWRITE_BLOCKED", "The source file cannot be overwritten.");
        }

        if (File.Exists(outputPath))
        {
            throw new OfficeWorkerException("OUTPUT_ALREADY_EXISTS", "The output file already exists.");
        }

        var inspection = OfficeDocumentInspector.Inspect(inputPath);
        if (!inspection.IsValid || inspection.Kind is null)
        {
            throw new OfficeWorkerException(
                inspection.ErrorCode ?? "INPUT_INVALID",
                "The input does not match its supported Office file type.");
        }

        var operationMatches = request.Operation switch
        {
            OfficeWorkerOperations.WordToPdf =>
                inspection.Kind is OfficeDocumentKind.WordBinary or OfficeDocumentKind.WordOpenXml,
            OfficeWorkerOperations.ExcelToPdf =>
                inspection.Kind is OfficeDocumentKind.ExcelBinary or OfficeDocumentKind.ExcelOpenXml,
            _ => throw new OfficeWorkerException("OPERATION_UNSUPPORTED", "Unsupported Office operation."),
        };
        if (!operationMatches)
        {
            throw new OfficeWorkerException("OPERATION_TYPE_MISMATCH", "The operation does not match the input type.");
        }

        var worksheetName = request.Options?.WorksheetName;
        if (inspection.Kind is OfficeDocumentKind.WordBinary or OfficeDocumentKind.WordOpenXml &&
            worksheetName is not null)
        {
            throw new OfficeWorkerException(
                "OFFICE_OPTIONS_TYPE_MISMATCH",
                "Excel worksheet options cannot be used with a Word document.");
        }

        if (worksheetName is not null &&
            (string.IsNullOrWhiteSpace(worksheetName) || worksheetName.Length > 31))
        {
            throw new OfficeWorkerException(
                "EXCEL_WORKSHEET_NAME_INVALID",
                "The Excel worksheet name is invalid.");
        }

        return (inputPath, outputPath, inspection.Kind.Value);
    }

    private static string ValidateExcelWorksheetList(WorkerStartMessage request)
    {
        if (request.Options is not null)
        {
            throw new OfficeWorkerException(
                "OFFICE_OPTIONS_UNSUPPORTED",
                "Worksheet list requests do not accept conversion options.");
        }

        var inputPath = Path.GetFullPath(request.Input);
        var inspection = OfficeDocumentInspector.Inspect(inputPath);
        if (!inspection.IsValid ||
            inspection.Kind is not (OfficeDocumentKind.ExcelBinary or OfficeDocumentKind.ExcelOpenXml))
        {
            throw new OfficeWorkerException(
                inspection.ErrorCode ?? "INPUT_INVALID",
                "The input does not match a supported Excel file type.");
        }

        return inputPath;
    }

    private static void ValidateProtocol(WorkerStartMessage request)
    {
        if (request.V != WorkerProtocol.CurrentVersion || request.Type != WorkerMessageTypes.Start)
        {
            throw new OfficeWorkerException("PROTOCOL_MISMATCH", "Unsupported worker protocol message.");
        }

        if (string.IsNullOrWhiteSpace(request.Operation) || string.IsNullOrWhiteSpace(request.Input))
        {
            throw new OfficeWorkerException(
                "START_MESSAGE_INVALID",
                "The start message requires an operation and input path.");
        }
    }
}
