using System.Globalization;
using System.Security;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Pdf;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Runtime;
using DocPivot.Infrastructure.Storage;

namespace DocPivot.Infrastructure.Pdf.Ghostscript;

public sealed class GhostscriptPdfOptimizer : IGhostscriptPdfOptimizer
{
    public const string PinnedVersion = "10.07.1";

    public static readonly string RelativeExecutablePath = Path.Combine(
        "vendor",
        "ghostscript",
        PinnedVersion,
        "runtime",
        "bin",
        "gswin64c.exe");

    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(5);

    // Ghostscript's pdfwrite occasionally passes through non-embedded base fonts
    // (no ToUnicode CMap), which makes the searchable text layer unreadable after
    // compression. Retrying once is enough to get a properly re-encoded output.
    private const int MaximumCompressionAttempts = 2;

    private readonly IGhostscriptRuntimeProbe _probe;
    private readonly IGhostscriptProcessRunner _runner;
    private readonly IGhostscriptSearchableTextVerifier _textVerifier;
    private readonly TimeSpan _operationTimeout;

    public GhostscriptPdfOptimizer(string distributionRoot)
    {
        var runner = new GhostscriptProcessRunner();
        _probe = new GhostscriptRuntimeProbe(distributionRoot, runner, DefaultProbeTimeout);
        _runner = runner;
        _textVerifier = new GhostscriptSearchableTextVerifier(runner);
        _operationTimeout = DefaultOperationTimeout;
    }

    internal GhostscriptPdfOptimizer(
        IGhostscriptRuntimeProbe probe,
        IGhostscriptProcessRunner runner,
        IGhostscriptSearchableTextVerifier textVerifier,
        TimeSpan operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(textVerifier);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);

