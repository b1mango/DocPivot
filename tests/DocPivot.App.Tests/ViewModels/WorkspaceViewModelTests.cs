using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Excel;
using DocPivot.Core.Jobs;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Operations;

namespace DocPivot.App.Tests.ViewModels;

public sealed class WorkspaceViewModelTests
{
    [Fact]
    public async Task StartProcessing_UsesUniqueOutputNameAndMarksSuccess()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("report.xlsx");
        var outputDirectory = workspace.CreateDirectory("output");
        workspace.CreateFile(Path.Combine("output", "report.pdf"));
        string? requestedOutput = null;
        var officeClient = new FakeOfficeWorkerClient
        {
            ConvertHandler = (input, output, _, progress, _) =>
            {
                requestedOutput = output;
                progress?.Report(WorkerProgressMessage.Create(Guid.NewGuid(), "pdf-exported", 2, 3));
                return Task.FromResult(OfficeConversionResult.Succeeded([output]));
            },
        };
        using var viewModel = CreateViewModel(officeClient);
        viewModel.OutputDirectory = outputDirectory;

        await viewModel.InitializeAsync();
        viewModel.AddPaths([inputPath]);
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(requestedOutput);
        Assert.EndsWith("report (2).pdf", requestedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobState.Succeeded, viewModel.Files[0].State);
        Assert.True(viewModel.Files[0].CanOpenResult);
        Assert.True(viewModel.HasResults);
    }

    [Fact]
    public async Task StartProcessing_ContinuesAfterSingleFileFailure()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreateFile("first.docx");
        var secondPath = workspace.CreateFile("second.xlsx");
        var officeClient = new FakeOfficeWorkerClient
        {
            ConvertHandler = (input, output, _, _, _) => Task.FromResult(
                input.EndsWith("first.docx", StringComparison.OrdinalIgnoreCase)
                    ? OfficeConversionResult.Failed("OFFICE_COM_FAILURE", "failed", true)
                    : OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);
        viewModel.OutputDirectory = workspace.CreateDirectory("output");

        await viewModel.InitializeAsync();
        viewModel.AddPaths([firstPath, secondPath]);
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Failed, viewModel.Files[0].State);
        Assert.Contains("Office", viewModel.Files[0].StatusText, StringComparison.Ordinal);
        Assert.Equal(JobState.Succeeded, viewModel.Files[1].State);
        Assert.Equal("处理完成：成功 1，失败 1", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CancelProcessing_MarksCurrentFileCanceled()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("cancel.docx");
        var secondPath = workspace.CreateFile("later.xlsx");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var officeClient = new FakeOfficeWorkerClient
        {
            ConvertHandler = async (_, _, _, _, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return OfficeConversionResult.Failed("UNREACHABLE", "unreachable", false);
            },
        };
        using var viewModel = CreateViewModel(officeClient);
        viewModel.OutputDirectory = workspace.CreateDirectory("output");

        await viewModel.InitializeAsync();
        viewModel.AddPaths([inputPath]);
        var processing = viewModel.StartProcessingCommand.ExecuteAsync(null);
        await started.Task;
        viewModel.AddPaths([secondPath]);
        Assert.Single(viewModel.Files);
        Assert.Equal("任务处理中，暂时不能添加文件", viewModel.StatusMessage);
        viewModel.CancelProcessingCommand.Execute(null);
        await processing;

        Assert.Equal(JobState.Canceled, viewModel.Files[0].State);
        Assert.False(viewModel.IsProcessing);
        Assert.StartsWith("已取消", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPaths_SkipsInvalidPathAndKeepsValidFile()
    {
        using var workspace = new TemporaryWorkspace();
        var validPath = workspace.CreateFile("valid.xlsx");
        using var viewModel = CreateViewModel(new FakeOfficeWorkerClient
        {
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        });

        viewModel.AddPaths(["\0invalid", validPath]);

        Assert.Single(viewModel.Files);
        Assert.Equal("已添加 1 个文件，跳过 1 个", viewModel.StatusMessage);
    }

    [Fact]
    public async Task StartProcessing_InvalidOutputPath_RemainsQueuedAndReportsError()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("invalid-output.xlsx");
        using var viewModel = CreateViewModel(new FakeOfficeWorkerClient
        {
            ConvertHandler = (_, _, _, _, _) =>
                throw new InvalidOperationException("The worker must not run for an invalid output path."),
        });

        await viewModel.InitializeAsync();
        viewModel.AddPaths([inputPath]);
        viewModel.OutputDirectory = "\0invalid";

        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Queued, viewModel.Files[0].State);
        Assert.False(viewModel.IsProcessing);
        Assert.Equal("无法创建输出目录，请选择有写入权限的位置", viewModel.StatusMessage);
    }

    [Fact]
    public async Task StartProcessing_LocksOutputDirectoryForEntireBatch()
    {
        using var workspace = new TemporaryWorkspace();
        var initialOutputDirectory = workspace.CreateDirectory("initial-output");
        var changedOutputDirectory = workspace.CreateDirectory("changed-output");
        var firstPath = workspace.CreateFile("first.xlsx");
        var secondPath = workspace.CreateFile("second.xlsx");
        var requestedOutputs = new List<string>();
        WorkspaceViewModel? viewModelReference = null;
        var officeClient = new FakeOfficeWorkerClient
        {
            ConvertHandler = (_, output, _, _, _) =>
            {
                requestedOutputs.Add(output);
                viewModelReference!.OutputDirectory = changedOutputDirectory;
                return Task.FromResult(OfficeConversionResult.Succeeded([output]));
            },
        };
        using var viewModel = CreateViewModel(officeClient);
        viewModelReference = viewModel;
        viewModel.OutputDirectory = initialOutputDirectory;

        await viewModel.InitializeAsync();
        viewModel.AddPaths([firstPath, secondPath]);
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.Equal(2, requestedOutputs.Count);
        Assert.All(
            requestedOutputs,
            output => Assert.StartsWith(
                Path.GetFullPath(initialOutputDirectory) + Path.DirectorySeparatorChar,
                output,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddExcel_DiscoversOrderedWorksheetOptionsAndDefaultsToAll()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("范围测试.xlsx");
        string? inspectedPath = null;
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (path, _) =>
            {
                inspectedPath = path;
                return Task.FromResult(ExcelWorksheetListResult.Succeeded(
                [
                    new ExcelWorksheetDescriptor("总览", 1, "visible", true),
                    new ExcelWorksheetDescriptor("华东 明细_2026", 2, "visible", true),
                    new ExcelWorksheetDescriptor("内部参数", 3, "very-hidden", false),
                ]));
            },
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForWorksheetDiscoveryAsync();

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(inputPath, inspectedPath);
        Assert.True(file.IsWorksheetSelectionResolved);
        Assert.Equal(ExcelExportScopeKind.AllWorksheets, file.SelectedWorksheetOption.Kind);
        Assert.Collection(
            file.WorksheetOptions,
            all => Assert.Equal("全部工作表", all.DisplayName),
            overview => Assert.Equal("总览", overview.WorksheetName),
            details => Assert.Equal("华东 明细_2026", details.WorksheetName),
            hidden =>
            {
                Assert.False(hidden.IsEnabled);
                Assert.Contains("深度隐藏", hidden.DisplayName, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task AddWord_DoesNotStartWorksheetDiscovery()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("report.docx");
        var discoveryCalls = 0;
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (_, _) =>
            {
                Interlocked.Increment(ref discoveryCalls);
                return Task.FromResult(ExcelWorksheetListResult.Succeeded([]));
            },
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForWorksheetDiscoveryAsync();

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(0, discoveryCalls);
        Assert.Equal(WorksheetDiscoveryState.NotApplicable, file.WorksheetDiscoveryState);
        Assert.True(file.IsWorksheetSelectionResolved);
    }

    [Fact]
    public async Task AddExcelToExcelTool_StartsCompatibilityPreflight()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("operations.xlsx");
        var discoveryCalls = 0;
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (_, _) =>
            {
                Interlocked.Increment(ref discoveryCalls);
                return Task.FromResult(ExcelWorksheetListResult.Succeeded(
                    [new ExcelWorksheetDescriptor("Data", 1, "visible", true)],
                    new ExcelWorkbookCompatibilityReport(1, 0, 0, 0, false, false, [])));
            },
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForWorksheetDiscoveryAsync();

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(1, discoveryCalls);
        Assert.False(file.RequiresWorksheetSelection);
        Assert.True(file.RequiresExcelToolPreflight);
        Assert.True(file.IsExcelToolPreflightResolved);
        Assert.Equal("1 张表 · 可处理", file.ExcelToolPreflightSummaryText);
        Assert.Equal(WorksheetDiscoveryState.NotApplicable, file.WorksheetDiscoveryState);
    }

    [Fact]
    public async Task WorksheetDiscovery_LoadingBlocksStartUntilReady()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("loading.xlsx");
        var completion = new TaskCompletionSource<ExcelWorksheetListResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (_, _) => completion.Task,
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);
        await viewModel.InitializeAsync();

        viewModel.AddPaths([inputPath]);

        Assert.True(viewModel.Files[0].IsWorksheetSelectionLoading);
        Assert.False(viewModel.StartProcessingCommand.CanExecute(null));

        completion.SetResult(ExcelWorksheetListResult.Succeeded(
            [new ExcelWorksheetDescriptor("Sheet1", 1, "visible", true)]));
        await viewModel.WaitForWorksheetDiscoveryAsync();

        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));
        Assert.True(viewModel.Files[0].IsWorksheetSelectionEnabled);
    }

    [Fact]
    public async Task StartProcessing_PassesIndependentWorksheetSelectionForEachExcelFile()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreateFile("first.xlsx");
        var secondPath = workspace.CreateFile("second.xls");
        var requestedScopes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (path, _) => Task.FromResult(ExcelWorksheetListResult.Succeeded(
            [
                new ExcelWorksheetDescriptor(
                    path.EndsWith("first.xlsx", StringComparison.OrdinalIgnoreCase) ? "第一张" : "第二张",
                    1,
                    "visible",
                    true),
            ])),
            ConvertHandler = (input, output, options, _, _) =>
            {
                requestedScopes[input] = options?.WorksheetName;
                return Task.FromResult(OfficeConversionResult.Succeeded([output]));
            },
        };
        using var viewModel = CreateViewModel(officeClient);
        viewModel.OutputDirectory = workspace.CreateDirectory("output");
        await viewModel.InitializeAsync();
        viewModel.AddPaths([firstPath, secondPath]);
        await viewModel.WaitForWorksheetDiscoveryAsync();
        viewModel.Files[0].SelectedWorksheetOption = viewModel.Files[0].WorksheetOptions[1];
        viewModel.Files[1].SelectedWorksheetOption = viewModel.Files[1].WorksheetOptions[1];

        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.Equal("第一张", requestedScopes[firstPath]);
        Assert.Equal("第二张", requestedScopes[secondPath]);
    }

    [Fact]
    public async Task WorksheetDiscoveryFailure_RequiresExplicitCompleteWorkbookFallback()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("failure.xlsx");
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (_, _) => Task.FromResult(ExcelWorksheetListResult.Failed(
                "OFFICE_COM_FAILURE",
                "failed",
                true)),
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);
        await viewModel.InitializeAsync();
        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForWorksheetDiscoveryAsync();
        var file = Assert.Single(viewModel.Files);

        Assert.Equal(WorksheetDiscoveryState.Failed, file.WorksheetDiscoveryState);
        Assert.False(viewModel.StartProcessingCommand.CanExecute(null));
        Assert.True(file.CanConfirmAllWorksheetsFallback);

        viewModel.ConfirmAllWorksheetsFallbackCommand.Execute(file);

        Assert.Equal(WorksheetDiscoveryState.Ready, file.WorksheetDiscoveryState);
        Assert.Null(file.SelectedWorksheetName);
        Assert.True(file.HasWorksheetDiscoveryWarning);
        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));
    }

    [Fact]
    public async Task RetryWorksheetDiscovery_ReplacesFailureWithFreshWorksheetList()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("retry.xlsx");
        var attempts = 0;
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (_, _) => Task.FromResult(
                Interlocked.Increment(ref attempts) == 1
                    ? ExcelWorksheetListResult.Failed("OFFICE_COM_FAILURE", "failed", true)
                    : ExcelWorksheetListResult.Succeeded(
                        [new ExcelWorksheetDescriptor("恢复后的工作表", 1, "visible", true)])),
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);
        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForWorksheetDiscoveryAsync();
        var file = Assert.Single(viewModel.Files);
        Assert.Equal(WorksheetDiscoveryState.Failed, file.WorksheetDiscoveryState);

        viewModel.RetryWorksheetDiscoveryCommand.Execute(file);
        await viewModel.WaitForWorksheetDiscoveryAsync();

        Assert.Equal(2, attempts);
        Assert.Equal(WorksheetDiscoveryState.Ready, file.WorksheetDiscoveryState);
        Assert.False(file.HasWorksheetDiscoveryWarning);
        Assert.Equal("恢复后的工作表", file.WorksheetOptions[1].WorksheetName);
    }

    [Fact]
    public async Task RemoveFile_CancelsInFlightWorksheetDiscovery()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("remove.xlsx");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    cancellationObserved.TrySetResult();
                }

                return ExcelWorksheetListResult.Succeeded([]);
            },
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);
        viewModel.AddPaths([inputPath]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        viewModel.RemoveFileCommand.Execute(viewModel.Files[0]);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await viewModel.WaitForWorksheetDiscoveryAsync();
        Assert.Empty(viewModel.Files);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightOfficeProbe()
    {
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var officeClient = new FakeOfficeWorkerClient
        {
            ProbeHandler = async cancellationToken =>
            {
                probeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    cancellationObserved.TrySetResult();
                }

                return new OfficeEngineStatus(true, "Office", "16.0", "x64", null);
            },
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient);

        var initialization = viewModel.InitializeAsync();
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        viewModel.Dispose();

        await initialization.WaitAsync(TimeSpan.FromSeconds(1));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(viewModel.IsEngineReady);
    }

    [Fact]
    public async Task ExcelMerge_UsesQueueOrderAndFrozenSafeOptions()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreateFile("first.xlsx");
        var secondPath = workspace.CreateFile("second.xls");
        ExcelOperationRequest? capturedRequest = null;
        var excelClient = new FakeExcelOperationsClient
        {
            ExecuteHandler = (request, _, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(OperationExecutionResult.Succeeded([request.Output]));
            },
        };
        using var viewModel = CreateViewModel(CreateReadyOfficeClient(), excelClient);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.OutputDirectory = workspace.CreateDirectory("output");
        await viewModel.InitializeAsync();
        viewModel.AddPaths([firstPath, secondPath]);

        viewModel.MoveFileUpCommand.Execute(viewModel.Files[1]);
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(ExcelOperationKind.Merge, capturedRequest.Kind);
        Assert.Equal([secondPath, firstPath], capturedRequest.Inputs);
        Assert.True(capturedRequest.Options.IncludeHiddenWorksheets);
        Assert.True(capturedRequest.Options.PreserveExternalLinks);
        Assert.True(capturedRequest.Options.SkipUnsafeCompressionSheets);
        Assert.True(capturedRequest.Options.ConvertLegacyWorkbookToOpenXml);
        Assert.Equal(ExcelImageCompressionLevel.None, capturedRequest.Options.ImageCompressionLevel);
        Assert.EndsWith("合并工作簿.xlsx", capturedRequest.Output, StringComparison.OrdinalIgnoreCase);
        Assert.All(viewModel.Files, static file => Assert.Equal(JobState.Succeeded, file.State));
        Assert.Equal(viewModel.Files[0].OutputPath, viewModel.Files[1].OutputPath);
    }

    [Fact]
    public async Task ExcelMerge_PreviewsDeterministicDuplicateWorksheetNames()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreateFile("first.xlsx");
        var secondPath = workspace.CreateFile("second.xlsx");
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (_, _) => Task.FromResult(ExcelWorksheetListResult.Succeeded(
            [
                new ExcelWorksheetDescriptor("总览", 1, "visible", true),
                new ExcelWorksheetDescriptor("明细", 2, "visible", true),
            ])),
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient, new FakeExcelOperationsClient
        {
            ExecuteHandler = (request, _, _) =>
                Task.FromResult(OperationExecutionResult.Succeeded([request.Output])),
        });
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        await viewModel.InitializeAsync();
        viewModel.AddPaths([firstPath, secondPath]);

        await viewModel.WaitForWorksheetDiscoveryAsync();

        Assert.Equal("4 张表 · 2 个名称调整", viewModel.ExcelMergePreviewSummary);
        Assert.Contains("总览 → 总览 (2)", viewModel.ExcelMergePreviewDetail, StringComparison.Ordinal);
        Assert.Contains("明细 → 明细 (2)", viewModel.ExcelMergePreviewDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcelSplit_UsesOutputDirectoryAndOpensResultFolder()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("source.xlsx");
        var outputDirectory = workspace.CreateDirectory("output");
        ExcelOperationRequest? capturedRequest = null;
        var excelClient = new FakeExcelOperationsClient
        {
            ExecuteHandler = (request, _, _) =>
            {
                capturedRequest = request;
                Directory.CreateDirectory(request.Output);
                return Task.FromResult(OperationExecutionResult.Succeeded(
                    [Path.Combine(request.Output, "Sheet1.xlsx")]));
            },
        };
        var shellService = new FakeShellService();
        using var viewModel = CreateViewModel(CreateReadyOfficeClient(), excelClient, shellService);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.SelectedExcelToolMode = Assert.Single(
            viewModel.ExcelToolModes,
            static mode => mode.Mode == ExcelToolMode.Split);
        viewModel.OutputDirectory = outputDirectory;
        await viewModel.InitializeAsync();
        viewModel.AddPaths([inputPath]);

        await viewModel.StartProcessingCommand.ExecuteAsync(null);
        viewModel.OpenResultCommand.Execute(viewModel.Files[0]);

        Assert.NotNull(capturedRequest);
        Assert.Equal(ExcelOperationKind.Split, capturedRequest.Kind);
        var splitDirectory = Path.Combine(outputDirectory, "source - 拆分");
        Assert.Equal(splitDirectory, capturedRequest.Output);
        Assert.Equal(splitDirectory, viewModel.Files[0].OutputPath);
        Assert.Equal(splitDirectory, shellService.OpenedFolder);
        Assert.Null(shellService.OpenedFile);
    }

    [Fact]
    public async Task ExcelSplit_WithSameNamedSources_UsesIndependentOutputDirectories()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreateFile(Path.Combine("first", "report.xlsx"));
        var secondPath = workspace.CreateFile(Path.Combine("second", "report.xlsx"));
        var outputDirectory = workspace.CreateDirectory("output");
        var capturedRequests = new List<ExcelOperationRequest>();
        var excelClient = new FakeExcelOperationsClient
        {
            ExecuteHandler = (request, _, _) =>
            {
                capturedRequests.Add(request);
                Directory.CreateDirectory(request.Output);
                return Task.FromResult(OperationExecutionResult.Succeeded(
                    [Path.Combine(request.Output, "Sheet1.xlsx")]));
            },
        };
        using var viewModel = CreateViewModel(CreateReadyOfficeClient(), excelClient);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.SelectedExcelToolMode = Assert.Single(
            viewModel.ExcelToolModes,
            static mode => mode.Mode == ExcelToolMode.Split);
        viewModel.OutputDirectory = outputDirectory;
        await viewModel.InitializeAsync();
        viewModel.AddPaths([firstPath, secondPath]);

        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.Equal(2, capturedRequests.Count);
        Assert.Equal(
            Path.Combine(outputDirectory, "report - 拆分"),
            capturedRequests[0].Output);
        Assert.Equal(
            Path.Combine(outputDirectory, "report - 拆分 (2)"),
            capturedRequests[1].Output);
        Assert.NotEqual(capturedRequests[0].Output, capturedRequests[1].Output);
        Assert.Equal(capturedRequests[0].Output, viewModel.Files[0].OutputPath);
        Assert.Equal(capturedRequests[1].Output, viewModel.Files[1].OutputPath);
    }

    [Fact]
    public async Task ExcelCompress_AllocatesUniqueXlsxOutputAndPassesOptions()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("data.xls");
        var outputDirectory = workspace.CreateDirectory("output");
        workspace.CreateFile(Path.Combine("output", "data - 压缩.xls"));
        ExcelOperationRequest? capturedRequest = null;
        var excelClient = new FakeExcelOperationsClient
        {
            ExecuteHandler = (request, _, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(OperationExecutionResult.Succeeded([request.Output]));
            },
        };
        using var viewModel = CreateViewModel(CreateReadyOfficeClient(), excelClient);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.SelectedExcelToolMode = Assert.Single(
            viewModel.ExcelToolModes,
            static mode => mode.Mode == ExcelToolMode.Compress);
        viewModel.IncludeHiddenWorksheets = false;
        viewModel.PreserveExternalLinks = false;
        viewModel.SkipUnsafeCompressionSheets = false;
        viewModel.ConvertLegacyWorkbookToOpenXml = false;
        viewModel.SelectedExcelImageCompressionProfile = Assert.Single(
            viewModel.ExcelImageCompressionProfiles,
            static profile => profile.Level == ExcelImageCompressionLevel.Balanced);
        viewModel.OutputDirectory = outputDirectory;
        await viewModel.InitializeAsync();
        viewModel.AddPaths([inputPath]);

        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(ExcelOperationKind.Compress, capturedRequest.Kind);
        Assert.EndsWith("data - 压缩 (2).xls", capturedRequest.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(capturedRequest.Options.IncludeHiddenWorksheets);
        Assert.False(capturedRequest.Options.PreserveExternalLinks);
        Assert.False(capturedRequest.Options.SkipUnsafeCompressionSheets);
        Assert.False(capturedRequest.Options.ConvertLegacyWorkbookToOpenXml);
        Assert.Equal(ExcelImageCompressionLevel.Balanced, capturedRequest.Options.ImageCompressionLevel);
    }

    [Fact]
    public async Task ExcelCompress_DisplaysActualCompressionMetrics()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("metrics.xlsx");
        var excelClient = new FakeExcelOperationsClient
        {
            ExecuteHandler = (request, _, _) => Task.FromResult(OperationExecutionResult.Succeeded(
                [request.Output],
                metrics: new Dictionary<string, string>
                {
                    ["bytesBefore"] = "1048576",
                    ["bytesAfter"] = "524288",
                    ["cleanedWorksheetCount"] = "2",
                    ["skippedWorksheetCount"] = "1",
                    ["compressedImageCount"] = "3",
                    ["sourceContentRetained"] = "false",
                    ["convertedLegacyWorkbook"] = "false",
                })),
        };
        using var viewModel = CreateViewModel(CreateReadyOfficeClient(), excelClient);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.SelectedExcelToolMode = Assert.Single(
            viewModel.ExcelToolModes,
            static mode => mode.Mode == ExcelToolMode.Compress);
        viewModel.OutputDirectory = workspace.CreateDirectory("output");
        await viewModel.InitializeAsync();
        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForWorksheetDiscoveryAsync();

        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(
            "压缩完成 · 1 MB → 512 KB · 节省 50.0% · 清理 2 张表 · 重采样 3 张图 · 跳过 1 张风险表",
            file.StatusText);
    }

    [Fact]
    public async Task ExcelCompatibilityPreflight_ReportsWorkbookRisks()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreateFile("risky.xlsx");
        var officeClient = new FakeOfficeWorkerClient
        {
            WorksheetListHandler = (_, _) => Task.FromResult(ExcelWorksheetListResult.Succeeded(
                [new ExcelWorksheetDescriptor("Data", 1, "visible", true)],
                new ExcelWorkbookCompatibilityReport(
                    1,
                    0,
                    1,
                    2,
                    true,
                    false,
                    ["嵌入式图表"]))),
            ConvertHandler = (_, output, _, _, _) =>
                Task.FromResult(OfficeConversionResult.Succeeded([output])),
        };
        using var viewModel = CreateViewModel(officeClient, new FakeExcelOperationsClient
        {
            ExecuteHandler = (_, _, _) => throw new InvalidOperationException("Must not execute."),
        });
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.AddPaths([inputPath]);

        await viewModel.WaitForWorksheetDiscoveryAsync();

        var file = Assert.Single(viewModel.Files);
        Assert.True(file.HasExcelToolPreflightWarning);
        Assert.Equal("1 张表 · 有风险", file.ExcelToolPreflightSummaryText);
        Assert.Contains("包含 VBA", file.ExcelToolPreflightStatusText, StringComparison.Ordinal);
        Assert.Contains("2 个外部链接", file.ExcelToolPreflightStatusText, StringComparison.Ordinal);
        Assert.Contains("嵌入式图表", file.ExcelToolPreflightStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcelMerge_RequiresTwoPendingFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var excelClient = new FakeExcelOperationsClient
        {
            ExecuteHandler = (_, _, _) => throw new InvalidOperationException("Must not execute."),
        };
        using var viewModel = CreateViewModel(CreateReadyOfficeClient(), excelClient);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        await viewModel.InitializeAsync();
        viewModel.AddPaths([workspace.CreateFile("one.xlsx")]);

        Assert.False(viewModel.StartProcessingCommand.CanExecute(null));

        viewModel.AddPaths([workspace.CreateFile("two.xlsx")]);

        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));
        Assert.Equal("Excel 工具已就绪", viewModel.EngineStatusTitle);
    }

    private static WorkspaceViewModel CreateViewModel(
        IOfficeWorkerClient officeClient,
        IExcelOperationsClient? excelOperationsClient = null,
        IShellService? shellService = null) =>
        new(
            new FakeFilePickerService(),
            officeClient,
            shellService ?? new FakeShellService(),
            excelOperationsClient);

    private static FakeOfficeWorkerClient CreateReadyOfficeClient() => new()
    {
        ConvertHandler = (_, output, _, _, _) =>
            Task.FromResult(OfficeConversionResult.Succeeded([output])),
    };

    private sealed class FakeOfficeWorkerClient : IOfficeWorkerClient
    {
        public Func<CancellationToken, Task<OfficeEngineStatus>> ProbeHandler { get; init; } =
            _ => Task.FromResult(new OfficeEngineStatus(true, "Office", "16.0", "x64", null));

        public Func<string, CancellationToken, Task<ExcelWorksheetListResult>> WorksheetListHandler { get; init; } =
            (_, _) => Task.FromResult(ExcelWorksheetListResult.Succeeded(
            [
                new ExcelWorksheetDescriptor("Sheet1", 1, "visible", true),
                new ExcelWorksheetDescriptor("数据表", 2, "visible", true),
            ]));

        public required Func<
            string,
            string,
            OfficeConversionOptions?,
            IProgress<WorkerProgressMessage>?,
            CancellationToken,
            Task<OfficeConversionResult>> ConvertHandler { get; init; }

        public Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            ProbeHandler(cancellationToken);

        public Task<ExcelWorksheetListResult> ListExcelWorksheetsAsync(
            string inputPath,
            CancellationToken cancellationToken = default) =>
            WorksheetListHandler(inputPath, cancellationToken);

        public Task<OfficeConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            OfficeConversionOptions? options = null,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            ConvertHandler(inputPath, outputPath, options, progress, cancellationToken);
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public IReadOnlyList<string> PickFiles(DocumentOperation operation) => [];

        public string? PickFolder(string initialDirectory) => null;
    }

    private sealed class FakeExcelOperationsClient : IExcelOperationsClient
    {
        public required Func<
            ExcelOperationRequest,
            IProgress<WorkerProgressMessage>?,
            CancellationToken,
            Task<OperationExecutionResult>> ExecuteHandler { get; init; }

        public Task<OperationExecutionResult> ExecuteAsync(
            ExcelOperationRequest request,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            ExecuteHandler(request, progress, cancellationToken);
    }

    private sealed class FakeShellService : IShellService
    {
        public string? OpenedFile { get; private set; }

        public string? OpenedFolder { get; private set; }

        public void OpenFile(string path)
        {
            OpenedFile = path;
        }

        public void OpenFolder(string path)
        {
            OpenedFolder = path;
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string CreateDirectory(string relativePath)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(string relativePath)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
