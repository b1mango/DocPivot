using DocPivot.Core.Contracts;
using DocPivot.Core.Tables;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocPivot.PdfWorker;

public sealed class PdfTableExtractor
{
    private const int MaximumPages = 1000;
    private readonly GhostscriptPageRenderer _renderer;
    private readonly Func<CancellationToken, Task<IOcrProvider>> _ocrProviderFactory;
    private IOcrProvider? _ocrProvider;

    public PdfTableExtractor(
        GhostscriptPageRenderer renderer,
        Func<CancellationToken, Task<IOcrProvider>> ocrProviderFactory)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(ocrProviderFactory);
        _renderer = renderer;
        _ocrProviderFactory = ocrProviderFactory;
    }

    public async Task<DocumentTableExtraction> ExtractAsync(
        string inputPath,
        string workspacePath,
        WorkerPdfToExcelOptions options,
        Action<WorkerProgressMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(options);

        using var document = PdfDocument.Open(inputPath);
        if (document.NumberOfPages is < 1 or > MaximumPages)
        {
            throw new PdfTableWorkerException(
                "PDF_PAGE_LIMIT_EXCEEDED",
                $"PDF table extraction supports between 1 and {MaximumPages} pages.");
        }

        var pages = new List<DocumentTablePage>(document.NumberOfPages);
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(pageNumber);
            progress?.Invoke(WorkerProgressMessage.Create(
                Guid.Empty,
                "page-extracting",
                pageNumber - 1,
                document.NumberOfPages + 2));

            var pageWidth = (double)page.Width;
            var pageHeight = (double)page.Height;
            var content = PdfPageContentReader.Read(page);
            var useOcr = options.OcrMode switch
            {
                WorkerPdfOcrMode.ForceOcr => true,
                WorkerPdfOcrMode.DigitalTextOnly => false,
                _ => PdfPageTextQuality.Evaluate(pageWidth, pageHeight, content.Words, content.Images).RequiresOcr,
            };
            if (!useOcr)
            {
                pages.Add(TableLayoutAnalyzer.AnalyzePage(
                    pageNumber,
                    pageWidth,
                    pageHeight,
                    DocumentTableSourceKind.DigitalText,
                    content.Words,
                    content.Lines));
                continue;
            }

            var imagePath = Path.Combine(workspacePath, $"page-{pageNumber:D4}.png");
            progress?.Invoke(WorkerProgressMessage.Create(
                Guid.Empty,
                "ocr-rendering",
                pageNumber - 1,
                document.NumberOfPages + 2));
            var rendered = await _renderer.RenderAsync(
                    inputPath,
                    pageNumber,
                    pageWidth,
                    pageHeight,
                    imagePath,
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.Invoke(WorkerProgressMessage.Create(
                Guid.Empty,
                "ocr-recognizing",
                pageNumber - 1,
                document.NumberOfPages + 2));
            _ocrProvider ??= await _ocrProviderFactory(cancellationToken).ConfigureAwait(false);
            var ocrWords = await _ocrProvider.RecognizeAsync(
                    new OcrPageRequest(
                        pageNumber,
                        rendered.ImagePath,
                        rendered.PixelWidth,
                        rendered.PixelHeight,
                        rendered.Dpi,
                        options.OcrLanguage),
                    cancellationToken)
                .ConfigureAwait(false);
            var normalizedOcrWords = NormalizeOcrCoordinates(
                ocrWords,
                rendered.PixelWidth,
                rendered.PixelHeight,
                pageWidth,
                pageHeight);
            var raster = ReadRasterLines(rendered.ImagePath, pageWidth, pageHeight);
            // Scans draw their grid into the image. Vector rules are the fallback when the scan shows
            // none; they share the raster's frame only on unrotated pages.
            var lines = raster.Lines.Count > 0 || page.Rotation.Value != 0 ? raster.Lines : content.Lines;
            pages.Add(TableLayoutAnalyzer.AnalyzePage(
                pageNumber,
                pageWidth,
                pageHeight,
                DocumentTableSourceKind.OpticalCharacterRecognition,
                raster.Straighten(normalizedOcrWords, pageWidth, pageHeight),
                lines));
        }

        return new DocumentTableExtraction(
            Path.GetFileName(inputPath),
            document.NumberOfPages,
            pages);
    }

    private static RasterPageLines ReadRasterLines(string imagePath, double pageWidth, double pageHeight)
    {
        try
        {
            return RasterTableLineReader.Read(imagePath, pageWidth, pageHeight);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return RasterPageLines.Empty;
        }
    }

    private static DocumentWord[] NormalizeOcrCoordinates(
        IReadOnlyList<DocumentWord> words,
        int pixelWidth,
        int pixelHeight,
        double pageWidth,
        double pageHeight)
    {
        var horizontalScale = pageWidth / pixelWidth;
        var verticalScale = pageHeight / pixelHeight;
        return words
            .Select(word => word with
            {
                Bounds = new DocumentBounds(
                    word.Bounds.Left * horizontalScale,
                    word.Bounds.Top * verticalScale,
                    word.Bounds.Right * horizontalScale,
                    word.Bounds.Bottom * verticalScale),
            })
            .ToArray();
    }
}
