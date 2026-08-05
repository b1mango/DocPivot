using DocPivot.Core.Contracts;
using DocPivot.Core.Tables;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

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

            var digitalWords = GetDigitalWords(page);
            var useOcr = options.OcrMode == WorkerPdfOcrMode.ForceOcr ||
                options.OcrMode == WorkerPdfOcrMode.Automatic &&
                !HasUsableDigitalLayer(page, digitalWords);
            if (!useOcr)
            {
                pages.Add(TableLayoutAnalyzer.AnalyzePage(
                    pageNumber,
                    (double)page.Width,
                    (double)page.Height,
                    DocumentTableSourceKind.DigitalText,
                    digitalWords));
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
                    (double)page.Width,
                    (double)page.Height,
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
            pages.Add(TableLayoutAnalyzer.AnalyzePage(
                pageNumber,
                rendered.PixelWidth,
                rendered.PixelHeight,
                DocumentTableSourceKind.OpticalCharacterRecognition,
                ocrWords));
        }

        return new DocumentTableExtraction(
            Path.GetFileName(inputPath),
            document.NumberOfPages,
            pages);
    }

    private static DocumentWord[] GetDigitalWords(Page page)
    {
        var pageHeight = (double)page.Height;
        return NearestNeighbourWordExtractor.Instance
            .GetWords(page.Letters)
            .Where(static word => !string.IsNullOrWhiteSpace(word.Text))
            .Select(word =>
            {
                var bounds = word.BoundingBox;
                return new DocumentWord(
                    word.Text,
                    new DocumentBounds(
                        bounds.Left,
                        pageHeight - bounds.Top,
                        bounds.Right,
                        pageHeight - bounds.Bottom),
                    1);
            })
            .Where(static word => word.Bounds.IsValid)
            .ToArray();
    }

    private static bool HasUsableDigitalLayer(Page page, DocumentWord[] words)
    {
        var characters = words.Sum(static word => word.Text.Count(static value => !char.IsWhiteSpace(value)));
        if (words.Length < 4 || characters < 8)
        {
            return false;
        }

        return page.NumberOfImages == 0 || words.Length >= 12 || characters >= 40;
    }
}
