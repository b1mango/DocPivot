using DocPivot.Core.Tables;
using Tesseract;

namespace DocPivot.PdfWorker;

public sealed class TesseractOcrProvider : IOcrProvider
{
    private readonly TesseractEngine _engine;
    private bool _disposed;

    public TesseractOcrProvider(string tessdataDirectory, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tessdataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        _engine = new TesseractEngine(
            Path.GetFullPath(tessdataDirectory),
            language,
            EngineMode.LstmOnly)
        {
            DefaultPageSegMode = PageSegMode.SparseText,
        };
        _engine.SetVariable("preserve_interword_spaces", "1");
    }

    public Task<IReadOnlyList<DocumentWord>> RecognizeAsync(
        OcrPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var image = Pix.LoadFromFile(request.ImagePath);
        using var page = _engine.Process(image, PageSegMode.SparseText);
        using var iterator = page.GetIterator();
        var words = new List<DocumentWord>();
        iterator.Begin();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
            if (string.IsNullOrWhiteSpace(text) ||
                !iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
            {
                continue;
            }

            var confidence = iterator.GetConfidence(PageIteratorLevel.Word);
            if (confidence > 1)
            {
                confidence /= 100;
            }

            var documentBounds = new DocumentBounds(
                bounds.X1,
                bounds.Y1,
                bounds.X2,
                bounds.Y2);
            if (documentBounds.IsValid)
            {
                words.Add(new DocumentWord(
                    text,
                    documentBounds,
                    Math.Clamp(confidence, 0, 1)));
            }
        }
        while (iterator.Next(PageIteratorLevel.Word));

        return Task.FromResult<IReadOnlyList<DocumentWord>>(words);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.Dispose();
    }
}
