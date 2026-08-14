using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocPivot.App.Services;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Excel;
using DocPivot.Core.Jobs;
using DocPivot.Core.Pdf;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Renaming;
using DocPivot.Infrastructure.Storage;
using DocPivot.Infrastructure.Tables;

namespace DocPivot.App.ViewModels;

public partial class WorkspaceViewModel
{
    private Task _pdfThumbnailTask = Task.CompletedTask;
    private CancellationTokenSource? _pdfThumbnailCancellation;
    private string? _pdfThumbnailDirectory;
    private bool _pdfThumbnailIsMergeMode;

    public ObservableCollection<PdfPageThumbnailViewModel> PdfPageThumbnails { get; }

    public bool ShowPdfVisualPreview =>
        (IsPdfVisualSplitMode || IsPdfMergeMode) && PdfPageThumbnails.Count > 0;

    public string PdfVisualSplitSummary
    {
        get
        {
            if (IsPdfMergeMode)
            {
                return $"{PdfPageThumbnails.Count} 个文件 · 可拖动调整顺序";
            }

            var outputCount = PdfPageThumbnails.Count(static page => page.IsCutBefore) + 1;
            return $"{PdfPageThumbnails.Count} 页 · 将输出 {outputCount} 个 PDF";
        }
    }

    [RelayCommand(CanExecute = nameof(CanTogglePdfCut))]
    private void TogglePdfCut(PdfPageThumbnailViewModel? page)
    {
        if (page is null)
        {
            return;
        }

        page.IsCutBefore = !page.IsCutBefore;
        OnPropertyChanged(nameof(PdfVisualSplitSummary));
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    private bool CanTogglePdfCut(PdfPageThumbnailViewModel? page) =>
        page?.CanCutBefore == true && IsPdfVisualSplitMode && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanRotatePdfPage))]
    private void RotatePdfPage(PdfPageThumbnailViewModel? page)
    {
        if (page is null)
        {
            return;
        }

        if (page.SourceFileIndex < 0 || page.SourceFileIndex >= Files.Count)
        {
            return;
        }

        var sourceFile = Files[page.SourceFileIndex];
        var newRotation = (sourceFile.PdfRotation + 90) % 360;
        sourceFile.SetPdfRotation(newRotation);
        foreach (var sibling in PdfPageThumbnails.Where(item => item.SourceFileIndex == page.SourceFileIndex))
        {
            sibling.Rotation = newRotation;
        }

        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    private bool CanRotatePdfPage(PdfPageThumbnailViewModel? page) =>
        page is not null && IsPdfMergeMode && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanRotatePdfFile))]
    private void RotatePdfFile(QueuedFileViewModel? file)
    {
        if (file is null || !IsPdfMergeMode || IsProcessing)
        {
            return;
        }

        var newRotation = (file.PdfRotation + 90) % 360;
        file.SetPdfRotation(newRotation);
        foreach (var page in PdfPageThumbnails.Where(item => item.SourceFileIndex == Files.IndexOf(file)))
        {
            page.Rotation = newRotation;
        }
    }

    private bool CanRotatePdfFile(QueuedFileViewModel? file) =>
        file is not null && IsPdfMergeMode && !IsProcessing;

    private void RefreshPdfVisualPreview()
    {
        if (IsPdfMergeMode)
        {
            RefreshPdfMergeVisualPreview();
            return;
        }

        RefreshPdfSplitVisualPreview();
    }

    private void RefreshPdfMergeVisualPreview() => RunOnUiThread(RefreshPdfMergeVisualPreviewCore);

    private void RefreshPdfMergeVisualPreviewCore()
    {
        var previewFiles = Files
            .Where(static file => file.State == JobState.Queued)
            .ToArray();
        var activePaths = previewFiles
            .Select(static file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = PdfPageThumbnails.Count - 1; index >= 0; index--)
        {
            var page = PdfPageThumbnails[index];
            if (page.SourceFilePath is null ||
                !activePaths.Contains(page.SourceFilePath) ||
                !seenPaths.Add(page.SourceFilePath))
            {
                PdfPageThumbnails.RemoveAt(index);
            }
        }

        RefreshPdfVisualOrder();
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
        OnPropertyChanged(nameof(PdfVisualSplitSummary));
        StartProcessingCommand.NotifyCanExecuteChanged();

        if (_pdfThumbnailRenderer is null || previewFiles.Length == 0)
        {
            if (previewFiles.Length == 0)
            {
                _pdfThumbnailCancellation?.Cancel();
                _pdfThumbnailCancellation = null;
                PdfPageThumbnails.Clear();
            }

            PdfPreviewStatus = string.Empty;
            IsPdfPreviewLoading = false;
            return;
        }

        if (_pdfThumbnailCancellation is not null &&
            !_pdfThumbnailTask.IsCompleted &&
            !_pdfThumbnailIsMergeMode)
        {
            _pdfThumbnailCancellation.Cancel();
            _pdfThumbnailCancellation = null;
            _pdfThumbnailDirectory = null;
        }

        var missingFiles = previewFiles
            .Where(file => !PdfPageThumbnails.Any(page =>
                string.Equals(page.SourceFilePath, file.FullPath, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingFiles.Length == 0)
        {
            IsPdfPreviewLoading = false;
            PdfPreviewStatus = "可视化预览已就绪，可拖动文件调整顺序";
            return;
        }

        if (_pdfThumbnailCancellation is not null && !_pdfThumbnailTask.IsCompleted)
        {
            IsPdfPreviewLoading = true;
            PdfPreviewStatus = $"正在生成 {missingFiles.Length} 个首页预览";
            return;
        }

        var directory = _pdfThumbnailDirectory;
        if (directory is null)
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                "DocPivot",
                "pdf-preview",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            _pdfThumbnailDirectory = directory;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _pdfThumbnailCancellation = cancellation;
        _pdfThumbnailIsMergeMode = true;
        IsPdfPreviewLoading = true;
        PdfPreviewStatus = $"正在生成 {missingFiles.Length} 个首页预览";
        _pdfThumbnailTask = LoadPdfVisualPreviewAsync(
            missingFiles,
            directory,
            cancellation);
    }

    private void RefreshPdfSplitVisualPreview() => RunOnUiThread(RefreshPdfSplitVisualPreviewCore);

    private void RefreshPdfSplitVisualPreviewCore()
    {
        _pdfThumbnailCancellation?.Cancel();
        _pdfThumbnailCancellation = null;
        PdfPageThumbnails.Clear();
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
        OnPropertyChanged(nameof(PdfVisualSplitSummary));
        StartProcessingCommand.NotifyCanExecuteChanged();

        var previewFiles = Files.Take(1).ToArray();
        if (_pdfThumbnailRenderer is null ||
            !IsPdfVisualSplitMode ||
            (IsPdfVisualSplitMode && previewFiles.Length != 1) ||
            previewFiles.Length == 0 ||
            previewFiles.Any(static file => file.PdfPageCount is null || !file.IsPdfPreflightResolved))
        {
            PdfPreviewStatus = string.Empty;
            IsPdfPreviewLoading = false;
            return;
        }

        var previousDirectory = _pdfThumbnailDirectory;
        if (previousDirectory is not null && _pdfThumbnailTask.IsCompleted)
        {
            TryDeletePreviewDirectory(previousDirectory);
        }
        _pdfThumbnailDirectory = null;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _pdfThumbnailCancellation = cancellation;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DocPivot",
            "pdf-preview",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _pdfThumbnailDirectory = directory;
        _pdfThumbnailIsMergeMode = false;
        IsPdfPreviewLoading = true;
        PdfPreviewStatus = $"正在生成 {previewFiles.Sum(static file => file.PdfPageCount!.Value)} 页预览";
        _pdfThumbnailTask = LoadPdfVisualPreviewAsync(
            previewFiles,
            directory,
            cancellation);
    }

    private void RefreshPdfVisualOrder() => RunOnUiThread(RefreshPdfVisualOrderCore);

    private void RefreshPdfVisualOrderCore()
    {
        if (!IsPdfMergeMode || PdfPageThumbnails.Count == 0)
        {
            return;
        }

        var orderedPreviews = Files
            .Select(file => PdfPageThumbnails.FirstOrDefault(page =>
                string.Equals(page.SourceFilePath, file.FullPath, StringComparison.OrdinalIgnoreCase)))
            .Where(static preview => preview is not null)
            .Cast<PdfPageThumbnailViewModel>()
            .ToArray();
        for (var targetIndex = 0; targetIndex < orderedPreviews.Length; targetIndex++)
        {
            var preview = orderedPreviews[targetIndex];
            var sourceIndex = Files.ToList().FindIndex(file =>
                string.Equals(file.FullPath, preview.SourceFilePath, StringComparison.OrdinalIgnoreCase));

            preview.UpdateSourceFileIndex(sourceIndex);
            var currentIndex = PdfPageThumbnails.IndexOf(preview);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                PdfPageThumbnails.Move(currentIndex, targetIndex);
            }
        }

        OnPropertyChanged(nameof(PdfVisualSplitSummary));
    }

    private async Task LoadPdfVisualPreviewAsync(
        QueuedFileViewModel[] files,
        string directory,
        CancellationTokenSource cancellation)
    {
        var completed = false;
        var renderFirstPageOnly = IsPdfMergeMode;
        try
        {
            var thumbnails = new List<(string Path, int SourceFileIndex)>();
            for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
            {
                var file = files[fileIndex];
                var fileDirectory = renderFirstPageOnly
                    ? Path.Combine(directory, "merge", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
                    : Path.Combine(directory, fileIndex.ToString(CultureInfo.InvariantCulture));
                var rendered = await _pdfThumbnailRenderer!.RenderAsync(
                    file.FullPath,
                    renderFirstPageOnly ? 1 : file.PdfPageCount!.Value,
                    fileDirectory,
                    null,
                    cancellation.Token);
                var selectedPages = renderFirstPageOnly ? rendered.Take(1) : rendered;
                foreach (var path in selectedPages)
                {
                    if (!File.Exists(path))
                    {
                        throw new IOException($"The PDF preview image was not created: {path}");
                    }

                    if (!renderFirstPageOnly)
                    {
                        thumbnails.Add((path, fileIndex));
                        continue;
                    }

                    RunOnUiThread(() =>
                    {
                        if (!ReferenceEquals(_pdfThumbnailCancellation, cancellation) ||
                            !Files.Contains(file) ||
                            PdfPageThumbnails.Any(page =>
                                string.Equals(page.SourceFilePath, file.FullPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            return;
                        }

                        var sourceIndex = Files.IndexOf(file);
                        if (sourceIndex < 0)
                        {
                            return;
                        }

                        PdfPageThumbnails.Add(new PdfPageThumbnailViewModel(
                            1,
                            path,
                            sourceIndex,
                            file.PdfRotation,
                            file.FullPath,
                            file.FileName));
                        RefreshPdfVisualOrder();
                        OnPropertyChanged(nameof(ShowPdfVisualPreview));
                        OnPropertyChanged(nameof(PdfVisualSplitSummary));
                        StartProcessingCommand.NotifyCanExecuteChanged();
                        PdfPreviewStatus = "正在继续生成首页预览";
                    });
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();

            if (!renderFirstPageOnly)
            {
                RunOnUiThread(() =>
                {
                    for (var index = 0; index < thumbnails.Count; index++)
                    {
                        PdfPageThumbnails.Add(new PdfPageThumbnailViewModel(
                            index + 1,
                            thumbnails[index].Path,
                            thumbnails[index].SourceFileIndex,
                            files[thumbnails[index].SourceFileIndex].PdfRotation,
                            files[thumbnails[index].SourceFileIndex].FullPath,
                            files[thumbnails[index].SourceFileIndex].FileName));
                    }
                });
            }

            completed = true;
            RunOnUiThread(() =>
            {
                PdfPreviewStatus = "点击页间剪刀设置拆分位置";
                if (IsPdfMergeMode)
                {
                    PdfPreviewStatus = "可视化预览已就绪，可拖动文件卡片调整顺序";
                }

                OnPropertyChanged(nameof(ShowPdfVisualPreview));
                OnPropertyChanged(nameof(PdfVisualSplitSummary));
                StartProcessingCommand.NotifyCanExecuteChanged();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Queue changes and shutdown intentionally cancel stale previews.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PdfPreview] render failed: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
            if (ReferenceEquals(_pdfThumbnailCancellation, cancellation))
            {
                PdfPreviewStatus = "无法生成页面预览，请重试 PDF 检查";
            }
        }
        finally
        {
            if (ReferenceEquals(_pdfThumbnailCancellation, cancellation))
            {
                _pdfThumbnailCancellation = null;
                IsPdfPreviewLoading = false;
            }

            cancellation.Dispose();
            if (!completed && !renderFirstPageOnly)
            {
                TryDeletePreviewDirectory(directory);
            }

            if (completed && renderFirstPageOnly &&
                ReferenceEquals(_pdfThumbnailDirectory, directory) &&
                !_isDisposed)
            {
                RefreshPdfMergeVisualPreview();
            }
        }
    }

    private static void TryDeletePreviewDirectory(string directory)
    {
        try
        {
            var previewRoot = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "DocPivot",
                "pdf-preview"));
            var candidate = Path.GetFullPath(directory);
            if (candidate.StartsWith(
                    previewRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(candidate))
            {
                Directory.Delete(candidate, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Preview cleanup is best effort and never changes the job result.
        }
    }

    private static string CreateSplitArchive(
        string inputPath,
        string outputDirectory,
        IReadOnlyList<string> artifacts,
        CancellationToken cancellationToken)
    {
        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException("PDF split did not produce files to archive.");
        }

        var archivePath = CreateUniqueNamedOutputPath(
            outputDirectory,
            $"{Path.GetFileNameWithoutExtension(inputPath)} - 拆分",
            ".zip");
        var stagingPath = AtomicOutputFile.CreateStagingPath(archivePath, Guid.NewGuid());
        try
        {
            using (var archive = ZipFile.Open(stagingPath, ZipArchiveMode.Create))
            {
                foreach (var artifact in artifacts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archive.CreateEntryFromFile(
                        artifact,
                        Path.GetFileName(artifact),
                        CompressionLevel.SmallestSize);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicOutputFile.Commit(stagingPath, archivePath);
            return archivePath;
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static void TryDeleteGeneratedSplitDirectory(
        string? splitDirectory,
        IReadOnlyList<string> artifacts)
    {
        if (splitDirectory is null || !Directory.Exists(splitDirectory))
        {
            return;
        }

        var normalizedDirectory = Path.GetFullPath(splitDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var artifact in artifacts)
        {
            var normalizedArtifact = Path.GetFullPath(artifact);
            if (normalizedArtifact.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(normalizedArtifact);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    return;
                }
            }
        }

        try
        {
            Directory.Delete(splitDirectory, recursive: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A generated directory containing unexpected files is deliberately retained.
        }
    }

    public async Task WaitForPdfPreviewAsync()
    {
        while (true)
        {
            var task = _pdfThumbnailTask;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Queue changes and shutdown intentionally cancel preview rendering.
            }

            if (ReferenceEquals(task, _pdfThumbnailTask) &&
                _pdfThumbnailCancellation is null)
            {
                return;
            }
        }
    }
}
