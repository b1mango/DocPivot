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
    private const int PdfPreviewBatchSize = 24;
    private int _pdfPreviewBatchIndex;
    private string? _pdfSplitPreviewSource;
    private int? _pdfSplitPreviewPageCount;
    private readonly SortedSet<int> _pdfCutPages = [];
    private readonly Dictionary<int, PdfPageThumbnailViewModel[]> _pdfPreviewBatches = [];
    private readonly LinkedList<int> _pdfPreviewBatchUsage = [];

    public int PdfPreviewTotalPages => IsPdfVisualSplitMode ? Files.FirstOrDefault()?.PdfPageCount ?? 0 : 0;
    public bool ShowPdfPreviewPagination => IsPdfVisualSplitMode && PdfPreviewTotalPages > PdfPreviewBatchSize;
    public string PdfPreviewPageRange => $"第 {_pdfPreviewBatchIndex * PdfPreviewBatchSize + 1}–{Math.Min((_pdfPreviewBatchIndex + 1) * PdfPreviewBatchSize, PdfPreviewTotalPages)} 页 / 共 {PdfPreviewTotalPages} 页";

    [RelayCommand(CanExecute = nameof(CanPreviousPdfPreviewBatch))]
    private void PreviousPdfPreviewBatch() => ChangePdfPreviewBatch(-1);

    [RelayCommand(CanExecute = nameof(CanNextPdfPreviewBatch))]
    private void NextPdfPreviewBatch() => ChangePdfPreviewBatch(1);

    private bool CanPreviousPdfPreviewBatch() => IsPdfVisualSplitMode && !IsPdfPreviewLoading && !IsProcessing && _pdfPreviewBatchIndex > 0;
    private bool CanNextPdfPreviewBatch() => IsPdfVisualSplitMode && !IsPdfPreviewLoading && !IsProcessing && (_pdfPreviewBatchIndex + 1) * PdfPreviewBatchSize < PdfPreviewTotalPages;

    private void ChangePdfPreviewBatch(int delta)
    {
        _pdfPreviewBatchIndex += delta;
        LoadPdfSplitBatch();
    }

    private void NotifyPdfPreviewPagination()
    {
        OnPropertyChanged(nameof(ShowPdfPreviewPagination));
        OnPropertyChanged(nameof(PdfPreviewPageRange));
        PreviousPdfPreviewBatchCommand.NotifyCanExecuteChanged();
        NextPdfPreviewBatchCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<PdfPageThumbnailViewModel> PdfPageThumbnails { get; }

    /// <summary>
    /// Merge mode always uses the visual list once files are queued (thumbnails fill in
    /// per row as they render); split mode needs its page thumbnails before switching.
    /// </summary>
    public bool ShowPdfVisualPreview =>
        IsPdfVisualSplitMode && PdfPageThumbnails.Count > 0 || IsPdfMergeMode && HasFiles;

    public string PdfVisualSplitSummary
    {
        get
        {
            if (IsPdfMergeMode)
            {
                return GetPdfMergeSummary();
            }

            var outputCount = _pdfCutPages.Count + 1;
            return $"{PdfPreviewTotalPages} 页 · 将输出 {outputCount} 个 PDF";
        }
    }

    private string GetPdfMergeSummary()
    {
        var parts = new List<string> { $"{Files.Count} 个文件" };
        var pageCount = Files.Sum(static file => file.PdfPageCount ?? 0);
        if (pageCount > 0)
        {
            parts.Add($"共 {pageCount} 页");
        }

        var checkingCount = Files.Count(static file => file.IsPdfPreflightLoading);
        if (checkingCount > 0)
        {
            parts.Add($"{checkingCount} 个正在检查");
        }

        var failedCount = Files.Count(static file => file.HasPdfPreflightFailure);
        if (failedCount > 0)
        {
            parts.Add($"{failedCount} 个无法读取");
        }

        return string.Join(" · ", parts);
    }

    [RelayCommand(CanExecute = nameof(CanTogglePdfCut))]
    private void TogglePdfCut(PdfPageThumbnailViewModel? page)
    {
        if (page is null)
        {
            return;
        }

        page.IsCutBefore = !page.IsCutBefore;
        if (page.IsCutBefore) _pdfCutPages.Add(page.PageNumber);
        else _pdfCutPages.Remove(page.PageNumber);
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
        if (!_pdfThumbnailIsMergeMode)
        {
            _pdfThumbnailCancellation?.Cancel();
            _pdfThumbnailCancellation = null;
            var previousDirectory = _pdfThumbnailDirectory;
            if (previousDirectory is not null)
                _ = _pdfThumbnailTask.ContinueWith(_ => TryDeletePreviewDirectory(previousDirectory), TaskScheduler.Default);
            _pdfThumbnailDirectory = null;
            _pdfPreviewBatches.Clear();
            _pdfPreviewBatchUsage.Clear();
            PdfPageThumbnails.Clear();
        }
        NotifyPdfPreviewPagination();
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
        var oldDirectory = _pdfThumbnailDirectory;
        if (oldDirectory is not null)
        {
            var oldTask = _pdfThumbnailTask;
            _ = oldTask.ContinueWith(_ => TryDeletePreviewDirectory(oldDirectory), TaskScheduler.Default);
        }
        _pdfThumbnailDirectory = null;
        _pdfPreviewBatches.Clear();
        _pdfPreviewBatchUsage.Clear();
        PdfPageThumbnails.Clear();
        _pdfPreviewBatchIndex = 0;
        var file = Files.FirstOrDefault();
        if (!string.Equals(_pdfSplitPreviewSource, file?.FullPath, StringComparison.OrdinalIgnoreCase) ||
            _pdfSplitPreviewPageCount != file?.PdfPageCount)
            _pdfCutPages.Clear();
        _pdfSplitPreviewSource = file?.FullPath;
        _pdfSplitPreviewPageCount = file?.PdfPageCount;
        if (_pdfThumbnailRenderer is null || !IsPdfVisualSplitMode || file?.PdfPageCount is null || !file.IsPdfPreflightResolved)
        {
            IsPdfPreviewLoading = false;
            NotifyPdfPreviewPagination();
            OnPropertyChanged(nameof(ShowPdfVisualPreview));
            return;
        }
        _pdfThumbnailDirectory = Path.Combine(Path.GetTempPath(), "DocPivot", "pdf-preview", Guid.NewGuid().ToString("N"));
        _pdfThumbnailIsMergeMode = false;
        LoadPdfSplitBatch();
    }

    private void LoadPdfSplitBatch()
    {
        if (_pdfPreviewBatches.TryGetValue(_pdfPreviewBatchIndex, out var cached))
        {
            ShowPdfSplitBatch(cached);
            return;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _pdfThumbnailCancellation = cancellation;
        IsPdfPreviewLoading = true;
        NotifyPdfPreviewPagination();
        _pdfThumbnailTask = LoadPdfSplitBatchAsync(Files[0], _pdfThumbnailDirectory!, _pdfPreviewBatchIndex, cancellation);
    }

    private void ShowPdfSplitBatch(PdfPageThumbnailViewModel[] pages)
    {
        PdfPageThumbnails.Clear();
        foreach (var page in pages)
        {
            page.IsCutBefore = _pdfCutPages.Contains(page.PageNumber);
            PdfPageThumbnails.Add(page);
        }
        _pdfPreviewBatchUsage.Remove(_pdfPreviewBatchIndex);
        _pdfPreviewBatchUsage.AddLast(_pdfPreviewBatchIndex);
        while (_pdfPreviewBatchUsage.Count > 3)
        {
            var expired = _pdfPreviewBatchUsage.First!.Value;
            _pdfPreviewBatchUsage.RemoveFirst();
            _pdfPreviewBatches.Remove(expired);
            TryDeletePreviewDirectory(Path.Combine(_pdfThumbnailDirectory!, $"batch-{expired}"));
        }
        IsPdfPreviewLoading = false;
        NotifyPdfPreviewPagination();
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
        OnPropertyChanged(nameof(PdfVisualSplitSummary));
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadPdfSplitBatchAsync(QueuedFileViewModel file, string directory, int batch, CancellationTokenSource cancellation)
    {
        var batchDirectory = Path.Combine(directory, $"batch-{batch}");
        var completed = false;
        try
        {
            var firstPage = batch * PdfPreviewBatchSize + 1;
            var count = Math.Min(PdfPreviewBatchSize, file.PdfPageCount!.Value - firstPage + 1);
            var paths = await _pdfThumbnailRenderer!.RenderAsync(file.FullPath, count, batchDirectory,
                cancellationToken: cancellation.Token, firstPage: firstPage);
            cancellation.Token.ThrowIfCancellationRequested();
            // Decode away from the dispatcher; each bitmap is frozen before publication.
            var pages = await Task.Run(() => paths.Select((path, index) =>
                new PdfPageThumbnailViewModel(firstPage + index, path, 0, file.PdfRotation, file.FullPath, null)).ToArray(), cancellation.Token);
            if (!ReferenceEquals(_pdfThumbnailCancellation, cancellation)) return;
            _pdfPreviewBatches[batch] = pages;
            ShowPdfSplitBatch(pages);
            PdfPreviewStatus = "点击页间剪刀设置拆分位置，翻页保留切点";
            completed = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            if (ReferenceEquals(_pdfThumbnailCancellation, cancellation))
            {
                _pdfPreviewBatchIndex = _pdfPreviewBatchUsage.Last?.Value ?? 0;
                PdfPreviewStatus = "此批预览生成失败，请重新检查 PDF 后重试";
            }
        }
        finally
        {
            if (ReferenceEquals(_pdfThumbnailCancellation, cancellation))
            {
                _pdfThumbnailCancellation = null;
                IsPdfPreviewLoading = false;
                NotifyPdfPreviewPagination();
            }
            cancellation.Dispose();
            if (!completed) TryDeletePreviewDirectory(batchDirectory);
        }
    }

    private void RefreshPdfVisualOrder() => RunOnUiThread(RefreshPdfVisualOrderCore);

    private void RefreshPdfVisualOrderCore()
    {
        if (!IsPdfMergeMode)
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

        foreach (var file in Files)
        {
            file.PdfPreview = PdfPageThumbnails.FirstOrDefault(page =>
                string.Equals(page.SourceFilePath, file.FullPath, StringComparison.OrdinalIgnoreCase));
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
                    cancellationToken: cancellation.Token);
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
