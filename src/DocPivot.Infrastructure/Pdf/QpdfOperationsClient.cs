using System.Globalization;
using System.Security;
using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Runtime;
using DocPivot.Infrastructure.Storage;

namespace DocPivot.Infrastructure.Pdf;

public sealed class QpdfOperationsClient : IPdfOperationsClient
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IQpdfToolProvider _toolProvider;
    private readonly IQpdfCliRunner _runner;
    private readonly TimeSpan _operationTimeout;

    public QpdfOperationsClient(
        string distributionRoot,
        TimeSpan? operationTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionRoot);
        var timeout = operationTimeout ?? DefaultOperationTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var runner = new QpdfCliRunner();
        _runner = runner;
        _toolProvider = new QpdfToolProbe(distributionRoot, runner, DefaultProbeTimeout);
        _operationTimeout = timeout;
    }

    internal QpdfOperationsClient(
        IQpdfToolProvider toolProvider,
        IQpdfCliRunner runner,
        TimeSpan operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(toolProvider);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);

        _toolProvider = toolProvider;
        _runner = runner;
        _operationTimeout = operationTimeout;
    }

    public async Task<PdfPreflightResult> PreflightAsync(
        PdfPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var tool = await _toolProvider.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!tool.IsReady || tool.ExecutablePath is null)
            {
                return PdfPreflightResult.Failed(
                    request.InputPath,
                    tool.ErrorCode ?? "QPDF_UNAVAILABLE",
                    tool.ErrorMessage ?? "The pinned qpdf runtime is unavailable.",
                    tool.IsRetryable);
            }

            return await InspectPdfAsync(
                    tool.ExecutablePath,
                    request.InputPath,
                    request.Password,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            return PdfPreflightResult.Failed(
                request.InputPath,
                "QPDF_TIMEOUT",
                exception.Message,
                isRetryable: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return PdfPreflightResult.Failed(
                request.InputPath,
                "PDF_PREFLIGHT_IO_FAILURE",
                "The PDF could not be read during preflight.",
                isRetryable: exception is IOException);
        }
    }

    public Task<OperationExecutionResult> MergeAsync(
        PdfMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteGuardedAsync(
            () => MergeCoreAsync(request, cancellationToken));
    }

    public Task<OperationExecutionResult> SplitAsync(
        PdfSplitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteGuardedAsync(
            () => SplitCoreAsync(request, cancellationToken));
    }

    public Task<OperationExecutionResult> OptimizeAsync(
        PdfOptimizeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteGuardedAsync(
            () => OptimizeCoreAsync(request, cancellationToken));
    }

    private async Task<OperationExecutionResult> MergeCoreAsync(
        PdfMergeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InputPaths is null || request.InputPaths.Count < 2)
        {
            return InvalidRequest("PDF merge requires at least two input files.");
        }

        if (request.InputPaths.Count > DocumentLimits.MaximumBatchFiles)
        {
            return InvalidRequest(
                $"PDF merge accepts at most {DocumentLimits.MaximumBatchFiles} input files.");
        }

        var tool = await _toolProvider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var unavailable = ToolUnavailable(tool);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var executablePath = tool.ExecutablePath!;
        var normalizedInputs = new List<(string Path, bool IsEncrypted, int Rotation)>(request.InputPaths.Count);
        var rotatedStagingPaths = new List<string>();
        var originalInputPaths = request.InputPaths.Select(Path.GetFullPath).ToArray();
        var totalPages = 0;
        for (var inputIndex = 0; inputIndex < request.InputPaths.Count; inputIndex++)
        {
            var inputPath = request.InputPaths[inputIndex];
            var preflight = await InspectPdfAsync(
                    executablePath,
                    inputPath,
                    request.Password,
                    cancellationToken)
                .ConfigureAwait(false);
            var failure = PreflightFailure(preflight);
            if (failure is not null)
            {
                foreach (var path in rotatedStagingPaths) DeleteFileIfExists(path);
                return failure;
            }

            var rotation = NormalizeRotation(request.Rotations, inputIndex);
            var sourcePath = preflight.InputPath!;
            if (rotation != 0)
            {
                var rotatedPath = AtomicOutputFile.CreateStagingPath(
                    Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(sourcePath)),
                    Guid.NewGuid());
                var rotateArguments = new List<string>();
                AddPasswordArgument(rotateArguments, request.Password, preflight.IsEncrypted);
                rotateArguments.Add("--decrypt");
                rotateArguments.Add($"--rotate=+{rotation.ToString(CultureInfo.InvariantCulture)}");
                rotateArguments.Add(sourcePath);
                rotateArguments.Add(rotatedPath);
                var rotateResult = await _runner.RunAsync(
                        executablePath,
                        rotateArguments,
                        _operationTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                var rotateFailure = QpdfCommandFailure(rotateResult, "PDF_ROTATION_FAILED");
                if (rotateFailure is not null)
                {
                    foreach (var path in rotatedStagingPaths) DeleteFileIfExists(path);
                    DeleteFileIfExists(rotatedPath);
                    return rotateFailure;
                }

                rotatedStagingPaths.Add(rotatedPath);
                sourcePath = rotatedPath;
            }

            normalizedInputs.Add((sourcePath, rotation == 0 && preflight.IsEncrypted, rotation));
            totalPages += preflight.PageCount!.Value;
            if (totalPages > DocumentLimits.MaximumPdfPages)
            {
                foreach (var path in rotatedStagingPaths) DeleteFileIfExists(path);
                return OperationExecutionResult.Failed(
                    "PDF_PAGE_LIMIT_EXCEEDED",
                    $"The merged PDF would exceed {DocumentLimits.MaximumPdfPages} pages.",
                    false);
            }
        }

        try
        {
            var outputValidation = ValidateOutputPath(
                request.OutputPath,
                originalInputPaths);
            if (!outputValidation.IsValid)
            {
                return outputValidation.Failure!;
            }

            var outputPath = outputValidation.Path!;
            var stagingPath = AtomicOutputFile.CreateStagingPath(outputPath, Guid.NewGuid());
            try
            {
                var arguments = new List<string>(6 + normalizedInputs.Count * 3);
                AddPasswordArgument(arguments, request.Password, normalizedInputs[0].IsEncrypted);
                arguments.Add(normalizedInputs[0].Path);
                arguments.Add("--decrypt");
                arguments.Add("--pages");
                arguments.Add(".");
                arguments.Add("1-z");
                foreach (var input in normalizedInputs.Skip(1))
                {
                    AddPasswordArgument(arguments, request.Password, input.IsEncrypted);
                    arguments.Add(input.Path);
                    arguments.Add("1-z");
                }

                arguments.Add("--");
                arguments.Add(stagingPath);
                var runResult = await _runner.RunAsync(
                        executablePath,
                        arguments,
                        _operationTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                var commandFailure = QpdfCommandFailure(runResult, "PDF_MERGE_FAILED");
                if (commandFailure is not null)
                {
                    return commandFailure;
                }

                var outputPreflight = await InspectPdfAsync(
                        executablePath,
                        stagingPath,
                        password: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                var outputFailure = ValidateGeneratedOutput(outputPreflight, totalPages);
                if (outputFailure is not null)
                {
                    return outputFailure;
                }

                AtomicOutputFile.Commit(stagingPath, outputPath);
                return OperationExecutionResult.Succeeded(
                [outputPath],
                notices:
                [
                    new WorkerNotice(
                        "PDF_MERGE_PRIMARY_DOCUMENT_INFO",
                        "Document-level information is retained from the first input; later inputs contribute pages only.",
                        "info"),
                ],
                metrics: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["inputCount"] = normalizedInputs.Count.ToString(CultureInfo.InvariantCulture),
                    ["pageCount"] = totalPages.ToString(CultureInfo.InvariantCulture),
                });
            }
            finally
            {
                DeleteFileIfExists(stagingPath);
            }
        }
        finally
        {
            foreach (var path in rotatedStagingPaths)
            {
                DeleteFileIfExists(path);
            }
        }
    }

    private async Task<OperationExecutionResult> SplitCoreAsync(
        PdfSplitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Selection);
        var tool = await _toolProvider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var unavailable = ToolUnavailable(tool);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var executablePath = tool.ExecutablePath!;
        var sourcePreflight = await InspectPdfAsync(
                executablePath,
                request.InputPath,
                request.Password,
                cancellationToken)
            .ConfigureAwait(false);
        var sourceFailure = PreflightFailure(sourcePreflight);
        if (sourceFailure is not null)
        {
            return sourceFailure;
        }

        var sourcePath = sourcePreflight.InputPath!;
        var baseOutputValidation = ValidateOutputPath(request.OutputPath, [sourcePath]);
        if (!baseOutputValidation.IsValid)
        {
            return baseOutputValidation.Failure!;
        }

        PdfSplitPlan plan;
        try
        {
            plan = PdfSplitPlanner.Create(
                baseOutputValidation.Path!,
                request.Selection,
                sourcePreflight.PageCount!.Value);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return InvalidRequest(exception.Message);
        }

        foreach (var artifact in plan.Artifacts)
        {
            var destinationValidation = ValidateOutputPath(artifact.DestinationPath, [sourcePath]);
            if (!destinationValidation.IsValid)
            {
                return destinationValidation.Failure!;
            }
        }

        var jobId = Guid.NewGuid();
        var stagingOutputs = new List<(string StagingPath, string DestinationPath)>(plan.Artifacts.Count);
        try
        {
            if (plan.UsesNativeSplit)
            {
                var outputDirectory = Path.GetDirectoryName(baseOutputValidation.Path!)!;
                Directory.CreateDirectory(outputDirectory);
                var stagingPattern = Path.Combine(
                    outputDirectory,
                    $".docpivot-{jobId:N}-%d.tmp.pdf");
                foreach (var artifact in plan.Artifacts)
                {
                    var nativeRangeLabel = plan.NativePagesPerFile == 1 ||
                        artifact.PageExpression.Contains('-', StringComparison.Ordinal)
                            ? artifact.PageExpression
                            : $"{artifact.PageExpression}-{artifact.PageExpression}";
                    stagingOutputs.Add((
                        stagingPattern.Replace("%d", nativeRangeLabel, StringComparison.Ordinal),
                        artifact.DestinationPath));
                }

                var arguments = new List<string>();
                AddPasswordArgument(arguments, request.Password, sourcePreflight.IsEncrypted);
                arguments.Add(sourcePath);
                arguments.Add("--decrypt");
                arguments.Add($"--split-pages={plan.NativePagesPerFile.ToString(CultureInfo.InvariantCulture)}");
                arguments.Add(stagingPattern);
                var runResult = await _runner.RunAsync(
                        executablePath,
                        arguments,
                        _operationTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                var commandFailure = QpdfCommandFailure(runResult, "PDF_SPLIT_FAILED");
                if (commandFailure is not null)
                {
                    return commandFailure;
                }
            }
            else
            {
                foreach (var artifact in plan.Artifacts)
                {
                    var stagingPath = AtomicOutputFile.CreateStagingPath(
                        artifact.DestinationPath,
                        Guid.NewGuid());
                    stagingOutputs.Add((stagingPath, artifact.DestinationPath));
                    var arguments = new List<string>();
                    AddPasswordArgument(arguments, request.Password, sourcePreflight.IsEncrypted);
                    arguments.Add(sourcePath);
                    arguments.Add("--decrypt");
                    arguments.Add("--pages");
                    arguments.Add(".");
                    arguments.Add(artifact.PageExpression);
                    arguments.Add("--");
                    arguments.Add(stagingPath);
                    var runResult = await _runner.RunAsync(
                            executablePath,
                            arguments,
                            _operationTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var commandFailure = QpdfCommandFailure(runResult, "PDF_SPLIT_FAILED");
                    if (commandFailure is not null)
                    {
                        return commandFailure;
                    }
                }
            }

            for (var index = 0; index < plan.Artifacts.Count; index++)
            {
                var outputPreflight = await InspectPdfAsync(
                        executablePath,
                        stagingOutputs[index].StagingPath,
                        password: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                var outputFailure = ValidateGeneratedOutput(
                    outputPreflight,
                    plan.Artifacts[index].ExpectedPageCount);
                if (outputFailure is not null)
                {
                    return outputFailure;
                }
            }

            var committed = AtomicOutputBatch.Commit(stagingOutputs);
            return OperationExecutionResult.Succeeded(
                committed,
                metrics: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["artifactCount"] = committed.Count.ToString(CultureInfo.InvariantCulture),
                    ["sourcePageCount"] = sourcePreflight.PageCount.Value.ToString(CultureInfo.InvariantCulture),
                    ["splitMode"] = request.Selection.Mode.ToString(),
                });
        }
        finally
        {
            foreach (var output in stagingOutputs)
            {
                DeleteFileIfExists(output.StagingPath);
            }
        }
    }

    private async Task<OperationExecutionResult> OptimizeCoreAsync(
        PdfOptimizeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CompressionStrength != 0)
        {
            return OperationExecutionResult.Failed(
                "PDF_LOSSY_COMPRESSION_UNAVAILABLE",
                "Only 0% lossless qpdf optimization is available.",
                false);
        }

        var tool = await _toolProvider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var unavailable = ToolUnavailable(tool);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var executablePath = tool.ExecutablePath!;
        var sourcePreflight = await InspectPdfAsync(
                executablePath,
                request.InputPath,
                request.Password,
                cancellationToken)
            .ConfigureAwait(false);
        var sourceFailure = PreflightFailure(sourcePreflight);
        if (sourceFailure is not null)
        {
            return sourceFailure;
        }

        // Signed PDFs are compressed on the working copy; the output signature
        // becomes invalid but the source file is untouched (force compression).
        var sourcePath = sourcePreflight.InputPath!;
        var outputValidation = ValidateOutputPath(request.OutputPath, [sourcePath]);
        if (!outputValidation.IsValid)
        {
            return outputValidation.Failure!;
        }

        var outputPath = outputValidation.Path!;
        var stagingPath = AtomicOutputFile.CreateStagingPath(outputPath, Guid.NewGuid());
        try
        {
            var arguments = new List<string>();
            AddPasswordArgument(arguments, request.Password, sourcePreflight.IsEncrypted);
            arguments.Add(sourcePath);
            arguments.Add("--decrypt");
            arguments.Add("--object-streams=generate");
            arguments.Add("--compress-streams=y");
            arguments.Add("--recompress-flate");
            arguments.Add("--compression-level=9");
            arguments.Add(stagingPath);
            var runResult = await _runner.RunAsync(
                    executablePath,
                    arguments,
                    _operationTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var commandFailure = QpdfCommandFailure(runResult, "PDF_OPTIMIZATION_FAILED");
            if (commandFailure is not null)
            {
                return commandFailure;
            }

            var outputPreflight = await InspectPdfAsync(
                    executablePath,
                    stagingPath,
                    password: null,
                    cancellationToken)
                .ConfigureAwait(false);
            var outputFailure = ValidateGeneratedOutput(
                outputPreflight,
                sourcePreflight.PageCount!.Value);
            if (outputFailure is not null)
            {
                return outputFailure;
            }

            var originalBytes = new FileInfo(sourcePath).Length;
            var optimizedBytes = new FileInfo(stagingPath).Length;
            var optimizationApplied = optimizedBytes < originalBytes;
            IReadOnlyList<WorkerNotice> notices = [];
            if (!optimizationApplied && !sourcePreflight.IsEncrypted)
            {
                File.Copy(sourcePath, stagingPath, overwrite: true);
                optimizedBytes = originalBytes;
                notices =
                [
                    new WorkerNotice(
                        "PDF_NO_COMPRESSION_BENEFIT",
                        "Lossless optimization did not reduce the file, so the source content was retained.",
                        "info"),
                ];
            }
            else if (!optimizationApplied)
            {
                notices =
                [
                    new WorkerNotice(
                        "PDF_DECRYPTED_WITHOUT_COMPRESSION",
                        "The PDF was unlocked successfully; lossless rewriting did not reduce its size.",
                        "info"),
                ];
            }

            AtomicOutputFile.Commit(stagingPath, outputPath);
            var savedBytes = originalBytes - optimizedBytes;
            var savedPercent = originalBytes == 0
                ? 0
                : savedBytes * 100d / originalBytes;
            return OperationExecutionResult.Succeeded(
                [outputPath],
                notices,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["compressionStrength"] = "0",
                    ["optimizationApplied"] = optimizationApplied ? "true" : "false",
                    ["originalBytes"] = originalBytes.ToString(CultureInfo.InvariantCulture),
                    ["outputBytes"] = optimizedBytes.ToString(CultureInfo.InvariantCulture),
                    ["savedBytes"] = savedBytes.ToString(CultureInfo.InvariantCulture),
                    ["savedPercent"] = savedPercent.ToString("0.##", CultureInfo.InvariantCulture),
                });
        }
        finally
        {
            DeleteFileIfExists(stagingPath);
        }
    }

    private async Task<PdfPreflightResult> InspectPdfAsync(
        string executablePath,
        string inputPath,
        string? password,
        CancellationToken cancellationToken)
    {
        string normalizedInput;
        try
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return PdfPreflightResult.Failed(
                    inputPath,
                    "PDF_INPUT_INVALID",
                    "The PDF input path is required.",
                    false);
            }

            normalizedInput = Path.GetFullPath(inputPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or SecurityException)
        {
            return PdfPreflightResult.Failed(
                inputPath,
                "PDF_INPUT_INVALID",
                "The PDF input path is invalid.",
                false);
        }

        if (!string.Equals(Path.GetExtension(normalizedInput), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_INPUT_EXTENSION_INVALID",
                "The input file must use the .pdf extension.",
                false);
        }

        var file = new FileInfo(normalizedInput);
        if (!file.Exists)
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_INPUT_NOT_FOUND",
                "The PDF input file does not exist.",
                false);
        }

        if (file.Length > DocumentLimits.MaximumFileSizeBytes)
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_INPUT_TOO_LARGE",
                "The PDF input exceeds the 100 MB limit.",
                false);
        }

        if (password?.Length > 1024)
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_PASSWORD_INVALID",
                "The PDF password is too long.",
                false,
                isEncrypted: true);
        }

        var encryptedResult = await _runner.RunAsync(
                executablePath,
                ["--is-encrypted", normalizedInput],
                _operationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var isEncrypted = encryptedResult.ExitCode == 0;
        if (!isEncrypted &&
            (encryptedResult.ExitCode != 2 || !string.IsNullOrWhiteSpace(encryptedResult.StandardError)))
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_PREFLIGHT_FAILED",
                GetQpdfFailureMessage(encryptedResult, "qpdf could not inspect PDF encryption."),
                false);
        }

        var checkArguments = new List<string>();
        AddPasswordArgument(checkArguments, password, isEncrypted);
        checkArguments.Add("--check");
        checkArguments.Add(normalizedInput);
        var checkResult = await _runner.RunAsync(
                executablePath,
                checkArguments,
                _operationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (checkResult.ExitCode != 0)
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                isEncrypted
                    ? password is null ? "PDF_PASSWORD_REQUIRED" : "PDF_PASSWORD_INVALID"
                    : "PDF_STRUCTURE_INVALID",
                isEncrypted
                    ? password is null
                        ? "The PDF requires a password."
                        : "The PDF password is incorrect."
                    : GetQpdfFailureMessage(checkResult, "qpdf reported a structural PDF error."),
                false,
                isEncrypted);
        }

        var jsonArguments = new List<string>();
        AddPasswordArgument(jsonArguments, password, isEncrypted);
        jsonArguments.Add("--json");
        jsonArguments.Add("--json-key=pages");
        jsonArguments.Add("--json-key=encrypt");
        jsonArguments.Add("--json-key=acroform");
        jsonArguments.Add(normalizedInput);
        var jsonResult = await _runner.RunAsync(
                executablePath,
                jsonArguments,
                _operationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (jsonResult.ExitCode != 0)
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_METADATA_INSPECTION_FAILED",
                GetQpdfFailureMessage(jsonResult, "qpdf could not read structured PDF metadata."),
                false);
        }

        QpdfJsonInspection inspection;
        try
        {
            inspection = QpdfJsonInspector.Parse(jsonResult.StandardOutput);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_METADATA_INVALID",
                "qpdf returned an unsupported metadata response.",
                false);
        }

        if (inspection.PageCount < 1)
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_HAS_NO_PAGES",
                "The PDF does not contain any pages.",
                false);
        }

        if (inspection.PageCount > DocumentLimits.MaximumPdfPages)
        {
            return PdfPreflightResult.Failed(
                normalizedInput,
                "PDF_PAGE_LIMIT_EXCEEDED",
                $"The PDF exceeds the {DocumentLimits.MaximumPdfPages} page limit.",
                false);
        }

        return PdfPreflightResult.Succeeded(
            normalizedInput,
            inspection.PageCount,
            inspection.HasSignatureFields,
            isEncrypted || inspection.IsEncrypted);
    }

    private static void AddPasswordArgument(
        List<string> arguments,
        string? password,
        bool isEncrypted)
    {
        if (isEncrypted)
        {
            arguments.Add($"--password={password ?? string.Empty}");
        }
    }

    private static OperationExecutionResult? ToolUnavailable(QpdfToolStatus tool) =>
        tool.IsReady && tool.ExecutablePath is not null
            ? null
            : OperationExecutionResult.Failed(
                tool.ErrorCode ?? "QPDF_UNAVAILABLE",
                tool.ErrorMessage ?? "The pinned qpdf runtime is unavailable.",
                tool.IsRetryable);

    private static OperationExecutionResult? PreflightFailure(PdfPreflightResult preflight) =>
        preflight.IsSucceeded
            ? null
            : OperationExecutionResult.Failed(
                preflight.ErrorCode ?? "PDF_PREFLIGHT_FAILED",
                preflight.ErrorMessage ?? "PDF preflight failed.",
                preflight.IsRetryable);

    private static OperationExecutionResult? ValidateGeneratedOutput(
        PdfPreflightResult preflight,
        int expectedPageCount)
    {
        var failure = PreflightFailure(preflight);
        if (failure is not null)
        {
            return OperationExecutionResult.Failed(
                "PDF_OUTPUT_INVALID",
                preflight.ErrorMessage ?? "qpdf produced an invalid PDF output.",
                false);
        }

        return preflight.PageCount == expectedPageCount
            ? null
            : OperationExecutionResult.Failed(
                "PDF_OUTPUT_PAGE_COUNT_MISMATCH",
                $"The generated PDF has {preflight.PageCount} pages; expected {expectedPageCount}.",
                false);
    }

    private static OperationExecutionResult? QpdfCommandFailure(
        ExternalProcessResult result,
        string errorCode) =>
        result.ExitCode == 0
            ? null
            : OperationExecutionResult.Failed(
                errorCode,
                GetQpdfFailureMessage(result, "qpdf did not complete the requested operation."),
                false);

    private static string GetQpdfFailureMessage(
        ExternalProcessResult result,
        string fallback)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        var firstLine = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return fallback;
        }

        const int maximumLength = 500;
        return firstLine.Length <= maximumLength ? firstLine : firstLine[..maximumLength];
    }

    private static OutputPathValidation ValidateOutputPath(
        string outputPath,
        IReadOnlyList<string> normalizedInputPaths)
    {
        string normalizedOutput;
        try
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return OutputPathValidation.Invalid(InvalidRequest("The PDF output path is required."));
            }

            normalizedOutput = Path.GetFullPath(outputPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or SecurityException)
        {
            return OutputPathValidation.Invalid(InvalidRequest("The PDF output path is invalid."));
        }

        if (!string.Equals(Path.GetExtension(normalizedOutput), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return OutputPathValidation.Invalid(
                InvalidRequest("The PDF output path must use the .pdf extension."));
        }

        if (normalizedInputPaths.Any(inputPath =>
                string.Equals(inputPath, normalizedOutput, StringComparison.OrdinalIgnoreCase)))
        {
            return OutputPathValidation.Invalid(
                InvalidRequest("The PDF output path must not overwrite an input file."));
        }

        if (File.Exists(normalizedOutput) || Directory.Exists(normalizedOutput))
        {
            return OutputPathValidation.Invalid(OperationExecutionResult.Failed(
                "PDF_OUTPUT_ALREADY_EXISTS",
                "The PDF output destination already exists.",
                false));
        }

        return OutputPathValidation.Valid(normalizedOutput);
    }

    private static OperationExecutionResult InvalidRequest(string message) =>
        OperationExecutionResult.Failed("PDF_REQUEST_INVALID", message, false);

    private static int NormalizeRotation(IReadOnlyList<int>? rotations, int index)
    {
        if (rotations is null || index >= rotations.Count)
        {
            return 0;
        }

        var value = rotations[index] % 360;
        if (value < 0) value += 360;
        return value is 90 or 180 or 270 ? value : 0;
    }

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
            return OperationExecutionResult.Failed("QPDF_TIMEOUT", exception.Message, true);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationExecutionResult.Failed(
                "PDF_ACCESS_DENIED",
                "The PDF operation could not access an input or output path.",
                false);
        }
        catch (SecurityException)
        {
            return OperationExecutionResult.Failed(
                "PDF_ACCESS_DENIED",
                "The PDF operation was blocked by the operating system.",
                false);
        }
        catch (IOException)
        {
            return OperationExecutionResult.Failed(
                "PDF_IO_FAILURE",
                "The PDF operation failed while reading or writing a file.",
                true);
        }
        catch (InvalidOperationException)
        {
            return OperationExecutionResult.Failed(
                "QPDF_EXECUTION_FAILED",
                "The qpdf process could not complete the operation.",
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

    private sealed record OutputPathValidation(
        bool IsValid,
        string? Path,
        OperationExecutionResult? Failure)
    {
        public static OutputPathValidation Valid(string path) => new(true, path, null);

        public static OutputPathValidation Invalid(OperationExecutionResult failure) =>
            new(false, null, failure);
    }
}
