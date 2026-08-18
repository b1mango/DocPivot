using System.Globalization;
using System.Security.Cryptography;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Tables;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Storage;

namespace DocPivot.PdfWorker;

public sealed class PdfTableWorkerRunner
{
    private const long MaximumOutputCells = 1_000_000;
    private readonly string _distributionRoot;
    private readonly EmbeddedTessdataStore _tessdataStore;

    public PdfTableWorkerRunner(
        string distributionRoot,
        EmbeddedTessdataStore? tessdataStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionRoot);
        _distributionRoot = PdfRuntimeDistributionRoot.Resolve(distributionRoot);
        _tessdataStore = tessdataStore ?? new EmbeddedTessdataStore();
    }

    public async Task<WorkerResultMessage> RunAsync(
        WorkerPdfToExcelStartMessage request,
        Action<object>? writeMessage = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var inputPath = Path.GetFullPath(request.Input);
        var outputPath = Path.GetFullPath(request.Output);
        ValidatePaths(inputPath, outputPath);

        var sourceHash = await ComputeSha256Async(inputPath, cancellationToken).ConfigureAwait(false);
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "DocPivot", "pdf-table-worker");
        using var workspace = JobWorkspace.Create(workspaceRoot, request.JobId);
        TesseractOcrProvider? ocrProvider = null;
        try
        {
            void Report(WorkerProgressMessage message) =>
                writeMessage?.Invoke(message with { JobId = request.JobId });

            Report(WorkerProgressMessage.Create(
                request.JobId,
                "pdf-opened",
                0,
                1));
            var renderer = new GhostscriptPageRenderer(_distributionRoot);
            var extractor = new PdfTableExtractor(
                renderer,
                async token =>
                {
                    var tessdataPath = await _tessdataStore
                        .EnsureAvailableAsync(request.Options.OcrLanguage, token)
                        .ConfigureAwait(false);
                    ocrProvider = new TesseractOcrProvider(
                        tessdataPath,
                        request.Options.OcrLanguage);
                    return ocrProvider;
                });
            var extraction = await extractor.ExtractAsync(
                    inputPath,
                    workspace.Path,
                    request.Options,
                    Report,
                    cancellationToken)
                .ConfigureAwait(false);
            if (extraction.Tables.Count == 0)
            {
                throw new PdfTableWorkerException(
                    "PDF_TABLE_NOT_FOUND",
                    "No recognizable table or text rows were found in the PDF.");
            }

            var outputCellCount = extraction.Tables.Sum(
                static table => checked((long)table.RowCount * table.ColumnCount));
            if (outputCellCount > MaximumOutputCells)
            {
                throw new PdfTableWorkerException(
                    "PDF_TABLE_CELL_LIMIT_EXCEEDED",
                    "The extracted table set exceeds the workbook cell safety limit.");
            }

            var tableSheetCount = OpenXmlTableWorkbookWriter.GetTableWorksheetCount(
                extraction,
                request.Options.WorksheetMode);
            var stagingPath = AtomicOutputFile.CreateStagingPath(outputPath, request.JobId);
            try
            {
                writeMessage?.Invoke(WorkerProgressMessage.Create(
                    request.JobId,
                    "workbook-writing",
                    extraction.PageCount,
                    extraction.PageCount + 2));
                OpenXmlTableWorkbookWriter.Write(
                    stagingPath,
                    extraction,
                    request.Options.WorksheetMode);
                cancellationToken.ThrowIfCancellationRequested();
                writeMessage?.Invoke(WorkerProgressMessage.Create(
                    request.JobId,
                    "workbook-validating",
                    extraction.PageCount + 1,
                    extraction.PageCount + 2));
                OpenXmlTableWorkbookWriter.Validate(stagingPath, tableSheetCount);

                var finalSourceHash = await ComputeSha256Async(inputPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(sourceHash, finalSourceHash, StringComparison.Ordinal))
                {
                    throw new PdfTableWorkerException(
                        "PDF_SOURCE_CHANGED",
                        "The source PDF changed during extraction.",
                        isRetryable: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                AtomicOutputFile.Commit(stagingPath, outputPath);
            }
            finally
            {
                TryDelete(stagingPath);
            }

            writeMessage?.Invoke(WorkerProgressMessage.Create(
                request.JobId,
                "completed",
                extraction.PageCount + 2,
                extraction.PageCount + 2));
            return WorkerResultMessage.Succeeded(
                request.JobId,
                [outputPath],
                BuildNotices(extraction),
                BuildMetrics(extraction, tableSheetCount, outputCellCount, outputPath));
        }
        finally
        {
            ocrProvider?.Dispose();
        }
    }

    private static void ValidateRequest(WorkerPdfToExcelStartMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.V != WorkerProtocol.CurrentVersion ||
            !string.Equals(request.Type, WorkerMessageTypes.Start, StringComparison.Ordinal) ||
            request.JobId == Guid.Empty ||
            !string.Equals(
                request.Operation,
                PdfTableWorkerOperations.PdfToExcel,
                StringComparison.Ordinal) ||
            request.Options is null ||
            !Enum.IsDefined(request.Options.OcrMode) ||
            !Enum.IsDefined(request.Options.WorksheetMode))
        {
            throw new PdfTableWorkerException(
                "START_MESSAGE_INVALID",
                "The PDF table worker start message is invalid.");
        }
    }

    private static void ValidatePaths(string inputPath, string outputPath)
    {
        if (!string.Equals(Path.GetExtension(inputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfTableWorkerException(
                "PDF_INPUT_INVALID",
                "The PDF table worker input must use the .pdf extension.");
        }

        var input = new FileInfo(inputPath);
        if (!input.Exists)
        {
            throw new PdfTableWorkerException("PDF_INPUT_NOT_FOUND", "The source PDF does not exist.");
        }

        if (input.Length > DocumentLimits.MaximumFileSizeBytes)
        {
            throw new PdfTableWorkerException(
                "PDF_INPUT_TOO_LARGE",
                "The source PDF exceeds the 100 MB safety limit.");
        }

        if (!PdfFileInspector.Inspect(inputPath).IsValid)
        {
            throw new PdfTableWorkerException(
                "PDF_STRUCTURE_INVALID",
                "The source PDF is incomplete or damaged.");
        }

        if (!string.Equals(Path.GetExtension(outputPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfTableWorkerException(
                "XLSX_OUTPUT_INVALID",
                "The PDF table worker output must use the .xlsx extension.");
        }

        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfTableWorkerException(
                "SOURCE_OVERWRITE_BLOCKED",
                "The output path cannot overwrite the source PDF.");
        }

        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new PdfTableWorkerException(
                "OUTPUT_ALREADY_EXISTS",
                "The output workbook already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    }

    private static List<WorkerNotice> BuildNotices(DocumentTableExtraction extraction)
    {
        var notices = new List<WorkerNotice>();
        if (extraction.OcrPageCount > 0)
        {
            notices.Add(new WorkerNotice(
                "OCR_CONTENT_REQUIRES_REVIEW",
                "Offline OCR was used. Review highlighted low-confidence cells before relying on the workbook.",
                "warning"));
        }

        if (extraction.LowConfidenceCellCount > 0)
        {
            notices.Add(new WorkerNotice(
                "LOW_CONFIDENCE_CELLS",
                "Low-confidence cells are highlighted in the output workbook.",
                "warning"));
        }

        if (extraction.Pages.SelectMany(static page => page.Warnings)
            .Concat(extraction.Tables.SelectMany(static table => table.Warnings))
            .Contains("TABLE_STRUCTURE_UNCERTAIN", StringComparer.Ordinal))
        {
            notices.Add(new WorkerNotice(
                "TABLE_STRUCTURE_UNCERTAIN",
                "At least one page was exported as low-confidence row text because stable columns were not detected.",
                "warning"));
        }

        return notices;
    }

    private static Dictionary<string, string> BuildMetrics(
        DocumentTableExtraction extraction,
        int tableSheetCount,
        long outputCellCount,
        string outputPath) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pageCount"] = extraction.PageCount.ToString(CultureInfo.InvariantCulture),
            ["digitalPageCount"] = extraction.DigitalPageCount.ToString(CultureInfo.InvariantCulture),
            ["ocrPageCount"] = extraction.OcrPageCount.ToString(CultureInfo.InvariantCulture),
            ["tableCount"] = extraction.Tables.Count.ToString(CultureInfo.InvariantCulture),
            ["worksheetCount"] = tableSheetCount.ToString(CultureInfo.InvariantCulture),
            ["lowConfidenceCellCount"] = extraction.LowConfidenceCellCount.ToString(CultureInfo.InvariantCulture),
            ["outputCellCount"] = outputCellCount.ToString(CultureInfo.InvariantCulture),
            ["outputBytes"] = new FileInfo(outputPath).Length.ToString(CultureInfo.InvariantCulture),
        };

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not replace the worker result.
        }
    }
}
