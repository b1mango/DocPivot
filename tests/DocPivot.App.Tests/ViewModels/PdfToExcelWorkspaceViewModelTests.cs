using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Tables;

namespace DocPivot.App.Tests.ViewModels;

public sealed class PdfToExcelWorkspaceViewModelTests
{
    [Fact]
    public async Task Processing_UsesSelectedOptionsAndAllowsSignedReadOnlyInput()
    {
        using var workspace = new TemporaryDirectory();
        var inputPath = workspace.CreateFile("signed-table.pdf");
        var outputDirectory = workspace.CreateDirectory("output");
        PdfToExcelRequest? capturedRequest = null;
        var tableClient = new FakePdfTableOperationsClient
        {
            ConvertHandler = (request, _, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(OperationExecutionResult.Succeeded(
                    [request.OutputPath],
                    [new WorkerNotice("LOW_CONFIDENCE_CELLS", "review", "warning")],
                    new Dictionary<string, string>
                    {
                        ["tableCount"] = "2",
                        ["worksheetCount"] = "3",
                        ["ocrPageCount"] = "1",
                        ["lowConfidenceCellCount"] = "4",
                    }));
            },
        };
        using var viewModel = CreateViewModel(
            new FakePdfOperationsClient(hasSignature: true),
            tableClient);
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfToExcel);
        viewModel.SelectedPdfOcrMode = Assert.Single(
            viewModel.PdfOcrModes,
            static option => option.Mode == WorkerPdfOcrMode.ForceOcr);
        viewModel.OutputDirectory = outputDirectory;

        await viewModel.InitializeAsync();
        viewModel.AddPaths([inputPath]);
        await viewModel.WaitForPdfPreflightAsync();

        var file = Assert.Single(viewModel.Files);
        Assert.True(file.HasPdfSignature);
        Assert.False(file.HasPdfPreflightWarning);
        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));
        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(WorkerPdfOcrMode.ForceOcr, capturedRequest.OcrMode);
        Assert.Equal(WorkerPdfWorksheetMode.OneWorksheetPerPage, capturedRequest.WorksheetMode);
        Assert.EndsWith("signed-table - 表格.xlsx", capturedRequest.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(file.IsSucceeded);
        Assert.Contains("已提取 2 个表格", file.StatusText, StringComparison.Ordinal);
        Assert.Contains("低置信度单元格", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_WhenOcrUnavailable_AllowsDigitalTextOnlyMode()
    {
        using var viewModel = CreateViewModel(
            new FakePdfOperationsClient(hasSignature: false),
            new FakePdfTableOperationsClient
            {
                ProbeStatus = new PdfTableEngineStatus(
                    IsDigitalTextReady: true,
                    IsLocalOcrReady: false,
                    IsXlsxReady: true,
                    ErrorCode: "OCR_ENGINE_UNAVAILABLE",
                    ErrorMessage: "OCR unavailable"),
            });
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfToExcel);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsSelectedToolReady);
        viewModel.SelectedPdfOcrMode = Assert.Single(
            viewModel.PdfOcrModes,
            static option => option.Mode == WorkerPdfOcrMode.DigitalTextOnly);
        Assert.True(viewModel.IsSelectedToolReady);
        Assert.Equal("PDF 转 Excel 已就绪", viewModel.EngineStatusTitle);
    }

    private static WorkspaceViewModel CreateViewModel(
        IPdfOperationsClient pdfOperationsClient,
        IPdfTableOperationsClient tableClient) =>
        new(
            new FakeFilePickerService(),
            new FakeOfficeWorkerClient(),
            new FakeShellService(),
            pdfOperationsClient: pdfOperationsClient,
            pdfTableOperationsClient: tableClient);

    private sealed class FakePdfTableOperationsClient : IPdfTableOperationsClient
    {
        public PdfTableEngineStatus ProbeStatus { get; init; } = new(
            IsDigitalTextReady: true,
            IsLocalOcrReady: true,
            IsXlsxReady: true,
            ErrorCode: null,
            ErrorMessage: null);

        public Func<
            PdfToExcelRequest,
            IProgress<WorkerProgressMessage>?,
            CancellationToken,
            Task<OperationExecutionResult>>? ConvertHandler { get; init; }

        public Task<PdfTableEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ProbeStatus);

        public Task<OperationExecutionResult> ConvertAsync(
            PdfToExcelRequest request,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            ConvertHandler?.Invoke(request, progress, cancellationToken) ??
            Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath]));
    }

    private sealed class FakePdfOperationsClient(bool hasSignature) :
        IPdfOperationsClient,
        IPdfEngineStatusProvider
    {
        public Task<PdfEngineStatus> ProbeEnginesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PdfEngineStatus(
                QpdfToolStatus.Ready("qpdf.exe", "test"),
                GhostscriptEngineStatus.Ready("gs.exe", "test")));

        public Task<PdfPreflightResult> PreflightAsync(
            PdfPreflightRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PdfPreflightResult.Succeeded(
                request.InputPath,
                pageCount: 2,
                hasSignatureFields: hasSignature));

        public Task<OperationExecutionResult> MergeAsync(
            PdfMergeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationExecutionResult> SplitAsync(
            PdfSplitRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationExecutionResult> OptimizeAsync(
            PdfOptimizeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeOfficeWorkerClient : IOfficeWorkerClient
    {
        public Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OfficeEngineStatus(true, "Office", "test", "x64", null));

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
            throw new NotSupportedException();
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public IReadOnlyList<string> PickFiles(DocumentOperation operation) => [];

        public string? PickFolder(string initialDirectory) => null;
    }

    private sealed class FakeShellService : IShellService
    {
        public void OpenFile(string path)
        {
        }

        public void OpenFolder(string path)
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DocPivot.Tests",
                "PdfToExcelViewModel",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "%PDF-1.4\n%%EOF");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
