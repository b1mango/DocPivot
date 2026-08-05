using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DocPivot.App;
using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Renaming;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Renaming;
using DocPivot.Infrastructure.Tables;
using DocPivotApplication = DocPivot.App.App;

namespace DocPivot.UiSnapshot;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        var repositoryRoot = FindRepositoryRoot();
        var outputDirectory = Path.Combine(repositoryRoot, "artifacts", "ui-snapshots");
        PrepareOutputDirectory(repositoryRoot, outputDirectory);

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "DocPivot.UiSnapshot",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);

        var application = new DocPivotApplication
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        application.InitializeComponent();

        try
        {
            CaptureScenario(application, outputDirectory, "01-empty.png", static _ => { });
            CaptureScenario(
                application,
                outputDirectory,
                "02-queued-long-name.png",
                viewModel => ConfigureQueuedScenario(viewModel, fixtureRoot));
            CaptureScenario(
                application,
                outputDirectory,
                "03-mixed-states.png",
                viewModel => ConfigureMixedScenario(viewModel, fixtureRoot));
            CaptureScenario(
                application,
                outputDirectory,
                "04-minimum-size.png",
                viewModel => ConfigureQueuedScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "05-pdf-compression-minimum-size.png",
                ConfigurePdfCompressionScenario,
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "06-batch-rename-minimum-size.png",
                viewModel => ConfigureBatchRenameScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "07-batch-rename-insert-minimum-size.png",
                viewModel => ConfigureBatchRenameInsertScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "08-batch-rename-numbering-minimum-size.png",
                viewModel => ConfigureBatchRenameNumberingScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "09-batch-rename-manual-minimum-size.png",
                viewModel => ConfigureBatchRenameManualScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "10-excel-compression-minimum-size.png",
                viewModel => ConfigureExcelCompressionScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "11-excel-merge-minimum-size.png",
                viewModel => ConfigureExcelMergeScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "12-pdf-to-excel-minimum-size.png",
                viewModel => ConfigurePdfToExcelScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "13-pdf-split-visual.png",
                viewModel => ConfigurePdfVisualSplitScenario(viewModel, fixtureRoot));
        }
        finally
        {
            application.Shutdown();
            DeleteFixtureDirectory(fixtureRoot);
        }

        Console.WriteLine($"UI snapshots created: {outputDirectory}");
        return 0;
    }

    private static void CaptureScenario(
        Application application,
        string outputDirectory,
        string fileName,
        Action<WorkspaceViewModel> configure,
        double width = 1360,
        double height = 820)
    {
        var window = new MainWindow(
            new FakeFilePickerService(),
            new FakeOfficeWorkerClient(),
            new FakeShellService(),
            pdfOperationsClient: new FakePdfOperationsClient(),
            excelOperationsClient: new FakeExcelOperationsClient(),
            batchRenameExecutor: new BatchRenameExecutor(),
            pdfTableOperationsClient: new FakePdfTableOperationsClient())
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20_000,
            Top = -20_000,
            Width = width,
            Height = height,
        };
        window.Background = (Brush)application.FindResource("WindowFallbackBrush");
        application.MainWindow = window;

        var viewModel = (WorkspaceViewModel)window.DataContext;
        configure(viewModel);

        try
        {
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth));
            var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            EnsureBitmapIsNonBlank(bitmap, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(
                Path.Combine(outputDirectory, fileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            encoder.Save(stream);
            if (stream.Length < 4_096)
            {
                throw new InvalidOperationException($"UI snapshot {fileName} is unexpectedly small.");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void EnsureBitmapIsNonBlank(RenderTargetBitmap bitmap, string fileName)
    {
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * bitmap.PixelHeight));
        bitmap.CopyPixels(pixels, stride, 0);

        var visiblePixels = 0;
        byte minimumChannel = byte.MaxValue;
        byte maximumChannel = byte.MinValue;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] == 0)
            {
                continue;
            }

            visiblePixels++;
            minimumChannel = Math.Min(
                minimumChannel,
                Math.Min(pixels[offset], Math.Min(pixels[offset + 1], pixels[offset + 2])));
            maximumChannel = Math.Max(
                maximumChannel,
                Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2])));
        }

        var minimumVisiblePixels = checked(bitmap.PixelWidth * bitmap.PixelHeight / 2);
        if (visiblePixels < minimumVisiblePixels || maximumChannel - minimumChannel < 64)
        {
            throw new InvalidOperationException($"UI snapshot {fileName} is blank or has insufficient pixel range.");
        }
    }

    private static void ConfigureQueuedScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        var longName = $"quarterly-consolidation-{new string('x', 96)}.xlsx";
        var inputPath = CreateFixture(fixtureRoot, longName);
        viewModel.AddPaths([inputPath]);
        viewModel.StatusMessage = "已添加 1 个文件";
    }

    private static void ConfigureMixedScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        var processingPath = CreateFixture(fixtureRoot, "processing.docx");
        var completedPath = CreateFixture(fixtureRoot, "completed.xlsx");
        var failedPath = CreateFixture(fixtureRoot, "failed.docx");
        viewModel.AddPaths([processingPath, completedPath, failedPath]);

        var processing = viewModel.Files[0];
        processing.MarkValidating();
        processing.MarkRunning();
        processing.ReportProgress(WorkerProgressMessage.Create(Guid.NewGuid(), "office-export", 2, 5));

        var completed = viewModel.Files[1];
        completed.MarkValidating();
        completed.MarkRunning();
        completed.MarkSucceeded(Path.Combine(fixtureRoot, "completed.pdf"));

        var failed = viewModel.Files[2];
        failed.MarkValidating();
        failed.MarkRunning();
        failed.MarkFailed("OFFICE_COM_FAILURE", "Office 导出失败，请检查源文件。");

        viewModel.IsProcessing = true;
        viewModel.StatusMessage = "正在处理 1 / 3";
    }

    private static void ConfigurePdfCompressionScenario(WorkspaceViewModel viewModel)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        viewModel.SelectedPdfToolMode = AssertSingle(
            viewModel.PdfToolModes,
            static option => option.Mode == PdfToolMode.Compress);
        viewModel.PdfCompressionStrength = 72;
        viewModel.StatusMessage = "PDF 压缩参数已就绪";
    }

    private static void ConfigureExcelCompressionScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.SelectedExcelToolMode = AssertSingle(
            viewModel.ExcelToolModes,
            static option => option.Mode == ExcelToolMode.Compress);
        viewModel.SelectedExcelImageCompressionProfile = AssertSingle(
            viewModel.ExcelImageCompressionProfiles,
            static option => option.Level == DocPivot.Core.Excel.ExcelImageCompressionLevel.Balanced);
        viewModel.AddPaths(
        [
            CreateFixture(fixtureRoot, "季度销售汇总.xlsx"),
            CreateFixture(fixtureRoot, "产品图册与报价.xlsx"),
        ]);
        viewModel.StatusMessage = "Excel 兼容性预检已完成";
    }

    private static void ConfigureExcelMergeScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.SelectedExcelToolMode = AssertSingle(
            viewModel.ExcelToolModes,
            static option => option.Mode == ExcelToolMode.Merge);
        viewModel.AddPaths(
        [
            CreateFixture(fixtureRoot, "华东季度销售.xlsx"),
            CreateFixture(fixtureRoot, "华南季度销售.xlsx"),
        ]);
        viewModel.StatusMessage = "合并名称预览已生成";
    }

    private static void ConfigurePdfToExcelScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfToExcel);
        viewModel.AddPaths([CreateFixture(fixtureRoot, "扫描报表与明细.pdf")]);
        viewModel.StatusMessage = "PDF 表格提取参数已就绪";
    }

    private static void ConfigurePdfVisualSplitScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        viewModel.SelectedPdfToolMode = AssertSingle(
            viewModel.PdfToolModes,
            static option => option.Mode == PdfToolMode.Split);
        viewModel.SelectedPdfSplitMode = AssertSingle(
            viewModel.PdfSplitModes,
            static option => option.Mode == PdfSplitMode.VisualCuts);
        viewModel.AddPaths([CreateFixture(fixtureRoot, "pdf-split-visual.pdf")]);

        var thumbnails = CreatePdfThumbnailFixtures(fixtureRoot, count: 6);
        for (var index = 0; index < thumbnails.Count; index++)
        {
            viewModel.PdfPageThumbnails.Add(new PdfPageThumbnailViewModel(index + 1, thumbnails[index])
            {
                IsCutBefore = index is 2 or 4,
            });
        }

        viewModel.PdfSplitOutputAsZip = true;
        viewModel.StatusMessage = "PDF 可视化拆分预览已就绪";
    }

    private static void ConfigureBatchRenameScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        ConfigureBatchRenameFiles(viewModel, fixtureRoot);
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.BatchRename);
        viewModel.RenameSearchText = "季度";
        viewModel.RenameReplacementText = "2026-Q3";
        viewModel.StatusMessage = "重命名预览已更新";
    }

    private static void ConfigureBatchRenameInsertScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        ConfigureBatchRenameFiles(viewModel, fixtureRoot);
        viewModel.SelectedRenameToolMode = AssertSingle(
            viewModel.RenameToolModes,
            static option => option.Mode == RenameToolMode.Insert);
        viewModel.RenameInsertText = "_归档";
        viewModel.SelectedRenameInsertPosition = AssertSingle(
            viewModel.RenamePositions,
            static option => option.Position == RenameInsertPosition.SpecifiedIndex);
        viewModel.RenameInsertIndex = 2;
    }

    private static void ConfigureBatchRenameNumberingScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        ConfigureBatchRenameFiles(viewModel, fixtureRoot);
        viewModel.SelectedRenameToolMode = AssertSingle(
            viewModel.RenameToolModes,
            static option => option.Mode == RenameToolMode.Numbering);
        viewModel.RenameNumberingEnabled = true;
        viewModel.RenameNumberingStart = 1;
        viewModel.RenameNumberingStep = 1;
        viewModel.RenameNumberingPadding = 3;
        viewModel.RenameNumberingPrefix = "DOC-";
        viewModel.SelectedRenameNumberingPosition = AssertSingle(
            viewModel.RenamePositions,
            static option => option.Position == RenameInsertPosition.Beginning);
    }

    private static void ConfigureBatchRenameManualScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        ConfigureBatchRenameFiles(viewModel, fixtureRoot);
        viewModel.SelectedRenameToolMode = AssertSingle(
            viewModel.RenameToolModes,
            static option => option.Mode == RenameToolMode.Manual);
        viewModel.RenameManualNamesText = "经营分析-华东.docx\n预算汇总-华东.xlsx\n审阅材料-华东.pdf";
        viewModel.ApplyRenameManualNamesCommand.Execute(null);
    }

    private static void ConfigureBatchRenameFiles(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.BatchRename);
        viewModel.AddPaths(
        [
            CreateFixture(fixtureRoot, "季度经营分析报告（华东区域）.docx"),
            CreateFixture(fixtureRoot, "季度预算汇总.xlsx"),
            CreateFixture(fixtureRoot, "季度审阅材料.pdf"),
        ]);
    }

    private static T AssertSingle<T>(
        IEnumerable<T> values,
        Func<T, bool> predicate)
    {
        var matches = values.Where(predicate).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException("UI snapshot scenario selection was ambiguous.");
    }

    private static string CreateFixture(string fixtureRoot, string fileName)
    {
        var path = Path.Combine(fixtureRoot, fileName);
        File.WriteAllText(path, "DocPivot UI snapshot fixture");
        return path;
    }

    private static List<string> CreatePdfThumbnailFixtures(string fixtureRoot, int count)
    {
        var paths = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            const int width = 180;
            const int height = 240;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = y * stride + x * 4;
                    var isBorder = x < 2 || x >= width - 2 || y < 2 || y >= height - 2;
                    var isHeader = y is >= 22 and < 34 && x is >= 18 and < 128;
                    var isAccent = y is >= 54 and < 112 && x is >= 18 and < 162;
                    var channel = isBorder ? (byte)174 : (byte)238;
                    pixels[offset] = isAccent ? (byte)(84 + index * 4) : channel;
                    pixels[offset + 1] = isAccent ? (byte)(116 + index * 3) : channel;
                    pixels[offset + 2] = isAccent ? (byte)216 : isHeader ? (byte)156 : channel;
                    pixels[offset + 3] = byte.MaxValue;
                }
            }

            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(fixtureRoot, $"pdf-page-{index + 1:D2}.png");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            paths.Add(path);
        }

        return paths;
    }

    private static void PrepareOutputDirectory(string repositoryRoot, string outputDirectory)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedOutput = Path.GetFullPath(outputDirectory);
        if (!normalizedOutput.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean a UI snapshot directory outside the repository.");
        }

        if (Directory.Exists(normalizedOutput))
        {
            Directory.Delete(normalizedOutput, recursive: true);
        }

        Directory.CreateDirectory(normalizedOutput);
    }

    private static void DeleteFixtureDirectory(string fixtureRoot)
    {
        var normalizedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DocPivot.UiSnapshot"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedFixture = Path.GetFullPath(fixtureRoot);
        if (normalizedFixture.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(normalizedFixture))
        {
            Directory.Delete(normalizedFixture, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DocPivot repository root.");
    }

    private sealed class FakeOfficeWorkerClient : IOfficeWorkerClient
    {
        public Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OfficeEngineStatus(true, "Microsoft Office", "2024", "x64", null));

        public Task<ExcelWorksheetListResult> ListExcelWorksheetsAsync(
            string inputPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExcelWorksheetListResult.Succeeded(
            [
                new ExcelWorksheetDescriptor("总览", 1, "visible", true),
                new ExcelWorksheetDescriptor("季度销售明细（华东区域）", 2, "visible", true),
                new ExcelWorksheetDescriptor("内部参数", 3, "hidden", false),
            ],
            new ExcelWorkbookCompatibilityReport(
                WorksheetCount: 3,
                HiddenWorksheetCount: 1,
                ChartSheetCount: 0,
                ExternalLinkCount: 0,
                HasVbaProject: false,
                Uses1904DateSystem: false,
                CompressionRisks: ["嵌入式图表", "工作簿级名称"])));

        public Task<OfficeConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            OfficeConversionOptions? options = null,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OfficeConversionResult.Succeeded([outputPath]));
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public IReadOnlyList<string> PickFiles(DocumentOperation operation) => [];

        public string? PickFolder(string initialDirectory) => null;
    }

    private sealed class FakePdfOperationsClient : IPdfOperationsClient
    {
        public Task<PdfPreflightResult> PreflightAsync(
            PdfPreflightRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PdfPreflightResult.Succeeded(
                request.InputPath,
                pageCount: 1,
                hasSignatureFields: false));

        public Task<OperationExecutionResult> MergeAsync(
            PdfMergeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath]));

        public Task<OperationExecutionResult> SplitAsync(
            PdfSplitRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([]));

        public Task<OperationExecutionResult> OptimizeAsync(
            PdfOptimizeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath]));
    }

    private sealed class FakeExcelOperationsClient : IExcelOperationsClient
    {
        public Task<OperationExecutionResult> ExecuteAsync(
            ExcelOperationRequest request,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([request.Output]));
    }

    private sealed class FakePdfTableOperationsClient : IPdfTableOperationsClient
    {
        public Task<PdfTableEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PdfTableEngineStatus(true, true, true, null, null));

        public Task<OperationExecutionResult> ConvertAsync(
            PdfToExcelRequest request,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath]));
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
}
