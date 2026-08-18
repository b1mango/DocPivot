using System.Text;
using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;

namespace DocPivot.PdfWorker;

public static class Program
{
    private const int MaximumMessageCharacters = 64 * 1024;
    private const int DefaultTimeoutSeconds = 600;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [STAThread]
    public static int Main(string[] args) => Run(args);

    public static int Run(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            var options = ParseArguments(args);
            return options.Mode switch
            {
                WorkerMode.Probe => ProbeAsync(options.DistributionRoot).GetAwaiter().GetResult(),
                WorkerMode.Execute => ExecuteAsync(options).GetAwaiter().GetResult(),
                _ => 64,
            };
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 64;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("The PDF table worker terminated unexpectedly.");
            return 70;
        }
    }

    private static async Task<int> ProbeAsync(string distributionRoot)
    {
        distributionRoot = PdfRuntimeDistributionRoot.Resolve(distributionRoot);
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["digitalText"] = true,
            ["xlsx"] = true,
            ["localOcr"] = false,
        };
        string? errorCode = null;
        string? errorMessage = null;
        try
        {
            var ghostscript = await new GhostscriptPdfOptimizer(distributionRoot).ProbeAsync()
                .ConfigureAwait(false);
            if (!ghostscript.IsReady)
            {
                errorCode = ghostscript.ErrorCode;
                errorMessage = ghostscript.ErrorMessage;
            }
            else
            {
                var tessdata = await new EmbeddedTessdataStore()
                    .EnsureAvailableAsync("chi_sim+eng")
                    .ConfigureAwait(false);
                using var provider = new TesseractOcrProvider(tessdata, "chi_sim+eng");
                capabilities["localOcr"] = true;
            }
        }
        catch (PdfTableWorkerException exception)
        {
            errorCode = exception.ErrorCode;
            errorMessage = exception.Message;
        }
        catch (Exception)
        {
            errorCode = "OCR_ENGINE_UNAVAILABLE";
            errorMessage = "The offline OCR engine could not be initialized.";
        }

        var ready = capabilities.Values.All(static value => value);
        WriteMessage(new WorkerPdfTableProbeResult(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.ProbeResult,
            "pdf-table",
            ready ? "ready" : "degraded",
            capabilities,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pdfPigVersion"] = "0.1.15",
                ["tesseractWrapperVersion"] = "5.2.0",
                ["tessdataRevision"] = EmbeddedTessdataStore.ModelRevision,
                ["openXmlVersion"] = "3.5.1",
            },
            errorCode,
            errorMessage));
        return ready ? 0 : 2;
    }

    private static async Task<int> ExecuteAsync(WorkerArguments options)
    {
        var line = Console.In.ReadLine()?.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumMessageCharacters)
        {
            WriteMessage(WorkerErrorMessage.Create(
                Guid.Empty,
                "START_MESSAGE_INVALID",
                "A single bounded start message is required.",
                false));
            return 65;
        }

        WorkerPdfToExcelStartMessage request;
        try
        {
            request = JsonSerializer.Deserialize<WorkerPdfToExcelStartMessage>(line, JsonOptions)
                ?? throw new JsonException("Start message was empty.");
        }
        catch (JsonException)
        {
            WriteMessage(WorkerErrorMessage.Create(
                Guid.Empty,
                "START_MESSAGE_JSON_INVALID",
                "The start message is not valid JSON.",
                false));
            return 65;
        }

        using var timeout = new CancellationTokenSource(options.Timeout);
        try
        {
            var result = await new PdfTableWorkerRunner(options.DistributionRoot)
                .RunAsync(request, WriteMessage, timeout.Token)
                .ConfigureAwait(false);
            WriteMessage(result);
            return 0;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            WriteMessage(WorkerErrorMessage.Create(
                request.JobId,
                "PDF_TABLE_TIMEOUT",
                "PDF table extraction exceeded the configured timeout.",
                true));
            return 124;
        }
        catch (PdfTableWorkerException exception)
        {
            WriteMessage(WorkerErrorMessage.Create(
                request.JobId,
                exception.ErrorCode,
                exception.Message,
                exception.IsRetryable));
            return exception.ErrorCode.StartsWith("START_MESSAGE_", StringComparison.Ordinal) ? 65 : 70;
        }
        catch (UnauthorizedAccessException)
        {
            WriteMessage(WorkerErrorMessage.Create(
                request.JobId,
                "PDF_TABLE_ACCESS_DENIED",
                "The PDF table worker could not access an input, model, or output path.",
                false));
            return 70;
        }
        catch (IOException)
        {
            WriteMessage(WorkerErrorMessage.Create(
                request.JobId,
                "PDF_TABLE_IO_FAILURE",
                "The PDF table worker failed while reading or writing a file.",
                true));
            return 70;
        }
        catch (Exception)
        {
            WriteMessage(WorkerErrorMessage.Create(
                request.JobId,
                "PDF_TABLE_EXTRACTION_FAILED",
                "The PDF table worker could not complete extraction.",
                false));
            return 70;
        }
    }

    private static WorkerArguments ParseArguments(string[] args)
    {
        if (args.Length < 3 ||
            (!string.Equals(args[0], "--probe", StringComparison.Ordinal) &&
             !string.Equals(args[0], "--execute", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Usage: DocPivot.PdfWorker --probe|--execute --distribution-root <path> [--timeout-seconds 10..900]");
        }

        var mode = string.Equals(args[0], "--probe", StringComparison.Ordinal)
            ? WorkerMode.Probe
            : WorkerMode.Execute;
        string? distributionRoot = null;
        var timeoutSeconds = DefaultTimeoutSeconds;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException("Worker arguments must be supplied as name/value pairs.");
            }

            if (string.Equals(args[index], "--distribution-root", StringComparison.Ordinal))
            {
                distributionRoot = args[index + 1];
            }
            else if (string.Equals(args[index], "--timeout-seconds", StringComparison.Ordinal) &&
                int.TryParse(args[index + 1], out var seconds) &&
                seconds is >= 10 and <= 900)
            {
                timeoutSeconds = seconds;
            }
            else
            {
                throw new ArgumentException("PDF table worker arguments are invalid.");
            }
        }

        if (string.IsNullOrWhiteSpace(distributionRoot))
        {
            throw new ArgumentException("--distribution-root is required.");
        }

        return new WorkerArguments(
            mode,
            Path.GetFullPath(distributionRoot),
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static void WriteMessage(object message)
    {
        Console.WriteLine(JsonSerializer.Serialize(message, JsonOptions));
        Console.Out.Flush();
    }

    private enum WorkerMode
    {
        Probe,
        Execute,
    }

    private sealed record WorkerArguments(
        WorkerMode Mode,
        string DistributionRoot,
        TimeSpan Timeout);
}