        _probe = probe;
        _runner = runner;
        _textVerifier = textVerifier;
        _operationTimeout = operationTimeout;
    }

    public Task<GhostscriptEngineStatus> ProbeAsync(
        CancellationToken cancellationToken = default) =>
        _probe.ProbeAsync(cancellationToken);

    public Task<OperationExecutionResult> OptimizeAsync(
        PdfOptimizeRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteGuardedAsync(() => OptimizeCoreAsync(request, cancellationToken));

    private async Task<OperationExecutionResult> OptimizeCoreAsync(
        PdfOptimizeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null)
        {
            return InvalidRequest("A PDF optimization request is required.");
        }

        if (request.CompressionStrength is < 1 or > 100)
        {
            return InvalidRequest(
                "Ghostscript lossy compression strength must be between 1 and 100.");
        }

        if (request.Password?.Length > 1024)
        {
            return InvalidRequest("The PDF password is too long.");
        }

        var inputValidation = ValidateInputPath(request.InputPath);
        if (!inputValidation.IsValid)
        {
            return inputValidation.Failure!;
        }

        var sourcePath = inputValidation.Path!;
        var outputValidation = ValidateOutputPath(request.OutputPath, sourcePath);
        if (!outputValidation.IsValid)
        {
            return outputValidation.Failure!;
        }

        var sourceInspection = PdfFileInspector.Inspect(sourcePath);
        if (!sourceInspection.IsValid)
        {
            return OperationExecutionResult.Failed(
                "PDF_INPUT_INVALID",
                "The input file is not a complete PDF.",
                false);
        }

        var engine = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!engine.IsReady)
        {
            return OperationExecutionResult.Failed(
                engine.ErrorCode ?? "GHOSTSCRIPT_UNAVAILABLE",
                engine.ErrorMessage ?? "The pinned Ghostscript runtime is unavailable.",
                engine.IsRetryable);
        }

        var outputPath = outputValidation.Path!;
        var profile = PdfCompressionProfile.FromStrength(request.CompressionStrength);
        var attempt = 0;
        while (true)
        {
            attempt++;
            var stagingPath = AtomicOutputFile.CreateStagingPath(outputPath, Guid.NewGuid());
            try
            {
                var result = await _runner.RunAsync(
                            engine.ExecutablePath!,
                            BuildArguments(sourcePath, stagingPath, profile, request.Password),
                            _operationTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    return OperationExecutionResult.Failed(
                        "GHOSTSCRIPT_OPTIMIZATION_FAILED",
                        GetFailureMessage(result),
                        false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var outputInspection = PdfFileInspector.Inspect(stagingPath);
                if (!outputInspection.IsValid)
                {
                    return OperationExecutionResult.Failed(
                        "PDF_OUTPUT_INVALID",
                        "Ghostscript produced an incomplete PDF output.",
                        false);
                }

                var originalBytes = new FileInfo(sourcePath).Length;
                var outputBytes = new FileInfo(stagingPath).Length;
                var optimizationApplied = outputBytes < originalBytes;
                var searchableTextStatus = "source-retained";
                var sourceTextCharacters = 0;
                var outputTextCharacters = 0;
                IReadOnlyList<WorkerNotice> notices = [];
                if (optimizationApplied)
                {
                    var textVerification = await _textVerifier.VerifyAsync(
                            engine.ExecutablePath!,
                            sourcePath,
                            stagingPath,
                            _operationTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!textVerification.IsSucceeded)
                    {
                        if (attempt < MaximumCompressionAttempts)
                        {
                            // The staged output failed the searchable-text gate; retry once
                            // with a fresh staging path before rejecting the operation.
                            continue;
                        }

                        return OperationExecutionResult.Failed(
                            textVerification.ErrorCode ?? "PDF_TEXT_VERIFICATION_FAILED",
                            textVerification.ErrorMessage ??
                                "The searchable text verification did not complete.",
                            false);
                    }

                    searchableTextStatus = textVerification.Status;
                    sourceTextCharacters = textVerification.SourceCharacterCount;
                    outputTextCharacters = textVerification.OutputCharacterCount;
                    notices =
                    [
                        new WorkerNotice(
                            "PDF_LOSSY_PRESERVATION_LIMITS",
                            "Page count and searchable text are verified. Comments, forms, and attachments are not guaranteed to survive PDF rewriting.",
                            "warning"),
                    ];
                }
                else
                {
                    File.Copy(sourcePath, stagingPath, overwrite: true);
                    outputBytes = originalBytes;
                    notices =
                    [
                        new WorkerNotice(
                            "PDF_NO_COMPRESSION_BENEFIT",
                            "Lossy optimization did not reduce the file, so the source content was retained.",
                            "info"),
                    ];
                }

                cancellationToken.ThrowIfCancellationRequested();
                AtomicOutputFile.Commit(stagingPath, outputPath);

                var savedBytes = originalBytes - outputBytes;
                var savedPercent = originalBytes == 0
                    ? 0
                    : savedBytes * 100d / originalBytes;
                return OperationExecutionResult.Succeeded(
                    [outputPath],
                    notices,
                    metrics: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["compressionStrength"] = profile.Strength.ToString(CultureInfo.InvariantCulture),
                        ["imageDpi"] = profile.ImageDpi!.Value.ToString(CultureInfo.InvariantCulture),
                        ["jpegQuality"] = profile.JpegQuality!.Value.ToString(CultureInfo.InvariantCulture),
                        ["impactLevel"] = profile.ImpactLevel.ToString().ToLowerInvariant(),
                        ["optimizationApplied"] = optimizationApplied ? "true" : "false",
                        ["searchableTextStatus"] = searchableTextStatus,
                        ["sourceTextCharacters"] = sourceTextCharacters.ToString(CultureInfo.InvariantCulture),
                        ["outputTextCharacters"] = outputTextCharacters.ToString(CultureInfo.InvariantCulture),
                        ["originalBytes"] = originalBytes.ToString(CultureInfo.InvariantCulture),
                        ["outputBytes"] = outputBytes.ToString(CultureInfo.InvariantCulture),
                        ["savedBytes"] = savedBytes.ToString(CultureInfo.InvariantCulture),
                        ["savedPercent"] = savedPercent.ToString("0.##", CultureInfo.InvariantCulture),
                    });
                }
            finally
            {
                DeleteFileIfExists(stagingPath);
            }
        }
    }

    private static List<string> BuildArguments(
        string sourcePath,
        string stagingPath,
        PdfCompressionProfile profile,
        string? password)
    {
        var imageDpi = profile.ImageDpi!.Value.ToString(CultureInfo.InvariantCulture);
        var monochromeDpi = Math.Max(profile.ImageDpi.Value, 300)
            .ToString(CultureInfo.InvariantCulture);
        var jpegQuality = profile.JpegQuality!.Value.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "-q",
            "-dSAFER",
            "-dBATCH",
            "-dNOPAUSE",
            "-dNOPROMPT",
            "-sDEVICE=pdfwrite",
            "-dCompatibilityLevel=1.7",
            "-dAutoRotatePages=/None",
            "-dPreserveAnnots=true",
            "-dPreserveMarkedContent=true",
            "-dPreserveHalftoneInfo=true",
            "-dPreserveOverprintSettings=true",
            "-dDetectDuplicateImages=true",
            "-dCompressFonts=true",
            "-dPassThroughFonts=false",
            "-dSubsetFonts=true",
            "-dEmbedAllFonts=true",
            "-dPassThroughJPEGImages=true",
            "-dAutoFilterColorImages=true",
            "-dDownsampleColorImages=true",
            "-dColorImageDownsampleType=/Bicubic",
            $"-dColorImageResolution={imageDpi}",
            "-dAutoFilterGrayImages=true",
            "-dDownsampleGrayImages=true",
            "-dGrayImageDownsampleType=/Bicubic",
            $"-dGrayImageResolution={imageDpi}",
            "-dDownsampleMonoImages=true",
            "-dMonoImageDownsampleType=/Subsample",
            $"-dMonoImageResolution={monochromeDpi}",
            $"-dJPEGQ={jpegQuality}",
            $"-sOutputFile={stagingPath}"
        };
        if (password is not null)
        {
            arguments.Add($"-sPDFPassword={password}");
        }

        arguments.Add("-f");
        arguments.Add(sourcePath);
        return arguments;
    }

    private static PathValidation ValidateInputPath(string inputPath)
    {
        string normalizedPath;
        try
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return PathValidation.Invalid(InvalidRequest("The PDF input path is required."));
            }

            normalizedPath = Path.GetFullPath(inputPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or SecurityException)
        {
            return PathValidation.Invalid(InvalidRequest("The PDF input path is invalid."));
        }

        if (!string.Equals(Path.GetExtension(normalizedPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PathValidation.Invalid(InvalidRequest("The PDF input path must use .pdf."));
        }

        return File.Exists(normalizedPath)
            ? PathValidation.Valid(normalizedPath)
            : PathValidation.Invalid(OperationExecutionResult.Failed(
                "PDF_INPUT_NOT_FOUND",
                "The PDF input file does not exist.",
                false));
    }

    private static PathValidation ValidateOutputPath(string outputPath, string sourcePath)
    {
        string normalizedPath;
        try
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return PathValidation.Invalid(InvalidRequest("The PDF output path is required."));
            }

            normalizedPath = Path.GetFullPath(outputPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or SecurityException)
        {
            return PathValidation.Invalid(InvalidRequest("The PDF output path is invalid."));
        }

        if (!string.Equals(Path.GetExtension(normalizedPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PathValidation.Invalid(InvalidRequest("The PDF output path must use .pdf."));
        }

        if (string.Equals(sourcePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return PathValidation.Invalid(
                InvalidRequest("The PDF output path must not overwrite the input file."));
        }

        return File.Exists(normalizedPath) || Directory.Exists(normalizedPath)
            ? PathValidation.Invalid(OperationExecutionResult.Failed(
                "PDF_OUTPUT_ALREADY_EXISTS",
                "The PDF output destination already exists.",
                false))
            : PathValidation.Valid(normalizedPath);
    }

    private static string GetFailureMessage(ExternalProcessResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        var firstLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "Ghostscript did not complete PDF optimization.";
        }

        const int maximumLength = 500;
        return firstLine.Length <= maximumLength ? firstLine : firstLine[..maximumLength];
    }

    private static OperationExecutionResult InvalidRequest(string message) =>
        OperationExecutionResult.Failed("PDF_REQUEST_INVALID", message, false);

    private static async Task<OperationExecutionResult> ExecuteGuardedAsync(
        Func<Task<OperationExecutionResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            return OperationExecutionResult.Failed(
                "GHOSTSCRIPT_TIMEOUT",
                exception.Message,
                true);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationExecutionResult.Failed(
                "PDF_ACCESS_DENIED",
                "Ghostscript could not access an input or output path.",
                false);
        }
        catch (SecurityException)
        {
            return OperationExecutionResult.Failed(
                "PDF_ACCESS_DENIED",
                "The Ghostscript operation was blocked by the operating system.",
                false);
        }
        catch (IOException)
        {
            return OperationExecutionResult.Failed(
                "PDF_IO_FAILURE",
                "Ghostscript failed while reading or writing a PDF file.",
                true);
        }
        catch (InvalidOperationException)
        {
            return OperationExecutionResult.Failed(
                "GHOSTSCRIPT_EXECUTION_FAILED",
                "The Ghostscript process could not complete the operation.",
                true);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Best-effort cleanup must not replace the operation result or cancellation.
        }
    }

    private sealed record PathValidation(
        bool IsValid,
        string? Path,
        OperationExecutionResult? Failure)
    {
        public static PathValidation Valid(string path) => new(true, path, null);

        public static PathValidation Invalid(OperationExecutionResult failure) =>
            new(false, null, failure);
    }
}
