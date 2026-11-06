using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tests.Office;

public sealed class ExcelWorksheetPreviewServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task RenderPreviewsAsync_CachesAcrossInstancesAndInvalidatesOnFileOrWidthChanges()
    {
        using var files = new PreviewFiles();
        var worker = new PreviewWorker();
        var renderer = new PreviewRenderer();
        var service = new ExcelWorksheetPreviewService(renderer, worker.RunAsync);

        var first = await service.RenderPreviewsAsync(files.Input, files.Output, 240, CancellationToken.None);
        var otherService = new ExcelWorksheetPreviewService(renderer, worker.RunAsync);
        var cached = await otherService.RenderPreviewsAsync(files.Input, files.Output, 240, CancellationToken.None);
        Assert.Equal(first, cached);
        Assert.Equal(1, worker.ExportCount);
        Assert.Equal(240, worker.LastPixelWidth);
        Assert.Single(renderer.Calls);

        await service.RenderPreviewsAsync(files.Input, files.Output, 480, CancellationToken.None);
        Assert.Equal(2, worker.ExportCount);
        Assert.Equal(480, worker.LastPixelWidth);
        await File.AppendAllTextAsync(files.Input, "updated");
        await service.RenderPreviewsAsync(files.Input, files.Output, 480, CancellationToken.None);
        Assert.Equal(3, worker.ExportCount);
    }

    [Fact]
    public async Task RenderPreviewsAsync_PreservesOrderAndIsolatesRenderAndResizeFailures()
    {
        using var files = new PreviewFiles();
        var worker = new PreviewWorker
        {
            Worksheets =
            [
                new("first", 1, "first.pdf", 4),
                new("chart", 3, null, null),
                new("render-failed", 4, "render-failed.pdf", 1),
                new("resize-failed", 6, "resize-failed.pdf", 2),
            ],
            FailedResizePosition = "6",
        };
        var renderer = new PreviewRenderer { FailingPdf = "render-failed.pdf" };
        var service = new ExcelWorksheetPreviewService(renderer, worker.RunAsync);

        var previews = await service.RenderPreviewsAsync(files.Input, files.Output, 320, CancellationToken.None);

        Assert.Equal([1, 3, 4, 6], previews.Select(static preview => preview.Position));
        Assert.Equal("first", previews[0].Name);
        Assert.NotNull(previews[0].ImagePath);
        Assert.Equal(4, previews[0].PageCount);
        Assert.All(previews.Skip(1), static preview => Assert.Null(preview.ImagePath));
        Assert.All(renderer.Calls, static call => Assert.Equal((1, 1), (call.PageCount, call.FirstPage)));
    }

    [Fact]
    public async Task RenderPreviewsAsync_MissingCachedImageTriggersRegeneration()
    {
        using var files = new PreviewFiles();
        var worker = new PreviewWorker();
        var service = new ExcelWorksheetPreviewService(new PreviewRenderer(), worker.RunAsync);
        var previews = await service.RenderPreviewsAsync(files.Input, files.Output, 200, CancellationToken.None);
        File.Delete(Assert.Single(previews).ImagePath!);

        var regenerated = await service.RenderPreviewsAsync(files.Input, files.Output, 200, CancellationToken.None);

        Assert.Equal(2, worker.ExportCount);
        Assert.True(File.Exists(Assert.Single(regenerated).ImagePath));
    }

    [Fact]
    public async Task RenderPreviewsAsync_CancellationDoesNotCachePartialResults()
    {
        using var files = new PreviewFiles();
        using var cancellation = new CancellationTokenSource();
        var worker = new PreviewWorker();
        var renderer = new PreviewRenderer { Cancel = cancellation };
        var service = new ExcelWorksheetPreviewService(renderer, worker.RunAsync);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RenderPreviewsAsync(files.Input, files.Output, 200, cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(files.Output, "previews.json", SearchOption.AllDirectories));
        renderer.Cancel = null;
        await service.RenderPreviewsAsync(files.Input, files.Output, 200, CancellationToken.None);
        Assert.Equal(2, worker.ExportCount);
    }

    [Fact]
    public async Task RenderPreviewsAsync_WorkerFailureDoesNotBecomeAnEmptySuccessfulCache()
    {
        using var files = new PreviewFiles();
        var service = new ExcelWorksheetPreviewService(new PreviewRenderer(), (request, _) =>
        {
            var previewRequest = Assert.IsType<WorkerExcelPreviewStartMessage>(request);
            return Task.FromResult(Response(WorkerErrorMessage.Create(previewRequest.JobId,
                "EXCEL_NOT_INSTALLED", "Excel unavailable", false), 2));
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenderPreviewsAsync(files.Input, files.Output, 200, CancellationToken.None));

        Assert.Equal("Excel unavailable", error.Message);
        Assert.Empty(Directory.EnumerateFiles(files.Output, "previews.json", SearchOption.AllDirectories));
    }

    private static ExternalProcessResult Response(object message, int exitCode = 0) =>
        new(exitCode, JsonSerializer.Serialize(message, JsonOptions), "");

    private sealed class PreviewWorker
    {
        public IReadOnlyList<WorkerExcelWorksheetPreview> Worksheets { get; init; } = [new("sheet", 1, "sheet.pdf", 3)];
        public string? FailedResizePosition { get; init; }
        public int ExportCount { get; private set; }
        public int LastPixelWidth { get; private set; }

        public Task<ExternalProcessResult> RunAsync(object request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is WorkerExcelPreviewStartMessage export)
            {
                ExportCount++;
                return Task.FromResult(Response(WorkerExcelPreviewsMessage.Create(export.JobId, Worksheets)));
            }

            var resize = Assert.IsType<WorkerExcelPreviewResizeStartMessage>(request);
            LastPixelWidth = resize.PixelWidth;
            var artifacts = resize.ImagePaths.Where(path =>
                Path.GetFileName(Path.GetDirectoryName(path)) != FailedResizePosition).ToArray();
            return Task.FromResult(Response(WorkerResultMessage.Succeeded(resize.JobId, artifacts)));
        }
    }

    private sealed class PreviewRenderer : IPdfThumbnailRenderer
    {
        public List<(int PageCount, int FirstPage)> Calls { get; } = [];
        public string? FailingPdf { get; init; }
        public CancellationTokenSource? Cancel { get; set; }

        public Task<IReadOnlyList<string>> RenderAsync(string inputPath, int pageCount, string outputDirectory,
            string? password = null, int firstPage = 1, CancellationToken cancellationToken = default)
        {
            Calls.Add((pageCount, firstPage));
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (inputPath == FailingPdf)
            {
                throw new InvalidOperationException("Synthetic render failure");
            }

            Directory.CreateDirectory(outputDirectory);
            var imagePath = Path.Combine(outputDirectory, "page-0001.png");
            File.WriteAllBytes(imagePath, [1]);
            return Task.FromResult<IReadOnlyList<string>>([imagePath]);
        }
    }

    private sealed class PreviewFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"DocPivot-preview-tests-{Guid.NewGuid():N}");

        public PreviewFiles()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Input, "synthetic workbook identity");
        }

        public string Input => Path.Combine(_root, "workbook.xlsx");
        public string Output => Path.Combine(_root, "previews");
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
