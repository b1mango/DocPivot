using System.Buffers.Binary;
using System.Globalization;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.PdfWorker;

public sealed record RenderedPdfPage(
    string ImagePath,
    int PixelWidth,
    int PixelHeight,
    int Dpi);

public sealed class GhostscriptPageRenderer
{
    private const int TargetDpi = 300;
    private const int MinimumDpi = 150;
    private const long MaximumPixels = 25_000_000;
    private const int MaximumDimension = 12_000;
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(90);

    private readonly GhostscriptPdfOptimizer _probe;
    private GhostscriptEngineStatus? _status;

    public GhostscriptPageRenderer(string distributionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionRoot);
        _probe = new GhostscriptPdfOptimizer(distributionRoot);
    }

    public async Task<RenderedPdfPage> RenderAsync(
        string inputPath,
        int pageNumber,
        double widthPoints,
        double heightPoints,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(widthPoints, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heightPoints, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var dpi = SelectDpi(widthPoints, heightPoints);
        _status ??= await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!_status.IsReady)
        {
            throw new PdfTableWorkerException(
                _status.ErrorCode ?? "GHOSTSCRIPT_UNAVAILABLE",
                _status.ErrorMessage ?? "The pinned Ghostscript runtime is unavailable.",
                _status.IsRetryable);
        }

        var result = await ExternalProcessRunner.RunAsync(
                _status.ExecutablePath!,
                BuildArguments(inputPath, outputPath, pageNumber, dpi),
                string.Empty,
                PageTimeout,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new PdfTableWorkerException(
                "PDF_PAGE_RENDER_FAILED",
                "Ghostscript could not render a PDF page for offline OCR.");
        }

        var (pixelWidth, pixelHeight) = ReadPngDimensions(outputPath);
        if ((long)pixelWidth * pixelHeight > MaximumPixels ||
            pixelWidth > MaximumDimension ||
            pixelHeight > MaximumDimension)
        {
            throw new PdfTableWorkerException(
                "OCR_PAGE_TOO_LARGE",
                "The rendered PDF page exceeds the OCR pixel safety limit.");
        }

        return new RenderedPdfPage(outputPath, pixelWidth, pixelHeight, dpi);
    }

    private static int SelectDpi(double widthPoints, double heightPoints)
    {
        var widthAtTarget = widthPoints / 72 * TargetDpi;
        var heightAtTarget = heightPoints / 72 * TargetDpi;
        var scaleForPixels = Math.Sqrt(MaximumPixels / (widthAtTarget * heightAtTarget));
        var scaleForDimensions = Math.Min(
            MaximumDimension / widthAtTarget,
            MaximumDimension / heightAtTarget);
        var scale = Math.Min(1, Math.Min(scaleForPixels, scaleForDimensions));
        var dpi = Math.Min(TargetDpi, (int)Math.Floor(TargetDpi * scale));
        if (dpi < MinimumDpi)
        {
            throw new PdfTableWorkerException(
                "OCR_PAGE_TOO_LARGE",
                "The PDF page cannot be rendered at the minimum safe OCR resolution.");
        }

        return dpi;
    }

    private static IReadOnlyList<string> BuildArguments(
        string inputPath,
        string outputPath,
        int pageNumber,
        int dpi) =>
    [
        "-q",
        "-dSAFER",
        "-dBATCH",
        "-dNOPAUSE",
        "-dNOPROMPT",
        "-dUseCropBox",
        "-sDEVICE=pnggray",
        "-dTextAlphaBits=4",
        "-dGraphicsAlphaBits=4",
        $"-r{dpi.ToString(CultureInfo.InvariantCulture)}",
        $"-dFirstPage={pageNumber.ToString(CultureInfo.InvariantCulture)}",
        $"-dLastPage={pageNumber.ToString(CultureInfo.InvariantCulture)}",
        $"-sOutputFile={outputPath}",
        "-f",
        inputPath,
    ];

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.ReadExactly(header);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header[..8].SequenceEqual(signature))
        {
            throw new PdfTableWorkerException(
                "OCR_IMAGE_INVALID",
                "Ghostscript did not produce a valid PNG image.");
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width <= 0 || height <= 0)
        {
            throw new PdfTableWorkerException(
                "OCR_IMAGE_INVALID",
                "The rendered OCR image has invalid dimensions.");
        }

        return (width, height);
    }
}
