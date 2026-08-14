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
            var normalizedOcrWords = NormalizeOcrCoordinates(
                ocrWords,
                rendered.PixelWidth,
                rendered.PixelHeight,
                (double)page.Width,
                (double)page.Height);
            pages.Add(TableLayoutAnalyzer.AnalyzePage(
                pageNumber,
                (double)page.Width,
                (double)page.Height,
                DocumentTableSourceKind.OpticalCharacterRecognition,
                normalizedOcrWords));
        }

        return new DocumentTableExtraction(
            Path.GetFileName(inputPath),
            document.NumberOfPages,
            pages);
    }

    private static DocumentWord[] GetDigitalWords(Page page)
    {
        var pageHeight = (double)page.Height;
        var letters = page.Letters
            .Where(static letter => !string.IsNullOrWhiteSpace(letter.Value))
            .Select(letter =>
            {
                var bounds = letter.BoundingBox;
                return new DigitalLetter(
                    letter.Value,
                    new DocumentBounds(
                        bounds.Left,
                        pageHeight - bounds.Top,
                        bounds.Right,
                        pageHeight - bounds.Bottom));
            })
            .Where(static letter => letter.Bounds.IsValid)
            .OrderBy(static letter => letter.Bounds.CenterY)
            .ThenBy(static letter => letter.Bounds.Left)
            .ToArray();
        if (letters.Length == 0)
        {
            return [];
        }

        var medianHeight = Median(letters.Select(static letter => letter.Bounds.Height));
        var rows = new List<List<DigitalLetter>>();
        foreach (var letter in letters)
        {
            var row = rows.LastOrDefault(candidate =>
                Math.Abs(candidate[0].Bounds.CenterY - letter.Bounds.CenterY) <= medianHeight * 0.65);
            if (row is null)
            {
                rows.Add([letter]);
            }
            else
            {
                row.Add(letter);
            }
        }

        var words = new List<DocumentWord>();
        foreach (var row in rows.OrderBy(static row => row.Min(letter => letter.Bounds.Top)))
        {
            var ordered = row.OrderBy(static letter => letter.Bounds.Left).ToArray();
            var characterWidths = ordered
                .Select(static letter => letter.Bounds.Width)
                .Where(static width => width > 0)
                .ToArray();
            var medianCharacterWidth = Median(characterWidths);
            var gapThreshold = Math.Max(medianHeight * 0.85, medianCharacterWidth * 1.55);
            var segment = new List<DigitalLetter> { ordered[0] };
            for (var index = 1; index < ordered.Length; index++)
            {
                var gap = ordered[index].Bounds.Left - ordered[index - 1].Bounds.Right;
                if (gap > gapThreshold)
                {
                    words.Add(CreateDigitalWord(segment));
                    segment = [];
                }

                segment.Add(ordered[index]);
            }

            words.Add(CreateDigitalWord(segment));
        }

        return words.ToArray();

        DocumentWord CreateDigitalWord(IReadOnlyList<DigitalLetter> segment) =>
            new(
                string.Concat(segment.Select(static letter => letter.Text)),
                DocumentBounds.Union(segment.Select(static letter => letter.Bounds)),
                1);
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

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(static value => double.IsFinite(value) && value > 0).Order().ToArray();
        if (ordered.Length == 0)
        {
            return 1;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private sealed record DigitalLetter(string Text, DocumentBounds Bounds);

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
