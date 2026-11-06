using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Pdf;

namespace DocPivot.App.ViewModels;

/// <summary>
/// The compression slider answers "how small will it get?" while it is being dragged: each queued file
/// is estimated at the chosen strength (debounced, previous request cancelled) and the totals are shown
/// next to the slider as the size after compression and the share saved.
/// </summary>
public partial class WorkspaceViewModel
{
    private static readonly TimeSpan EstimateDebounce = TimeSpan.FromMilliseconds(250);

    private static readonly string[] EstimateDependencies =
    [
        nameof(PdfCompressionStrength),
        nameof(ExcelCompressionStrength),
        nameof(QueueCountText),
        nameof(SelectedTool),
        nameof(SelectedPdfToolMode),
        nameof(SelectedExcelToolMode),
        nameof(IsProcessing),
    ];

    private readonly IPdfCompressionEstimator? _pdfCompressionEstimator;
    private readonly IExcelCompressionEstimator? _excelCompressionEstimator;
    private CancellationTokenSource? _estimateCancellation;

    [ObservableProperty]
    private bool _isEstimatingCompression;

    [ObservableProperty]
    private bool _hasCompressionEstimate;

    [ObservableProperty]
    private string _compressionEstimateText = string.Empty;

    [ObservableProperty]
    private string _compressionOriginalText = string.Empty;

    [ObservableProperty]
    private string _compressionRatioText = string.Empty;

    private Func<QueuedFileViewModel, int, CancellationToken, Task<(long Original, long Estimated)>>? CurrentEstimator
    {
        get
        {
            if (IsPdfCompressMode && _pdfCompressionEstimator is { } pdf)
            {
                return async (file, strength, token) =>
                {
                    var estimate = await pdf.EstimateAsync(file.FullPath, strength, null, token);
                    return (estimate.OriginalBytes, estimate.EstimatedBytes);
                };
            }

            if (IsExcelCompressMode && _excelCompressionEstimator is { } excel)
            {
                return async (file, strength, token) =>
                {
                    var estimate = await excel.EstimateAsync(file.FullPath, strength, token);
                    return (estimate.OriginalBytes, estimate.EstimatedBytes);
                };
            }

            return null;
        }
    }

    private int CurrentCompressionStrength => IsExcelCompressMode ? ExcelCompressionStrength : PdfCompressionStrength;

    private void OnEstimateDependencyChanged(PropertyChangedEventArgs e)
    {
        if ((_pdfCompressionEstimator is not null || _excelCompressionEstimator is not null) &&
            e.PropertyName is not null &&
            Array.IndexOf(EstimateDependencies, e.PropertyName) >= 0)
        {
            ScheduleCompressionEstimate();
        }
    }

    private void ScheduleCompressionEstimate()
    {
        _estimateCancellation?.Cancel();
        _estimateCancellation = null;
        var estimator = CurrentEstimator;
        var files = Files
            .Where(static file => !file.HasPdfPreflightFailure && !file.HasExcelToolPreflightFailure)
            .ToArray();
        if (_isDisposed || estimator is null || files.Length == 0 || IsProcessing)
        {
            IsEstimatingCompression = false;
            HasCompressionEstimate = false;
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _estimateCancellation = cancellation;
        IsEstimatingCompression = true;
        _ = EstimateCompressionAsync(estimator, files, CurrentCompressionStrength, cancellation);
    }

    private async Task EstimateCompressionAsync(
        Func<QueuedFileViewModel, int, CancellationToken, Task<(long Original, long Estimated)>> estimator,
        QueuedFileViewModel[] files,
        int strength,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(EstimateDebounce, token);
            long original = 0;
            long estimated = 0;
            foreach (var file in files)
            {
                var (fileOriginal, fileEstimated) = await estimator(file, strength, token);
                original += fileOriginal;
                estimated += Math.Min(fileEstimated, fileOriginal);
            }

            ShowCompressionEstimate(original, estimated);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer slider position, or the window is closing.
        }
        catch (Exception)
        {
            // An estimate is a convenience; when it cannot be made the slider still works.
            HasCompressionEstimate = false;
        }
        finally
        {
            if (ReferenceEquals(_estimateCancellation, cancellation))
            {
                _estimateCancellation = null;
                IsEstimatingCompression = false;
            }

            cancellation.Dispose();
        }
    }

    private void ShowCompressionEstimate(long original, long estimated)
    {
        CompressionEstimateText = $"约 {FormatBytes(estimated)}";
        CompressionOriginalText = $"原 {FormatBytes(original)}";
        var saved = original <= 0 ? 0 : (int)Math.Round((original - estimated) * 100d / original);
        CompressionRatioText = saved <= 0 ? "几乎不变" : $"减少 {saved}%";
        HasCompressionEstimate = true;
    }

    private void CancelCompressionEstimate()
    {
        _estimateCancellation?.Cancel();
        _estimateCancellation = null;
    }
}
