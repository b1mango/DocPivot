using System.IO.Compression;
using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Jobs;
using DocPivot.Core.Pdf;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;

namespace DocPivot.App.Tests.ViewModels;

public sealed class PdfWorkspaceViewModelTests
{
    [Fact]
    public async Task AddPdf_StartsAsyncPreflightAndPublishesPageCount()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("report.pdf");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<PdfPreflightResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PdfPreflightRequest? capturedRequest = null;
        var pdfClient = new FakePdfOperationsClient
        {
            PreflightHandler = async (request, cancellationToken) =>
            {
                capturedRequest = request;
                started.TrySetResult();
                return await completion.Task.WaitAsync(cancellationToken);
            },
        };
        using var viewModel = CreateViewModel(pdfClient);

        viewModel.AddPaths([inputPath]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var file = Assert.Single(viewModel.Files);
        Assert.True(file.IsPdfPreflightLoading);
        Assert.False(viewModel.StartProcessingCommand.CanExecute(null));
        Assert.Equal(inputPath, capturedRequest?.InputPath);

        completion.SetResult(PdfPreflightResult.Succeeded(inputPath, 37, hasSignatureFields: false));
        await viewModel.WaitForPdfPreflightAsync();

        Assert.Equal(PdfPreflightState.Ready, file.PdfPreflightState);
        Assert.Equal(37, file.PdfPageCount);
        Assert.Equal("37 页", file.PdfPageCountText);
        Assert.False(file.HasPdfPreflightWarning);
    }

    [Theory]
    [InlineData("PDF_PASSWORD_REQUIRED", true, "已加密")]
    [InlineData("PDF_STRUCTURE_INVALID", false, "结构损坏")]
    public async Task FailedPdfPreflight_BlocksStartAndLocalizesError(
        string errorCode,
        bool isEncrypted,
        string expectedMessageFragment)
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("invalid.pdf");
        var pdfClient = new FakePdfOperationsClient
        {
            PreflightHandler = (request, _) => Task.FromResult(PdfPreflightResult.Failed(
                request.InputPath,
                errorCode,
                "raw engine detail",
                isRetryable: false,
                isEncrypted)),
        };
        using var viewModel = CreateViewModel(pdfClient);
        SelectPdfToolMode(viewModel, PdfToolMode.Compress);

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(PdfPreflightState.Failed, file.PdfPreflightState);
        Assert.Equal(isEncrypted, file.IsPdfEncrypted);
        Assert.Contains(expectedMessageFragment, file.PdfPreflightStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("raw engine detail", file.PdfPreflightStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.StartProcessingCommand.CanExecute(null));
    }

