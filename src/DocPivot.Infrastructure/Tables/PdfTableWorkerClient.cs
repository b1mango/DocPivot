using System.Globalization;
using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tables;

public sealed class PdfTableWorkerClient : IPdfTableOperationsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultWorkerTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DecryptTimeout = TimeSpan.FromMinutes(2);

    private readonly string _executablePath;
    private readonly string _distributionRoot;
    private readonly string[] _argumentPrefix;
    private readonly TimeSpan _workerTimeout;
    private readonly TimeSpan _parentTimeout;
    private readonly string _qpdfExecutablePath;
    private readonly QpdfCliRunner _qpdfRunner = new();

    public PdfTableWorkerClient(
        string executablePath,
        string distributionRoot,
        IReadOnlyList<string>? argumentPrefix = null,
        TimeSpan? workerTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionRoot);
        _executablePath = Path.GetFullPath(executablePath);
        // The single-file host may extract bundled runtimes under AppContext.BaseDirectory.
        _distributionRoot = PdfRuntimeDistributionRoot.Resolve(distributionRoot);
        _qpdfExecutablePath = Path.Combine(
            _distributionRoot,
            QpdfToolProbe.RelativeExecutablePath);
        _argumentPrefix = argumentPrefix?.ToArray() ?? [];
        _workerTimeout = workerTimeout ?? DefaultWorkerTimeout;
        if (_workerTimeout < TimeSpan.FromSeconds(10) || _workerTimeout > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerTimeout),
                "PDF table worker timeout must be between 10 and 900 seconds.");
        }

        _parentTimeout = _workerTimeout + TimeSpan.FromSeconds(15);
    }

    public async Task<PdfTableEngineStatus> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await ExternalProcessRunner.RunAsync(
                _executablePath,
                BuildArguments(
                    "--probe",
                    "--distribution-root",
                    _distributionRoot),
                string.Empty,
                ProbeTimeout,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var probe = ReadMessages<WorkerPdfTableProbeResult>(result.StandardOutput)
            .LastOrDefault(static message =>
                message.V == WorkerProtocol.CurrentVersion &&
                message.Type == WorkerMessageTypes.ProbeResult &&
                string.Equals(message.Worker, "pdf-table", StringComparison.Ordinal));
        if (probe is null)
        {
            return PdfTableEngineStatus.Unavailable(
                "PDF_TABLE_WORKER_UNAVAILABLE",
                "The PDF table worker did not return a valid probe result.");
        }

        return new PdfTableEngineStatus(
            probe.Capabilities.TryGetValue("digitalText", out var digitalText) && digitalText,
            probe.Capabilities.TryGetValue("localOcr", out var localOcr) && localOcr,
            probe.Capabilities.TryGetValue("xlsx", out var xlsx) && xlsx,
            probe.ErrorCode,
            probe.ErrorMessage);
    }

    public async Task<OperationExecutionResult> ConvertAsync(
        PdfToExcelRequest request,
        IProgress<WorkerProgressMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var jobId = Guid.NewGuid();
        string? decryptedInputPath = null;
        try
        {
            var workerInputPath = request.InputPath;
            if (!string.IsNullOrEmpty(request.Password))
            {
                if (request.Password.Length > 1024)
                {
                    return OperationExecutionResult.Failed(
                        "PDF_PASSWORD_INVALID",
                        "The PDF password is too long.",
                        false);
                }

                if (!File.Exists(_qpdfExecutablePath))
                {
                    return OperationExecutionResult.Failed(
                        "QPDF_UNAVAILABLE",
                        "The local PDF unlock engine is unavailable.",
                        false);
                }

                var decryptDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "DocPivot",
                    "pdf-unlock");
                Directory.CreateDirectory(decryptDirectory);
                decryptedInputPath = Path.Combine(decryptDirectory, $"{jobId:N}.pdf");
                var decryptResult = await _qpdfRunner.RunAsync(
                        _qpdfExecutablePath,
                        [
                            $"--password={request.Password}",
                            "--decrypt",
                            request.InputPath,
                            decryptedInputPath,
                        ],
                        DecryptTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (decryptResult.ExitCode != 0 || !File.Exists(decryptedInputPath))
                {
                    return OperationExecutionResult.Failed(
                        "PDF_PASSWORD_INVALID",
                        "The PDF password is missing or incorrect.",
                        false);
                }

                workerInputPath = decryptedInputPath;
            }

            var workerRequest = WorkerPdfToExcelStartMessage.Create(
                jobId,
                workerInputPath,
                request.OutputPath,
                new WorkerPdfToExcelOptions(
                    request.OcrMode,
                    request.WorksheetMode,
                    request.OcrLanguage));

            void HandleOutputLine(string line)
            {
                var message = TryDeserialize<WorkerProgressMessage>(line);
                if (message is not null &&
                    message.V == WorkerProtocol.CurrentVersion &&
                    message.Type == WorkerMessageTypes.Progress &&
                    message.JobId == jobId)
                {
                    progress?.Report(message);
                }
            }

            var result = await ExternalProcessRunner.RunAsync(
                    _executablePath,
                    BuildArguments(
                        "--execute",
                        "--distribution-root",
                        _distributionRoot,
                        "--timeout-seconds",
                        ((int)_workerTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)),
                    JsonSerializer.Serialize(workerRequest, JsonOptions),
                    _parentTimeout,
                    HandleOutputLine,
                    cancellationToken)
                .ConfigureAwait(false);
            var error = ReadMessages<WorkerErrorMessage>(result.StandardOutput)
                .LastOrDefault(message =>
                    message.V == WorkerProtocol.CurrentVersion &&
                    message.Type == WorkerMessageTypes.Error &&
                    message.JobId == jobId);
            if (error is not null)
            {
                return OperationExecutionResult.Failed(error.Code, error.Message, error.Retryable);
            }

            var workerResult = ReadMessages<WorkerResultMessage>(result.StandardOutput)
                .LastOrDefault(message =>
                    message.V == WorkerProtocol.CurrentVersion &&
                    message.Type == WorkerMessageTypes.Result &&
                    message.JobId == jobId);
            if (result.ExitCode == 0 &&
                workerResult is not null &&
                string.Equals(workerResult.Status, "succeeded", StringComparison.Ordinal))
            {
                return OperationExecutionResult.Succeeded(
                    workerResult.Artifacts,
                    workerResult.Notices,
                    workerResult.Metrics);
            }

            return OperationExecutionResult.Failed(
                "PDF_TABLE_WORKER_PROTOCOL_INVALID",
                "The PDF table worker did not return a valid result.",
                true);
        }
        finally
        {
            if (decryptedInputPath is not null)
            {
                try
                {
                    File.Delete(decryptedInputPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup must not replace the extraction result.
                }
            }
        }
    }

    private IReadOnlyList<string> BuildArguments(params string[] arguments) =>
        [.. _argumentPrefix, .. arguments];

    private static IEnumerable<T> ReadMessages<T>(string output)
        where T : class
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var message = TryDeserialize<T>(line);
            if (message is not null)
            {
                yield return message;
            }
        }
    }

    private static T? TryDeserialize<T>(string line)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
