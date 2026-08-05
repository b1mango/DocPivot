using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Office;

public sealed class OfficeWorkerClient : IOfficeWorkerClient, IExcelOperationsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultWorkerTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExcelToolWorkerTimeout = TimeSpan.FromSeconds(300);

    private readonly string _executablePath;
    private readonly string[] _argumentPrefix;
    private readonly TimeSpan _workerTimeout;
    private readonly TimeSpan _parentTimeout;

    public OfficeWorkerClient(
        string executablePath,
        IReadOnlyList<string>? argumentPrefix = null,
        TimeSpan? workerTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        _argumentPrefix = argumentPrefix?.ToArray() ?? [];
        _workerTimeout = workerTimeout ?? DefaultWorkerTimeout;
        if (_workerTimeout < TimeSpan.FromSeconds(10) || _workerTimeout > TimeSpan.FromSeconds(300))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerTimeout),
                "Worker timeout must be between 10 and 300 seconds.");
        }

        _parentTimeout = _workerTimeout + TimeSpan.FromSeconds(15);
    }

    public async Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExternalProcessRunner.RunAsync(
            _executablePath,
            BuildArguments("--probe"),
            string.Empty,
            ProbeTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var probe = ReadMessages<WorkerProbeResult>(result.StandardOutput)
            .LastOrDefault(static message =>
                message.V == WorkerProtocol.CurrentVersion &&
                message.Type == WorkerMessageTypes.ProbeResult &&
                string.Equals(message.Worker, "office", StringComparison.Ordinal));
        if (probe is null)
        {
            return new OfficeEngineStatus(
                false,
                null,
                null,
                null,
                "未检测到可用的 Microsoft Word 和 Excel。请确认 Office 已安装并激活。");
        }

        string? product = null;
        string? version = null;
        string? platform = null;
        probe.Metadata?.TryGetValue("officeProductReleaseIds", out product);
        probe.Metadata?.TryGetValue("officeVersion", out version);
        probe.Metadata?.TryGetValue("officePlatform", out platform);
        var isWordReady = probe.Capabilities.TryGetValue("wordComRegistered", out var wordReady) && wordReady;
        var isExcelReady = probe.Capabilities.TryGetValue("excelComRegistered", out var excelReady) && excelReady;
        var isReady = isWordReady && isExcelReady;
        return new OfficeEngineStatus(
            isReady,
            product,
            version,
            platform,
            isReady ? null : "Microsoft Word 或 Excel 未完整注册。")
        {
            IsWordReady = isWordReady,
            IsExcelReady = isExcelReady,
        };
    }

    public async Task<OfficeConversionResult> ConvertAsync(
        string inputPath,
        string outputPath,
        OfficeConversionOptions? options = null,
        IProgress<WorkerProgressMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var inspection = OfficeDocumentInspector.Inspect(inputPath);
        if (!inspection.IsValid || inspection.Kind is null)
        {
            return OfficeConversionResult.Failed(
                inspection.ErrorCode ?? "INPUT_INVALID",
                "输入文件与受支持的 Office 文件类型不匹配。",
                false);
        }

        var operation = inspection.Kind is OfficeDocumentKind.WordBinary or OfficeDocumentKind.WordOpenXml
            ? OfficeWorkerOperations.WordToPdf
            : OfficeWorkerOperations.ExcelToPdf;
        var worksheetName = options?.WorksheetName;
        if (operation == OfficeWorkerOperations.WordToPdf && worksheetName is not null)
        {
            return OfficeConversionResult.Failed(
                "OFFICE_OPTIONS_TYPE_MISMATCH",
                "Excel worksheet options cannot be used with a Word document.",
                false);
        }

        if (worksheetName is not null && string.IsNullOrWhiteSpace(worksheetName))
        {
            return OfficeConversionResult.Failed(
                "EXCEL_WORKSHEET_NAME_INVALID",
                "The worksheet name cannot be empty.",
                false);
        }

        var jobId = Guid.NewGuid();
        var request = WorkerStartMessage.CreateConversion(
            jobId,
            operation,
            inputPath,
            outputPath,
            worksheetName);
        var inputLine = JsonSerializer.Serialize(request, JsonOptions);

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
                "--timeout-seconds",
                ((int)_workerTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            inputLine,
            _parentTimeout,
            HandleOutputLine,
            cancellationToken).ConfigureAwait(false);

        var error = ReadMessages<WorkerErrorMessage>(result.StandardOutput)
            .LastOrDefault(message =>
                message.V == WorkerProtocol.CurrentVersion &&
                message.Type == WorkerMessageTypes.Error &&
                message.JobId == jobId);
        if (error is not null)
        {
            return OfficeConversionResult.Failed(error.Code, error.Message, error.Retryable);
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
            return OfficeConversionResult.Succeeded(workerResult.Artifacts);
        }

        return OfficeConversionResult.Failed(
            "WORKER_PROTOCOL_INVALID",
            "处理引擎未返回有效结果。",
            true);
    }

    public async Task<ExcelWorksheetListResult> ListExcelWorksheetsAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var inspection = OfficeDocumentInspector.Inspect(inputPath);
        if (!inspection.IsValid ||
            inspection.Kind is not (OfficeDocumentKind.ExcelBinary or OfficeDocumentKind.ExcelOpenXml))
        {
            return ExcelWorksheetListResult.Failed(
                inspection.ErrorCode ?? "INPUT_INVALID",
                "输入文件与受支持的 Excel 文件类型不匹配。",
                false);
        }

        var jobId = Guid.NewGuid();
        var request = WorkerStartMessage.CreateExcelWorksheetList(jobId, inputPath);
        var result = await ExternalProcessRunner.RunAsync(
            _executablePath,
            BuildArguments(
                "--execute",
                "--timeout-seconds",
                ((int)_workerTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonSerializer.Serialize(request, JsonOptions),
            _parentTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var error = ReadMessages<WorkerErrorMessage>(result.StandardOutput)
            .LastOrDefault(message =>
                message.V == WorkerProtocol.CurrentVersion &&
                message.Type == WorkerMessageTypes.Error &&
                message.JobId == jobId);
        if (error is not null)
        {
            return ExcelWorksheetListResult.Failed(error.Code, error.Message, error.Retryable);
        }

        var worksheetMessage = ReadMessages<WorkerExcelWorksheetsMessage>(result.StandardOutput)
            .LastOrDefault(message =>
                message.V == WorkerProtocol.CurrentVersion &&
                message.Type == WorkerMessageTypes.ExcelWorksheets &&
                message.JobId == jobId);
        if (result.ExitCode == 0 && worksheetMessage is not null)
        {
            return ExcelWorksheetListResult.Succeeded(
                worksheetMessage.Worksheets,
                worksheetMessage.Compatibility);
        }

        return ExcelWorksheetListResult.Failed(
            "WORKER_PROTOCOL_INVALID",
            "处理引擎未返回有效的工作表列表。",
            true);
    }

    public async Task<OperationExecutionResult> ExecuteAsync(
        ExcelOperationRequest request,
        IProgress<WorkerProgressMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Output);

        var operation = request.Kind switch
        {
            ExcelOperationKind.Merge => OfficeWorkerOperations.MergeExcelWorkbooks,
            ExcelOperationKind.Split => OfficeWorkerOperations.SplitExcelWorkbook,
            ExcelOperationKind.Compress => OfficeWorkerOperations.CompressExcelWorkbook,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported Excel operation."),
        };
        var jobId = Guid.NewGuid();
        var workerRequest = WorkerExcelToolStartMessage.Create(
            jobId,
            operation,
            request.Inputs,
            request.Output,
            new WorkerExcelToolOptions(
                request.Options.IncludeHiddenWorksheets,
                request.Options.PreserveExternalLinks,
                request.Options.SkipUnsafeCompressionSheets,
                request.Options.ConvertLegacyWorkbookToOpenXml,
                request.Options.ImageCompressionLevel));

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
                "--timeout-seconds",
                ((int)ExcelToolWorkerTimeout.TotalSeconds).ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            JsonSerializer.Serialize(workerRequest, JsonOptions),
            ExcelToolWorkerTimeout + TimeSpan.FromSeconds(15),
            HandleOutputLine,
            cancellationToken).ConfigureAwait(false);

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
            "WORKER_PROTOCOL_INVALID",
            "Excel 处理引擎未返回有效结果。",
            true);
    }

    private string[] BuildArguments(params string[] arguments) => [.. _argumentPrefix, .. arguments];

    private static IEnumerable<TMessage> ReadMessages<TMessage>(string output)
        where TMessage : class
    {
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (TryDeserialize<TMessage>(line) is { } message)
            {
                yield return message;
            }
        }
    }

    private static TMessage? TryDeserialize<TMessage>(string line)
        where TMessage : class
    {
        try
        {
            return JsonSerializer.Deserialize<TMessage>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