    [Fact]
    public async Task SignedPdf_IsReportedButCompressionProceedsOnCopy()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("signed.pdf");
        var pdfClient = new FakePdfOperationsClient
        {
            PreflightHandler = (request, _) => Task.FromResult(
                PdfPreflightResult.Succeeded(request.InputPath, 6, hasSignatureFields: true)),
        };
        using var viewModel = CreateViewModel(pdfClient);
        SelectPdfToolMode(viewModel, PdfToolMode.Compress);

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(PdfPreflightState.Ready, file.PdfPreflightState);
        Assert.True(file.HasPdfSignature);
        Assert.False(file.HasPdfPreflightWarning);
        Assert.Contains("数字签名输入将只读保留", file.PdfPreflightStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));
    }

    [Fact]
    public async Task PdfMerge_RequiresTwoFilesAndPreservesCurrentQueueOrder()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreatePdf("first.pdf");
        var secondPath = workspace.CreatePdf("second.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        PdfMergeRequest? capturedRequest = null;
        var pdfClient = new FakePdfOperationsClient
        {
            MergeHandler = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(OperationExecutionResult.Succeeded(
                    [request.OutputPath],
                    [
                        new WorkerNotice(
                            "PDF_MERGE_PRIMARY_DOCUMENT_INFO",
                            "raw infrastructure notice",
                            "info"),
                    ]));
            },
        };
        using var viewModel = CreateViewModel(pdfClient);
        viewModel.OutputDirectory = outputDirectory;

        viewModel.AddPaths([firstPath]);
        await viewModel.WaitForPdfPreflightAsync();
        Assert.False(viewModel.StartProcessingCommand.CanExecute(null));

        viewModel.AddPaths([secondPath]);
        await viewModel.WaitForPdfPreflightAsync();
        viewModel.MoveFileUpCommand.Execute(viewModel.Files[1]);

        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal([secondPath, firstPath], capturedRequest.InputPaths);
        Assert.EndsWith("合并 PDF.pdf", capturedRequest.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, pdfClient.MergeCalls);
        Assert.All(viewModel.Files, static file => Assert.Equal(JobState.Succeeded, file.State));
        Assert.Equal(viewModel.Files[0].OutputPath, viewModel.Files[1].OutputPath);
        Assert.Contains("首个 PDF", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("raw infrastructure notice", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfCustomRangeSplit_PassesTrimmedSelectionAndExposesResultDirectory()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("source.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        var shellService = new FakeShellService();
        PdfSplitRequest? capturedRequest = null;
        var pdfClient = new FakePdfOperationsClient
        {
            PreflightHandler = (request, _) => Task.FromResult(
                PdfPreflightResult.Succeeded(request.InputPath, 10, hasSignatureFields: false)),
            SplitHandler = (request, _) =>
            {
                capturedRequest = request;
                var resultDirectory = Path.GetDirectoryName(request.OutputPath)!;
                Directory.CreateDirectory(resultDirectory);
                var artifact = Path.Combine(resultDirectory, "part-001.pdf");
                File.WriteAllText(artifact, "%PDF-1.7\n%%EOF");
                return Task.FromResult(OperationExecutionResult.Succeeded([artifact]));
            },
        };
        using var viewModel = CreateViewModel(pdfClient, shellService);
        SelectPdfToolMode(viewModel, PdfToolMode.Split);
        SelectPdfSplitMode(viewModel, PdfSplitMode.CustomRange);
        viewModel.PdfPageRange = " 2-4,7 ";
        viewModel.OutputDirectory = outputDirectory;

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(PdfSplitMode.CustomRange, capturedRequest.Selection.Mode);
        Assert.Equal("2-4,7", capturedRequest.Selection.PageRange);
        Assert.EndsWith("拆分结果.pdf", capturedRequest.OutputPath, StringComparison.OrdinalIgnoreCase);
        var resultDirectoryPath = Path.GetDirectoryName(capturedRequest.OutputPath);
        Assert.Equal(resultDirectoryPath, viewModel.Files[0].OutputPath);

        viewModel.OpenResultCommand.Execute(viewModel.Files[0]);
        Assert.Equal(resultDirectoryPath, shellService.OpenedFolder);
        Assert.Null(shellService.OpenedFile);
    }

    [Fact]
    public async Task PdfEveryNPagesSplit_PassesPagesPerFile()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("source.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        PdfSplitRequest? capturedRequest = null;
        var pdfClient = new FakePdfOperationsClient
        {
            SplitHandler = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(OperationExecutionResult.Succeeded([]));
            },
        };
        using var viewModel = CreateViewModel(pdfClient);
        SelectPdfToolMode(viewModel, PdfToolMode.Split);
        SelectPdfSplitMode(viewModel, PdfSplitMode.EveryNPages);
        viewModel.PdfPagesPerFile = 3;
        viewModel.OutputDirectory = outputDirectory;

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(PdfSplitMode.EveryNPages, capturedRequest.Selection.Mode);
        Assert.Equal(3, capturedRequest.Selection.PagesPerFile);
        Assert.Null(capturedRequest.Selection.PageRange);
    }

    [Fact]
    public async Task PdfVisualSplit_UsesThumbnailCutsAndCanReturnOneZipArchive()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("visual-source.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        PdfSplitRequest? capturedRequest = null;
        var pdfClient = new FakePdfOperationsClient
        {
            PreflightHandler = (request, _) => Task.FromResult(
                PdfPreflightResult.Succeeded(request.InputPath, 4, hasSignatureFields: false)),
            SplitHandler = (request, _) =>
            {
                capturedRequest = request;
                var resultDirectory = Path.GetDirectoryName(request.OutputPath)!;
                Directory.CreateDirectory(resultDirectory);
                var artifacts = Enumerable.Range(1, 3)
                    .Select(index => Path.Combine(resultDirectory, $"part-{index}.pdf"))
                    .ToArray();
                foreach (var artifact in artifacts)
                {
                    File.WriteAllText(artifact, "%PDF-1.7\n%%EOF");
                }

                return Task.FromResult(OperationExecutionResult.Succeeded(artifacts));
            },
        };
        using var viewModel = CreateViewModel(
            pdfClient,
            pdfThumbnailRenderer: new FakePdfThumbnailRenderer());
        SelectPdfToolMode(viewModel, PdfToolMode.Split);
        SelectPdfSplitMode(viewModel, PdfSplitMode.VisualCuts);
        viewModel.PdfSplitOutputAsZip = true;
        viewModel.OutputDirectory = outputDirectory;

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();
        await viewModel.WaitForPdfPreviewAsync();

        Assert.Equal(6, viewModel.PdfSplitModes.Count);
        Assert.Equal(4, viewModel.PdfPageThumbnails.Count);
        Assert.True(viewModel.ShowPdfVisualPreview);
        viewModel.TogglePdfCutCommand.Execute(viewModel.PdfPageThumbnails[1]);
        viewModel.TogglePdfCutCommand.Execute(viewModel.PdfPageThumbnails[3]);
        Assert.Equal("4 页 · 将输出 3 个 PDF", viewModel.PdfVisualSplitSummary);
        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));

        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(PdfSplitMode.VisualCuts, capturedRequest.Selection.Mode);
        Assert.Equal([1, 3], capturedRequest.Selection.CutAfterPages);
        var archivePath = Assert.Single(viewModel.Files).OutputPath;
        Assert.NotNull(archivePath);
        Assert.EndsWith(".zip", archivePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(archivePath));
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(3, archive.Entries.Count);
        Assert.All(archive.Entries, static entry => Assert.EndsWith(
            ".pdf",
            entry.Name,
            StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(72, false)]
    public async Task PdfCompression_RoutesStrengthContract(int strength, bool expectedLossless)
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("source.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        PdfOptimizeRequest? capturedRequest = null;
        var pdfClient = new FakePdfOperationsClient
        {
            OptimizeHandler = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath]));
            },
        };
        using var viewModel = CreateViewModel(pdfClient);
        SelectPdfToolMode(viewModel, PdfToolMode.Compress);
        viewModel.PdfCompressionStrength = strength;
        viewModel.OutputDirectory = outputDirectory;

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(strength, capturedRequest.CompressionStrength);
        Assert.Equal(expectedLossless, viewModel.CurrentPdfCompressionProfile.IsLossless);
        Assert.Equal(1, pdfClient.OptimizeCalls);
        Assert.Equal(0, pdfClient.MergeCalls);
        Assert.Equal(0, pdfClient.SplitCalls);
    }

    [Fact]
    public async Task PdfCompressionSuccess_DisplaysSizeProfileImpactAndTextVerification()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("source.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        var pdfClient = new FakePdfOperationsClient
        {
            OptimizeHandler = (request, _) => Task.FromResult(
                OperationExecutionResult.Succeeded(
                    [request.OutputPath],
                    [
                        new WorkerNotice(
                            "PDF_LOSSY_PRESERVATION_LIMITS",
                            "raw infrastructure notice",
                            "warning"),
                    ],
                    new Dictionary<string, string>
                    {
                        ["compressionStrength"] = "50",
                        ["optimizationApplied"] = "true",
                        ["originalBytes"] = "1048576",
                        ["outputBytes"] = "524288",
                        ["savedPercent"] = "50",
                        ["imageDpi"] = "197",
                        ["jpegQuality"] = "69",
                        ["impactLevel"] = "medium",
                        ["searchableTextStatus"] = "verified",
                    })),
        };
        using var viewModel = CreateViewModel(pdfClient);
        SelectPdfToolMode(viewModel, PdfToolMode.Compress);
        viewModel.PdfCompressionStrength = 50;
        viewModel.OutputDirectory = outputDirectory;

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        var status = Assert.Single(viewModel.Files).StatusText;
        Assert.Contains("1 MB → 512 KB", status, StringComparison.Ordinal);
        Assert.Contains("节省 50%", status, StringComparison.Ordinal);
        Assert.Contains("197 DPI", status, StringComparison.Ordinal);
        Assert.Contains("JPEG 69%", status, StringComparison.Ordinal);
        Assert.Contains("中等画质影响", status, StringComparison.Ordinal);
        Assert.Contains("文字层已验证", status, StringComparison.Ordinal);
        Assert.Contains("批注、表单和附件不保证保留", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("raw infrastructure notice", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfCompressionPreservationNote_ReflectsLosslessAndLossyBoundaries()
    {
        using var viewModel = CreateViewModel(new FakePdfOperationsClient());
        SelectPdfToolMode(viewModel, PdfToolMode.Compress);

        viewModel.PdfCompressionStrength = 0;
        Assert.Contains("不重采样图片", viewModel.PdfCompressionPreservationNote, StringComparison.Ordinal);

        viewModel.PdfCompressionStrength = 80;
        Assert.Contains("可搜索文字", viewModel.PdfCompressionPreservationNote, StringComparison.Ordinal);
        Assert.Contains("附件不保证保留", viewModel.PdfCompressionPreservationNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovePdf_CancelsInFlightPreflight()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("pending.pdf");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pdfClient = new FakePdfOperationsClient
        {
            PreflightHandler = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Unreachable.");
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            },
        };
        using var viewModel = CreateViewModel(pdfClient);

        viewModel.AddPaths([inputPath]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        viewModel.RemoveFileCommand.Execute(viewModel.Files[0]);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await viewModel.WaitForPdfPreflightAsync();
        Assert.Empty(viewModel.Files);
    }

    [Fact]
    public async Task CancelProcessing_CancelsPdfClientAndMarksCurrentFileCanceled()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("source.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pdfClient = new FakePdfOperationsClient
        {
            OptimizeHandler = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Unreachable.");
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            },
        };
        using var viewModel = CreateViewModel(pdfClient);
        SelectPdfToolMode(viewModel, PdfToolMode.Compress);
        viewModel.OutputDirectory = outputDirectory;
        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();

        var processingTask = viewModel.StartProcessingCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(viewModel.CancelProcessingCommand.CanExecute(null));
        viewModel.CancelProcessingCommand.Execute(null);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await processingTask;
        Assert.Equal(JobState.Canceled, viewModel.Files[0].State);
        Assert.False(viewModel.IsProcessing);
    }

    [Fact]
    public async Task PdfOperationFailure_UsesLocalizedActionableMessage()
    {
        using var workspace = new TemporaryWorkspace();
        var inputPath = workspace.CreatePdf("source.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        var pdfClient = new FakePdfOperationsClient
        {
            OptimizeHandler = (_, _) => Task.FromResult(OperationExecutionResult.Failed(
                "PDF_ACCESS_DENIED",
                "raw engine detail",
                isRetryable: false)),
        };
        using var viewModel = CreateViewModel(pdfClient);
        SelectPdfToolMode(viewModel, PdfToolMode.Compress);
        viewModel.OutputDirectory = outputDirectory;

        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(JobState.Failed, file.State);
        Assert.Equal("PDF_ACCESS_DENIED", file.ErrorCode);
        Assert.Contains("权限", file.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("raw engine detail", file.StatusText, StringComparison.Ordinal);
    }

    private static WorkspaceViewModel CreateViewModel(
        IPdfOperationsClient pdfOperationsClient,
        IShellService? shellService = null,
        IPdfThumbnailRenderer? pdfThumbnailRenderer = null)
    {
        var viewModel = new WorkspaceViewModel(
            new FakeFilePickerService(),
            new FakeOfficeWorkerClient(),
            shellService ?? new FakeShellService(),
            pdfOperationsClient: pdfOperationsClient,
            pdfThumbnailRenderer: pdfThumbnailRenderer);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        return viewModel;
    }

    private static void SelectPdfToolMode(WorkspaceViewModel viewModel, PdfToolMode mode) =>
        viewModel.SelectedPdfToolMode = Assert.Single(
            viewModel.PdfToolModes,
            option => option.Mode == mode);

    private static void SelectPdfSplitMode(WorkspaceViewModel viewModel, PdfSplitMode mode) =>
        viewModel.SelectedPdfSplitMode = Assert.Single(
            viewModel.PdfSplitModes,
            option => option.Mode == mode);

    private sealed class FakePdfOperationsClient : IPdfOperationsClient
    {
        private int _mergeCalls;
        private int _splitCalls;
        private int _optimizeCalls;

        public Func<PdfPreflightRequest, CancellationToken, Task<PdfPreflightResult>> PreflightHandler { get; init; } =
            (request, _) => Task.FromResult(
                PdfPreflightResult.Succeeded(request.InputPath, 5, hasSignatureFields: false));

        public Func<PdfMergeRequest, CancellationToken, Task<OperationExecutionResult>> MergeHandler { get; init; } =
            (_, _) => Task.FromResult(UnexpectedCall());

        public Func<PdfSplitRequest, CancellationToken, Task<OperationExecutionResult>> SplitHandler { get; init; } =
            (_, _) => Task.FromResult(UnexpectedCall());

        public Func<PdfOptimizeRequest, CancellationToken, Task<OperationExecutionResult>> OptimizeHandler { get; init; } =
            (_, _) => Task.FromResult(UnexpectedCall());

        public int MergeCalls => Volatile.Read(ref _mergeCalls);

        public int SplitCalls => Volatile.Read(ref _splitCalls);

        public int OptimizeCalls => Volatile.Read(ref _optimizeCalls);

        public Task<PdfPreflightResult> PreflightAsync(
            PdfPreflightRequest request,
            CancellationToken cancellationToken = default) =>
            PreflightHandler(request, cancellationToken);

        public Task<OperationExecutionResult> MergeAsync(
            PdfMergeRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _mergeCalls);
            return MergeHandler(request, cancellationToken);
        }

        public Task<OperationExecutionResult> SplitAsync(
            PdfSplitRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _splitCalls);
            return SplitHandler(request, cancellationToken);
        }

        public Task<OperationExecutionResult> OptimizeAsync(
            PdfOptimizeRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _optimizeCalls);
            return OptimizeHandler(request, cancellationToken);
        }

        private static OperationExecutionResult UnexpectedCall() =>
            OperationExecutionResult.Failed("UNEXPECTED_CALL", "Unexpected fake client call.", false);
    }

    private sealed class FakeOfficeWorkerClient : IOfficeWorkerClient
    {
        public Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OfficeEngineStatus(true, "Office", "16.0", "x64", null));

        public Task<ExcelWorksheetListResult> ListExcelWorksheetsAsync(
            string inputPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExcelWorksheetListResult.Succeeded([]));

        public Task<OfficeConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            OfficeConversionOptions? options = null,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OfficeConversionResult.Succeeded([outputPath]));
    }

    private sealed class FakePdfThumbnailRenderer : IPdfThumbnailRenderer
    {
        private static readonly byte[] PngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        public Task<IReadOnlyList<string>> RenderAsync(
            string inputPath,
            int pageCount,
            string outputDirectory,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            var paths = Enumerable.Range(1, pageCount)
                .Select(index => Path.Combine(outputDirectory, $"page-{index:D4}.png"))
                .ToArray();
            foreach (var path in paths)
            {
                File.WriteAllBytes(path, PngBytes);
            }

            return Task.FromResult<IReadOnlyList<string>>(paths);
        }
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public IReadOnlyList<string> PickFiles(DocumentOperation operation) => [];

        public string? PickFolder(string initialDirectory) => null;
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

        public string CreatePdf(string relativePath)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "%PDF-1.7\n%%EOF");
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
